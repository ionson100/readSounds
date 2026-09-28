using System.Buffers.Binary;

namespace readSounds.Services.Audio;

/// <summary>
/// Наращиваемый WAV-файл: принимает куски одинакового формата и склеивает их
/// простым дописыванием PCM. Никакого перекодирования — только конкатенация байтов.
///
/// Формат выходного файла берётся из первого куска, поэтому все куски обязаны совпадать
/// по формату. Позиция данных отслеживается в <see cref="DataBytes"/>; её достаточно
/// сохранить в состояние проекта, чтобы при следующем запуске продолжить запись
/// ровно с последней целой части.
/// </summary>
public sealed class WavConcatWriter : IDisposable
{
    /// <summary>"RIFF"+size+"WAVE"+"fmt "+size+fmt+"data"+size.</summary>
    private const int FixedHeaderBytes = 28;

    private const int BufferSize = 128 * 1024;

    private readonly FileStream _stream;
    private readonly byte[] _formatChunk;
    private readonly long _dataOffset;
    private long _dataBytes;
    private bool _disposed;

    private WavConcatWriter(FileStream stream, byte[] formatChunk, WavFormat format, long dataOffset, long dataBytes)
    {
        _stream = stream;
        _formatChunk = formatChunk;
        Format = format;
        _dataOffset = dataOffset;
        _dataBytes = dataBytes;
    }

    public WavFormat Format { get; }

    /// <summary>Сколько байт PCM уже записано.</summary>
    public long DataBytes => _dataBytes;

    /// <summary>Смещение PCM в выходном файле.</summary>
    public long DataOffset => _dataOffset;

    public TimeSpan Duration => Format.DurationFor(_dataBytes);

    /// <summary>
    /// Создаёт пустой файл с заголовком по образцу <paramref name="template"/>.
    /// </summary>
    /// <remarks>
    /// Звук из <paramref name="template"/> сюда <b>не</b> попадает — образец нужен только ради
    /// формата. Первую часть, как и все остальные, добавляют через <see cref="Append"/>.
    /// </remarks>
    public static WavConcatWriter CreateEmpty(string path, WavFile template)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(template);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.None);
        var writer = new WavConcatWriter(
            stream, template.FormatChunk, template.Format, FixedHeaderBytes + template.FormatChunk.Length, 0);
        writer.WriteHeader();
        return writer;
    }

    /// <summary>
    /// Продолжает запись в существующий файл. Хвост длиннее <paramref name="committedDataBytes"/>
    /// обрезается — так убирается возможный недописанный кусок после аварийного закрытия.
    /// </summary>
    public static WavConcatWriter Resume(string path, long committedDataBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Аудиофайл проекта не найден.", path);
        }

        byte[] formatChunk;
        WavFormat format;
        long dataOffset;
        long dataLength;

        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var info = WavReader.Read(probe);
            formatChunk = info.FormatChunk;
            format = info.Format;
            dataOffset = info.DataOffset;
            dataLength = info.DataLength;
        }

        if (dataLength < committedDataBytes)
        {
            throw new InvalidDataException(
                $"Аудиофайл короче, чем записано в состояние проекта " +
                $"({dataLength} байт при {committedDataBytes}). Начните синтез заново.");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, BufferSize, FileOptions.None);
        var target = dataOffset + committedDataBytes;
        stream.SetLength(target);
        stream.Position = target;

        return new WavConcatWriter(stream, formatChunk, format, dataOffset, committedDataBytes);
    }

    /// <summary>Дописывает кусок аудио. Формат должен совпадать с уже записанным.</summary>
    public void Append(Stream chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunk);

        // Позиция не важна: один и тот же поток может прийти и на разбор, и на склейку.
        chunk.Position = 0;
        var info = WavReader.Read(chunk);
        if (info.Format != Format)
        {
            throw new InvalidDataException(
                $"Сервер вернул кусок в формате {info.Format.Describe()}, " +
                $"а в проекте уже записано {Format.Describe()}. Перезапустите синтез с начала.");
        }

        chunk.Position = info.DataOffset;
        CopyExactly(chunk, _stream, info.DataLength);

        _dataBytes += info.DataLength;
        PatchHeader();
    }

    private void WriteHeader()
    {
        _stream.Position = 0;
        _stream.Write("RIFF"u8);
        WriteUInt32(0);
        _stream.Write("WAVE"u8);
        _stream.Write("fmt "u8);
        WriteUInt32((uint)_formatChunk.Length);
        _stream.Write(_formatChunk);
        _stream.Write("data"u8);
        WriteUInt32(0);
        PatchHeader();
    }

    /// <summary>Проставляет реальные размеры в RIFF и data, возвращая курсор в конец данных.</summary>
    private void PatchHeader()
    {
        Span<byte> buffer = stackalloc byte[4];

        _stream.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(_dataOffset + _dataBytes - 8));
        _stream.Write(buffer);

        _stream.Position = _dataOffset - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)_dataBytes);
        _stream.Write(buffer);

        _stream.Position = _dataOffset + _dataBytes;
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private static void CopyExactly(Stream source, Stream destination, long count)
    {
        var buffer = new byte[BufferSize];
        long remaining = count;

        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "Кусок аудио оборван: данных меньше, чем заявлено в заголовке.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            PatchHeader();
            _stream.Flush(true);
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
