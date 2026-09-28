using System.Text.Json.Serialization;

namespace readSounds.Models;

/// <summary>Неизменяемая часть описания проекта: как он появился и из какого файла.</summary>
public sealed class ProjectMeta
{
    public int Version { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Имя скопированного исходного текста внутри папки проекта.</summary>
    public string SourceFileName { get; set; } = string.Empty;

    public int SourceCharCount { get; set; }

    /// <summary>SHA-256 исходного текста. Нужен, чтобы поймать правку исходника.</summary>
    public string SourceSha256 { get; set; } = string.Empty;
}

/// <summary>Изменяемая часть: сколько частей уже озвучено и сколько всего.</summary>
public sealed class ProjectProgress
{
    public int TotalChunks { get; set; }

    public int CompletedChunks { get; set; }

    /// <summary>
    /// Размер уже записанного PCM в байтах. Хранится после каждой успешной части,
    /// поэтому при открытии проекта можно обрезать файл ровно на границе целой части.
    /// </summary>
    public long DataBytes { get; set; }

    /// <summary>
    /// Смещение конца каждой готовой части в байтах, по порядку.
    /// </summary>
    /// <remarks>
    /// Нужно, чтобы переозвучить одну часть: файл обрезается до смещения предыдущей
    /// части, и синтез продолжается с нужного места, а не с начала.
    /// </remarks>
    public List<long> ChunkEnds { get; set; } = [];

    public ProjectStatus Status { get; set; } = ProjectStatus.NotStarted;

    /// <summary>Отпечаток исходника и настроек на момент последнего синтеза.</summary>
    public string? Signature { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// Проект озвучивания. Лежит в своей папке:
/// <code>
///   Моя книга/
///     project.json        — настройки и прогресс
///     book.txt            — копия исходного текста
///     audio/result.wav    — результат, растёт по мере синтеза
/// </code>
/// </summary>
public sealed class ProjectModel
{
    public const string ManifestFileName = "project.json";
    public const string AudioFolderName = "audio";
    public const string AudioFileName = "result.wav";

    /// <summary>Папка проекта. Не сериализуется — восстанавливается при загрузке.</summary>
    [JsonIgnore]
    public string Directory { get; set; } = string.Empty;

    public ProjectMeta Meta { get; set; } = new();

    public ProjectOptions Options { get; set; } = new();

    public ProjectProgress Progress { get; set; } = new();

    [JsonIgnore]
    public string ManifestPath => Path.Combine(Directory, ManifestFileName);

    [JsonIgnore]
    public string SourcePath => Path.Combine(Directory, Meta.SourceFileName);

    [JsonIgnore]
    public string AudioFolderPath => Path.Combine(Directory, AudioFolderName);

    [JsonIgnore]
    public string AudioPath => Path.Combine(AudioFolderPath, AudioFileName);

    [JsonIgnore]
    public double ProgressFraction => Progress.TotalChunks <= 0
        ? 0d
        : Math.Clamp((double)Progress.CompletedChunks / Progress.TotalChunks, 0d, 1d);

    [JsonIgnore]
    public string ProgressText => Progress.TotalChunks <= 0
        ? "Нет данных"
        : $"Часть {Progress.CompletedChunks} из {Progress.TotalChunks} " +
          $"({ProgressFraction * 100:0} %)";

    [JsonIgnore]
    public bool CanResume => Progress.CompletedChunks > 0 && Progress.CompletedChunks < Progress.TotalChunks;

    [JsonIgnore]
    public bool IsCompleted => Progress.Status == ProjectStatus.Completed;
}
