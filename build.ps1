<#
.SYNOPSIS
    Сборка самостоятельных дистрибутивов readSounds: EXE для Windows и .deb для Linux.

.DESCRIPTION
    Собирает приложение в двух видах, ни в одном из которых не требуется
    установленный .NET — рантайм попадает в выдачу вместе с программой
    (self-contained).

      * Windows x64 — один файл readSounds.exe.
      * Linux x64   — пакет readsounds_<версия>_amd64.deb, приложение
                      уезжает в /opt/readSounds, запускалка в /usr/bin.

    Сборка ведётся кроссплатформенно: RID linux-x64 публикуется с Windows
    без участия Docker и без установленного Linux.

    .deb собирается без dpkg-deb: если команда есть в системе, используется она,
    иначе пакет склеивается вручную из control.tar.gz и data.tar.gz
    (контейнер формата ar). Поэтому скрипт работает на «голом» Windows.

.PARAMETER Target
    all (по умолчанию), windows или linux.

.PARAMETER Version
    Версия, попадающая в сборку и в имя .deb.

.PARAMETER Configuration
    Release по умолчанию.

.PARAMETER OutputDirectory
    Куда складывать готовые файлы, по умолчанию ./artifacts.

.PARAMETER WindowsSingleFile
    Упаковать Windows-сборку в один .exe. По умолчанию включено;
    ключ -WindowsSingleFile:$false оставляет обычную папку.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Target linux -Version 1.2.0
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'windows', 'linux')]
    [string] $Target = 'all',

    [string] $Version = '1.0.0',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $OutputDirectory = 'artifacts',

    [switch] $WindowsSingleFile = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# GDI+ нужен для значка: чтение размера sound.png и сборка .ico из него.
# Скрипт рассчитан на Windows, поэтому System.Drawing доступен.
Add-Type -AssemblyName System.Drawing

# Режим вывода .NET, чтобы читалась и русская локализация ошибок.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'readSounds.csproj'
$Artifacts = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $ProjectRoot $OutputDirectory
}

# Один исходник значка на обе платформы: sound.png.
# В Windows он превращается в .ico для EXE, в Linux уходит в пакет как есть.
$SourceIcon = Join-Path $ProjectRoot 'sound.png'
$WindowsIconPath = Join-Path $ProjectRoot 'Assets\sound.ico'

$PackageName = 'readsounds'
$AppTitle = 'readSounds'
$LinuxInstallDir = '/opt/readSounds'
$BinaryName = 'readSounds'

# Список общих библиотек, без которых X11-приложение Avalonia не стартует.
$DebianDepends = 'libc6, libx11-6, libice6, libsm6, libfontconfig1, libgl1, libxext6, libxrender1, ca-certificates'

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([string] $Message)
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Invoke-External {
    <#
      Запускает внешнюю команду и падает, если она вернула ненулевой код.
      $AllowFailure — когда ненулевой код допустим (тогда вернётся $LASTEXITCODE).
    #>
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [string[]] $Arguments = @(),
        [switch] $AllowFailure,
        [string] $WorkingDirectory
    )

    Write-Detail "$FilePath $($Arguments -join ' ')"

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($WorkingDirectory) {
            Push-Location $WorkingDirectory
        }
        try {
            & $FilePath @Arguments
            $code = $LASTEXITCODE
        } finally {
            if ($WorkingDirectory) {
                Pop-Location
            }
        }
    } finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0 -and -not $AllowFailure) {
        throw "Команда завершилась с кодом $code : $FilePath $($Arguments -join ' ')"
    }

    return $code
}

function Test-Command {
    param([string] $Name)
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Reset-Directory {
    param([string] $Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path -LiteralPath $Path).Path
}

function Write-Utf8NoBom {
    <#
      Файлы в .deb обязаны быть UTF-8 без BOM и с переводами строк LF.

      -TrailingNewline обязателен для control и .desktop: Debian требует, чтобы
      последнее поле control заканчивалось переводом строки, иначе dpkg
      отвечает «в значении поля Description обнаружен конец файла».
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Content,
        [switch] $TrailingNewline
    )

    $normalized = $Content -replace "`r`n", "`n" -replace "`r", "`n"
    if ($TrailingNewline -and -not $normalized.EndsWith("`n")) {
        $normalized += "`n"
    }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $normalized, $encoding)
}

function Get-DirectorySizeKb {
    param([string] $Path)
    $bytes = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    return [math]::Ceiling($bytes / 1kb)
}

#region Значок приложения

function ConvertTo-IcoImage {
    <#
      Один кадр .ico в виде 32-битного DIB: BITMAPINFOHEADER, пиксели снизу
      вверх (BGRA) и прозрачная маска AND.

      Windows принимает и PNG внутри .ico, но DIB понятен всем, включая
      старые оболочки и просмотрщики, поэтому пишем его.
    #>
    param([Parameter(Mandatory = $true)][System.Drawing.Bitmap] $Bitmap)

    $size = $Bitmap.Width

    $data = $Bitmap.LockBits(
        (New-Object System.Drawing.Rectangle(0, 0, $size, $size)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = New-Object byte[] ($size * $size * 4)
        $raw = New-Object byte[] ([math]::Abs($data.Stride) * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)

        # В DIB строки идут снизу вверх, а LockBits отдаёт сверху вниз.
        for ($y = 0; $y -lt $size; $y++) {
            $from = ($size - 1 - $y) * $data.Stride
            [Array]::Copy($raw, $from, $pixels, $y * $size * 4, $size * 4)
        }
    } finally {
        $Bitmap.UnlockBits($data)
    }

    # Маска AND: бит на пиксель, ширина строки кратна 4 байтам.
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $size)

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($out)

    $writer.Write([uint32]40)          # biSize
    $writer.Write([int32]$size)        # biWidth
    $writer.Write([int32]($size * 2))  # biHeight: картинка плюс маска
    $writer.Write([uint16]1)           # biPlanes
    $writer.Write([uint16]32)          # biBitCount
    $writer.Write([uint32]0)           # biCompression: без сжатия
    $writer.Write([uint32]($pixels.Length + $mask.Length))
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)

    $writer.Write($pixels, 0, $pixels.Length)
    $writer.Write($mask, 0, $mask.Length)

    $writer.Flush()
    $result = $out.ToArray()
    $writer.Dispose()
    $out.Dispose()
    return $result
}

function New-IcoFromPng {
    <#
      Собирает .ico из одного PNG. Windows показывает значок в проводнике,
      панели задач и списке программ, и для каждого размера нужен свой кадр,
      поэтому в файл кладутся 7 размеров.

      Исходник — sound.png в корне проекта, он же уходит в пакет .deb.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $SourcePng,
        [Parameter(Mandatory = $true)][string] $OutputIco,
        [int[]] $Sizes = @(16, 24, 32, 48, 64, 128, 256)
    )

    if (-not (Test-Path -LiteralPath $SourcePng)) {
        throw "Не найден исходник значка: $SourcePng"
    }

    $source = [System.Drawing.Image]::FromFile($SourcePng)
    try {
        $frames = New-Object System.Collections.Generic.List[byte[]]

        foreach ($size in $Sizes) {
            $canvas = New-Object System.Drawing.Bitmap(
                $size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($canvas)
                try {
                    $graphics.CompositingMode =
                        [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                    $graphics.InterpolationMode =
                        [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode =
                        [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.SmoothingMode =
                        [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.DrawImage($source, 0, 0, $size, $size)
                } finally {
                    $graphics.Dispose()
                }

                $frames.Add((ConvertTo-IcoImage -Bitmap $canvas))
            } finally {
                $canvas.Dispose()
            }
        }
    } finally {
        $source.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($out)

    $writer.Write([uint16]0)              # зарезервировано
    $writer.Write([uint16]1)              # тип 1 — иконка
    $writer.Write([uint16]$frames.Count)

    # Смещения известны заранее: заголовок плюс по 16 байт на кадр.
    $offset = 6 + (16 * $frames.Count)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        # В заголовке .ico размер 256 записывается нулём.
        $dimension = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }

        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)           # палитра не используется
        $writer.Write([byte]0)
        $writer.Write([uint16]1)         # плоскости
        $writer.Write([uint16]32)        # бит на пиксель
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)

        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame, 0, $frame.Length)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputIco, $out.ToArray())
    $writer.Dispose()
    $out.Dispose()
}

#endregion

function New-TarArchive {
    <#
      Собирает архив tar (ustar) прямо в памяти с заданными правами.

      Зачем свой писатель: tar из Windows идёт через libarchive, который берёт
      права из файловой системы. В Windows их не существует, и всё раскладывается
      как 0666. Для control.tar это значит неисполняемый postinst, а для
      data.tar — неисполняемое приложение и библиотеки. Раз rights задаются
      только на Linux-машине, в Windows их выставить нечем, поэтому упаковка
      делается здесь, с точными правами на каждый файл.
    #>
    param(
        [Parameter(Mandatory = $true)][object[]] $Entries
    )

    $output = New-Object System.IO.MemoryStream

    # Время изменения: число секунд с начала эпохи, в восьмеричной записи.
    # В поле tar помещается 11 восьмеричных цифр, то есть примерно год 4000.
    # Если часы машины уехали дальше, время усекается: в .deb это косметика,
    # на установку и работу пакета mtime не влияет.
    $stamp = [Convert]::ToString([DateTimeOffset]::UtcNow.ToUnixTimeSeconds(), 8)
    if ($stamp.Length -gt 11) {
        $stamp = '0'
    }

    foreach ($entry in $Entries) {
        $data = $entry.Data
        $name = $entry.Name
        # Права: 0644 для control, 0755 для скриптов установки.
        $mode = if ($entry.PSObject.Properties.Name -contains 'Mode') { $entry.Mode } else { '644' }

        if ($name.Length -gt 99) {
            throw "Имя файла в tar длиннее 100 байт: $name"
        }

        $header = New-Object byte[] 512
        for ($i = 0; $i -lt 512; $i++) { $header[$i] = 0 }

        # name, 100 байт
        $nameBytes = [System.Text.Encoding]::ASCII.GetBytes($name)
        [Array]::Copy($nameBytes, 0, $header, 0, $nameBytes.Length)

        # Числовые поля заголовка: восьмеричные цифры и нули СЛЕВА.
        Write-Octal -Header $header -Offset 100 -Width 8 -Value $mode
        Write-Octal -Header $header -Offset 108 -Width 8 -Value '0'
        Write-Octal -Header $header -Offset 116 -Width 8 -Value '0'
        Write-Octal -Header $header -Offset 124 -Width 12 -Value ([Convert]::ToString($data.Length, 8))
        Write-Octal -Header $header -Offset 136 -Width 12 -Value $stamp

        # chksum, 8 байт: пока считаем как восемь пробелов
        for ($i = 148; $i -lt 156; $i++) { $header[$i] = 32 }

        $header[156] = 48      # typeflag '0' — обычный файл
        $magic = [System.Text.Encoding]::ASCII.GetBytes("ustar`0")
        [Array]::Copy($magic, 0, $header, 257, 6)
        $version = [System.Text.Encoding]::ASCII.GetBytes('00')
        [Array]::Copy($version, 0, $header, 263, 2)

        $checksum = 0
        foreach ($b in $header) { $checksum += $b }

        # Контрольная сумма: шесть цифр, нуль, пробел
        $sumText = [Convert]::ToString($checksum, 8).PadLeft(6, '0')
        $sumBytes = [System.Text.Encoding]::ASCII.GetBytes($sumText)
        [Array]::Copy($sumBytes, 0, $header, 148, 6)
        $header[154] = 0
        $header[155] = 32

        $output.Write($header, 0, 512)
        $output.Write($data, 0, $data.Length)

        # Данные дополняются нулями до границы 512 байт.
        $remainder = $data.Length % 512
        if ($remainder -ne 0) {
            $output.Write((New-Object byte[] (512 - $remainder)), 0, 512 - $remainder)
        }
    }

    # Архив завершается двумя пустыми блоками по 512 байт.
    $output.Write((New-Object byte[] 1024), 0, 1024)

    $result = $output.ToArray()
    $output.Dispose()
    return $result
}

function Write-Octal {
    <#
      Записывает поле заголовка tar фиксированной ширины.

      $Value — уже готовые восьмеричные цифры без ведущих нулей, например
      '644' или '12'. Нули дописываются СЛЕВА: в tar числовые поля выравниваются
      по левому краю, и дописывание справа превратило бы размер 10 байт
      в 134217728.
    #>
    param(
        [Parameter(Mandatory = $true)][byte[]] $Header,
        [Parameter(Mandatory = $true)][int] $Offset,
        [Parameter(Mandatory = $true)][int] $Width,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value
    )

    if ($Value.Length -gt ($Width - 1)) {
        throw "Восьмеричное значение '$Value' не помещается в поле tar шириной $Width"
    }

    $text = $Value.PadLeft($Width - 1, '0') + ' '
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($text)
    [Array]::Copy($bytes, 0, $Header, $Offset, $Width)
}

function Compress-Gzip {
    param([byte[]] $Bytes)

    $memory = New-Object System.IO.MemoryStream
    $gzip = New-Object System.IO.Compression.GZipStream(
        $memory, [System.IO.Compression.CompressionMode]::Compress, $true)
    $gzip.Write($Bytes, 0, $Bytes.Length)
    $gzip.Dispose()

    $result = $memory.ToArray()
    $memory.Dispose()
    return $result
}


#region Контейнер ar для .deb

function Format-ArField {
    <#
      Приводит значение к полю ar фиксированной ширины.
      PadLeft/PadRight не обрезают слишком длинное значение, а лишний символ
      сдвигает все последующие поля заголовка и портит весь пакет. Поэтому
      длина проверяется явно.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][int] $Width,
        [ValidateSet('Left', 'Right')][string] $Align = 'Left'
    )

    if ($Value.Length -gt $Width) {
        throw "Значение '$Value' не помещается в поле ar шириной $Width"
    }

    if ($Align -eq 'Right') { return $Value.PadLeft($Width) }
    return $Value.PadRight($Width)
}

function New-ArArchive {
    <#
      Собирает контейнер формата ar из готовых файлов-членов.
      .deb — это именно ar-архив с тремя членами: debian-binary,
      control.tar.* и data.tar.*.

      Заголовок члена занимает ровно 60 байт:
        0..15  имя      16..27  время     28..33  uid
       34..39  gid      40..47  права     48..57  размер
       58..59  "`" и перевод строки
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object[]] $Members
    )

    $stamp = Get-Date -Format 'yyMMddHHmmss'   # ровно 12 символов
    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($output)
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("!<arch>`n"))

    foreach ($member in $Members) {
        $header = -join @(
            (Format-ArField -Value $member.Name -Width 16)
            (Format-ArField -Value $stamp -Width 12 -Align Right)
            (Format-ArField -Value '0' -Width 6 -Align Right)
            (Format-ArField -Value '0' -Width 6 -Align Right)
            (Format-ArField -Value '100644' -Width 8 -Align Right)
            (Format-ArField -Value ([string]$member.Length) -Width 10 -Align Right)
            ([char]0x60).ToString()
            "`n"
        )

        $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
        if ($headerBytes.Length -ne 60) {
            throw "Заголовок члена '$($member.Name)' получился длиной $($headerBytes.Length) вместо 60"
        }

        $writer.Write($headerBytes, 0, $headerBytes.Length)
        $writer.Write($member.Data, 0, $member.Length)

        # Члены выравниваются по чётной границе; непарный добивается переводом строки.
        if ($member.Length % 2 -eq 1) {
            $writer.Write([byte]0x0A)
        }
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Path, $output.ToArray())
    $writer.Dispose()
    $output.Dispose()
}

#endregion

#region Сборка

function Remove-SymbolFiles {
    <#
      Убирает .pdb рядом с нативными библиотеками (Skia, HarfBuzz): они приезжают
      из пакетов и весят около 100 МБ. Стеки-trace нашего кода не пострадают —
      DebugType=embedded, он уже внутри сборки.
    #>
    param([Parameter(Mandatory = $true)][string] $Path)

    $symbols = Get-ChildItem -LiteralPath $Path -Filter '*.pdb' -File -ErrorAction SilentlyContinue
    if ($symbols) {
        $total = ($symbols | Measure-Object -Property Length -Sum).Sum
        $symbols | Remove-Item -Force
        Write-Detail ("удалено .pdb: {0} шт., {1} МБ" -f $symbols.Count, [math]::Round($total / 1mb, 1))
    }
}

function New-WindowsIcon {
    <#
      Готовит Assets\sound.ico для Windows-сборки. Файл кладут в проект
      через ApplicationIcon в csproj, и он должен существовать до
      dotnet publish. Пересобирается, только если sound.png свежее.
    #>
    param(
        [Parameter(Mandatory = $true)][string] $SourcePng,
        [Parameter(Mandatory = $true)][string] $OutputIco
    )

    if (-not (Test-Path -LiteralPath $SourcePng)) {
        throw "Не найден sound.png — без него Windows-сборка не соберётся. Путь: $SourcePng"
    }

    if ((Test-Path -LiteralPath $OutputIco) -and
        (Get-Item -LiteralPath $OutputIco).LastWriteTimeUtc -ge
        (Get-Item -LiteralPath $SourcePng).LastWriteTimeUtc) {
        Write-Detail 'значок .ico уже актуален, пропускаем'
        return
    }

    $directory = Split-Path -Parent $OutputIco
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    New-IcoFromPng -SourcePng $SourcePng -OutputIco $OutputIco
    $size = [math]::Round((Get-Item -LiteralPath $OutputIco).Length / 1kb, 1)
    Write-Detail "значок .ico собран из sound.png, $size КБ"
}

function Publish-Windows {
    Write-Step 'Сборка Windows x64 (self-contained)'

    # Значок нужен до публикации: csproj ссылается на него через ApplicationIcon.
    New-WindowsIcon -SourcePng $SourceIcon -OutputIco $WindowsIconPath

    $outDir = Reset-Directory (Join-Path $Artifacts 'win-x64')

    $arguments = @(
        'publish', $ProjectFile,
        '-c', $Configuration,
        '-f', 'net9.0-windows',
        '-r', 'win-x64',
        '--self-contained', 'true',
        ('-p:Version=' + $Version),
        '-o', $outDir,
        '--nologo',
        '-v', 'q'
    )

    if ($WindowsSingleFile) {
        # Нативные библиотеки Avalonia (Skia, HarfBuzz) иначе остались бы рядом
        # отдельными файлами, и «один exe» перестал бы быть одним файлом.
        $arguments += '-p:PublishSingleFile=true'
        $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
    }

    Invoke-External -FilePath 'dotnet' -Arguments $arguments | Out-Null
    Remove-SymbolFiles -Path $outDir

    $exe = Join-Path $outDir "$BinaryName.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "EXE не найден после публикации: $exe"
    }

    $exeInfo = Get-Item -LiteralPath $exe
    Write-Detail "$($exeInfo.Name) — $([math]::Round($exeInfo.Length / 1mb, 1)) МБ"
    return $outDir
}

function Publish-Linux {
    Write-Step 'Сборка Linux x64 (self-contained)'

    $outDir = Reset-Directory (Join-Path $Artifacts 'linux-x64')

    Invoke-External -FilePath 'dotnet' -Arguments @(
        'publish', $ProjectFile,
        '-c', $Configuration,
        '-f', 'net9.0',
        '-r', 'linux-x64',
        '--self-contained', 'true',
        ('-p:Version=' + $Version),
        '-o', $outDir,
        '--nologo',
        '-v', 'q'
    ) | Out-Null
    Remove-SymbolFiles -Path $outDir

    $binary = Join-Path $outDir $BinaryName
    if (-not (Test-Path -LiteralPath $binary)) {
        throw "Исполняемый файл не найден после публикации: $binary"
    }

    Write-Detail "$BinaryName — $([math]::Round((Get-Item -LiteralPath $binary).Length / 1mb, 1)) МБ"
    return $outDir
}

function New-DebianPackage {
    param([Parameter(Mandatory = $true)][string] $PublishDirectory)

    Write-Step 'Сборка пакета .deb'

    if (-not (Test-Command 'tar')) {
        throw 'Не найдена команда tar. На Windows она входит в состав системы (C:\Windows\System32\tar.exe).'
    }

    $staging = Reset-Directory (Join-Path $Artifacts 'deb-staging')
    $work = Reset-Directory (Join-Path $Artifacts 'deb-work')

    # --- Раскладка файлов ---
    $targetRoot = Join-Path $staging 'opt/readSounds'.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $targetRoot -Recurse -Force

    # Права на файлы здесь не выставляются: в Windows их негде хранить, и tar
    # проставил бы всем 0664. Правильные права выдаёт postinst при установке,
    # а для control.tar они заданы явно — там скриптам нужен бит исполнения.
    $appsDir = Join-Path $staging ('usr/share/applications')
    New-Item -ItemType Directory -Path $appsDir -Force | Out-Null

    Write-Utf8NoBom -Path (Join-Path $appsDir 'readSounds.desktop') -TrailingNewline -Content @"
[Desktop Entry]
Type=Application
Version=1.0
Name=$AppTitle
GenericName=Озвучивание текста
Comment=Превращает текстовые файлы в речь через XTTS v2
Exec=$BinaryName
Icon=$PackageName
Terminal=false
Categories=AudioVideo;Audio;Speech;
Keywords=tts;speech;voice;text;озвучка;
StartupWMClass=$BinaryName
"@

    # Значок берём из sound.png как есть. Каталог hicolor обязан совпадать
    # с размером файла, иначе рабочий стол либо не найдёт значок, либо
    # растянет чужой, поэтому размер читаем, а не задаём.
    if (-not (Test-Path -LiteralPath $SourceIcon)) {
        throw "Не найден sound.png — без него пакет не соберётся. Путь: $SourceIcon"
    }

    # Изображение держит файл открытым, пока не освобождён, поэтому размер
    # читаем в отдельном блоке: незакрытый дескриптор потом мешает пересборке.
    $iconSize = 0
    $probe = [System.Drawing.Image]::FromFile($SourceIcon)
    try {
        $iconSize = $probe.Width
    } finally {
        $probe.Dispose()
    }

    $iconDir = Join-Path $staging "usr/share/icons/hicolor/$($iconSize)x$($iconSize)/apps"
    New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
    Copy-Item -LiteralPath $SourceIcon -Destination (Join-Path $iconDir "$PackageName.png") -Force

    $iconFile = Get-Item -LiteralPath (Join-Path $iconDir "$PackageName.png")
    Write-Detail "значок $($iconSize)x$($iconSize) из sound.png, $([math]::Round($iconFile.Length / 1kb, 1)) КБ"

    # --- control ---
    $controlDir = Join-Path $staging 'DEBIAN'
    New-Item -ItemType Directory -Path $controlDir -Force | Out-Null

    $installedSize = Get-DirectorySizeKb -Path $targetRoot

    Write-Utf8NoBom -Path (Join-Path $controlDir 'control') -TrailingNewline -Content @"
Package: $PackageName
Version: $Version
Section: sound
Priority: optional
Architecture: amd64
Depends: $DebianDepends
Installed-Size: $installedSize
Maintainer: readSounds <root@localhost>
Description: Озвучивание текстовых файлов через XTTS v2
 Приложение разрезает текстовый файл на части, отправляет их на
 самостоятельно поднятый сервер XTTS v2 и склеивает полученные WAV
 в один файл. Синтез можно прервать и продолжить позже.
"@

    # Симлинк в /usr/bin создаётся при установке: на Windows создать его нельзя,
    # а класть симлинк в data.tar нечем без исходной системы.
    Write-Utf8NoBom -Path (Join-Path $controlDir 'postinst') -Content @"
#!/bin/sh
set -e

ln -sf $LinuxInstallDir/$BinaryName /usr/bin/$BinaryName

# Права, записанные в data.tar, не выдерживают переноса с Windows: tar оттуда
# проставляет 0664 всем файлам. Здесь выправляем их на месте, иначе ни
# приложение, ни нативные библиотеки не будут запускаться.
find $LinuxInstallDir -type d -exec chmod 755 {} +
find $LinuxInstallDir -type f -exec chmod 644 {} +
find $LinuxInstallDir -name '*.so' -exec chmod 755 {} +
chmod 755 $LinuxInstallDir/$BinaryName
chmod 644 /usr/share/applications/readSounds.desktop
chmod 644 /usr/share/icons/hicolor/512x512/apps/$PackageName.png

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
exit 0
"@

    Write-Utf8NoBom -Path (Join-Path $controlDir 'prerm') -Content @"
#!/bin/sh
set -e
rm -f /usr/bin/$BinaryName
exit 0
"@

    $packagePath = Join-Path $Artifacts "$PackageName`_$Version`_amd64.deb"

    # --- Упаковка ---
    if (Test-Command 'dpkg-deb') {
        Write-Detail 'упаковка через dpkg-deb'
        Invoke-External -FilePath 'dpkg-deb' -Arguments @(
            '--build', '-Zzstd', $staging, $packagePath
        ) | Out-Null
    } else {
        Write-Detail 'dpkg-deb не найден, пакет склеивается вручную (ar + tar.gz)'

        $dataArchive = Join-Path $work 'data.tar.gz'
        $controlArchive = Join-Path $work 'control.tar.gz'

        # Убираем DEBIAN из data: метаданные обязаны лежать только в control,
        # поэтому содержимое data собираем в отдельный корень.
        $stagingData = Join-Path $work 'data-root'
        New-Item -ItemType Directory -Path $stagingData -Force | Out-Null
        foreach ($entry in Get-ChildItem -LiteralPath $staging -Force) {
            if ($entry.Name -ne 'DEBIAN') {
                Copy-Item -LiteralPath $entry.FullName -Destination $stagingData -Recurse -Force
            }
        }

        # --owner/--group нужны, чтобы файлы принадлежали root, а не текущему
        # пользователю: в Windows uid и gid выставить иначе нельзя.
        Invoke-External -FilePath 'tar' -Arguments @(
            '-czf', $dataArchive,
            '--owner=0', '--group=0', '--numeric-owner',
            '-C', $stagingData, '.'
        ) | Out-Null

        # control.tar собирается собственным писателем: postinst и prerm обязаны
        # попасть в пакет исполняемыми, а tar из Windows ставит всем 0664.
        # Разложить содержимое DEBIAN нужно в порядке control, postinst, prerm.
        $controlEntries = @()
        foreach ($name in 'control', 'postinst', 'prerm') {
            $mode = if ($name -eq 'control') { '644' } else { '755' }
            $controlEntries += [pscustomobject]@{
                Name = ('./' + $name)
                Mode = $mode
                Data = [System.IO.File]::ReadAllBytes((Join-Path $controlDir $name))
            }
        }

        $controlTarGz = Compress-Gzip -Bytes (New-TarArchive -Entries $controlEntries)
        [System.IO.File]::WriteAllBytes($controlArchive, $controlTarGz)
        Write-Detail "control.tar.gz собран вручную, $((Get-Item -LiteralPath $controlArchive).Length) байт"

        $members = @(
            [pscustomobject]@{
                Name   = 'debian-binary'
                Length = 4
                Data   = [System.Text.Encoding]::ASCII.GetBytes("2.0`n")
            },
            [pscustomobject]@{
                Name   = 'control.tar.gz'
                Length = $controlTarGz.Length
                Data   = $controlTarGz
            },
            [pscustomobject]@{
                Name   = 'data.tar.gz'
                Length = (Get-Item -LiteralPath $dataArchive).Length
                Data   = [System.IO.File]::ReadAllBytes($dataArchive)
            }
        )

        New-ArArchive -Path $packagePath -Members $members
    }

    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "Пакет не собран: $packagePath"
    }

    $packageInfo = Get-Item -LiteralPath $packagePath
    Write-Detail "$($packageInfo.Name) — $([math]::Round($packageInfo.Length / 1mb, 1)) МБ"
    return $packagePath
}

#endregion

# ===========================================================================

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Не найден файл проекта: $ProjectFile"
}

New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null

Write-Host ''
Write-Host "$AppTitle $Version — сборка ($Configuration)" -ForegroundColor White
Write-Host "    цель: $Target, .NET внутри: $true" -ForegroundColor DarkGray

$windowsDir = $null
$linuxDir = $null

if ($Target -eq 'all' -or $Target -eq 'windows') {
    $windowsDir = Publish-Windows
}

if ($Target -eq 'all' -or $Target -eq 'linux') {
    $linuxDir = Publish-Linux
    $package = New-DebianPackage -PublishDirectory $linuxDir
}

Write-Step 'Готово'
if ($windowsDir) {
    Write-Host "    Windows: $windowsDir\$BinaryName.exe" -ForegroundColor Green
}
if ($linuxDir) {
    Write-Host "    Linux:   $package" -ForegroundColor Green
}
Write-Host ''
Write-Host '    .NET на машине пользователя не требуется: обе сборки самодостаточные.' -ForegroundColor DarkGray
Write-Host ''