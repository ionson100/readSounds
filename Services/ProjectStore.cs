using System.Text.Json;
using readSounds.Models;

namespace readSounds.Services;

/// <summary>Создание, чтение и сохранение проектов озвучивания.</summary>
public interface IProjectStore
{
    IReadOnlyList<ProjectModel> LoadAll();

    ProjectModel Load(string directory);

    /// <summary>Создаёт папку проекта, копирует туда исходный текст и сохраняет настройки.</summary>
    ProjectModel Create(string sourceFilePath, string name, ProjectOptions options);

    void Save(ProjectModel project);

    void Delete(ProjectModel project);
}

/// <summary>
/// Проекты хранятся как обычные папки, каждая самодостаточна:
/// <code>
///   Корень/
///     Моя книга/
///       project.json     — язык, голос, порог длины части, прогресс
///       book.txt         — копия исходника, синтез всегда идёт по ней
///       audio/result.wav — результат
/// </code>
/// Благодаря копии исходника проект переживает перемещение папки и правку оригинала.
/// </summary>
public sealed class ProjectStore(IAppSettingsProvider settings) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public IReadOnlyList<ProjectModel> LoadAll()
    {
        var root = settings.Current.ProjectsRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var projects = new List<ProjectModel>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(directory, ProjectModel.ManifestFileName)))
            {
                continue;
            }

            try
            {
                projects.Add(Load(directory));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Битый проект не должен мешать списку остальных.
            }
        }

        return projects
            .OrderByDescending(p => p.Meta.UpdatedUtc)
            .ToList();
    }

    public ProjectModel Load(string directory)
    {
        var json = File.ReadAllText(Path.Combine(directory, ProjectModel.ManifestFileName));
        var project = JsonSerializer.Deserialize<ProjectModel>(json, JsonOptions)
            ?? throw new InvalidDataException($"Проект в папке «{directory}» пуст.");

        project.Directory = directory;
        project.Meta ??= new ProjectMeta();
        project.Options ??= new ProjectOptions();
        project.Progress ??= new ProjectProgress();
        return project;
    }

    public ProjectModel Create(string sourceFilePath, string name, ProjectOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Исходный текст не найден.", sourceFilePath);
        }

        var root = settings.Current.ProjectsRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Не задана папка для проектов.");
        }

        var directory = ResolveUniqueDirectory(root, name);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, ProjectModel.AudioFolderName));

        var sourceFileName = Path.GetFileName(sourceFilePath);
        File.Copy(sourceFilePath, Path.Combine(directory, sourceFileName), overwrite: true);

        // Читаем копию, а не оригинал: синтез должен опираться на то, что лежит в проекте.
        var source = TextFileReader.Read(Path.Combine(directory, sourceFileName));

        var project = new ProjectModel
        {
            Directory = directory,
            Meta = new ProjectMeta
            {
                Name = name,
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                SourceFileName = sourceFileName,
                SourceCharCount = source.CharCount,
                SourceSha256 = source.Sha256,
            },
            Options = options.Clone(),
        };

        Save(project);
        return project;
    }

    public void Save(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);

        project.Meta ??= new ProjectMeta();
        project.Progress ??= new ProjectProgress();
        project.Meta.UpdatedUtc = DateTimeOffset.UtcNow;

        var directory = project.Directory;
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("У проекта не задан путь к папке.");
        }

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, ProjectModel.ManifestFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, JsonOptions));

        // Подмена файла целиком: читающий проект никогда не увидит наполовину записанный JSON.
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.IsNullOrEmpty(project.Directory) && Directory.Exists(project.Directory))
        {
            Directory.Delete(project.Directory, recursive: true);
        }
    }

    private static string ResolveUniqueDirectory(string root, string name)
    {
        var safeName = SanitizeName(name);
        var candidate = Path.Combine(root, safeName);
        var suffix = 2;

        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{safeName} ({suffix++})");
        }

        return candidate;
    }

    private static string SanitizeName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Проект" : name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. trimmed.Select(c => invalid.Contains(c) ? '_' : c)]);
        return cleaned.Length == 0 ? "Проект" : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}
