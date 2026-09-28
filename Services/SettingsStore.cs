using System.Text.Json;
using readSounds.Models;

namespace readSounds.Services;

/// <summary>Текущие настройки приложения с возможностью подписки на изменения.</summary>
public interface IAppSettingsProvider
{
    AppSettings Current { get; }

    /// <summary>Изменилось значение, влияющее на сетевые запросы (адрес сервера, таймаут).</summary>
    event EventHandler? ServerChanged;

    void Save(AppSettings settings);
}

/// <summary>
/// Настройки приложения. В Windows это <c>%APPDATA%\readSounds\settings.json</c>,
/// в Linux — <c>$XDG_CONFIG_HOME/readSounds/settings.json</c> (обычно
/// <c>~/.config/readSounds/settings.json</c>).
/// </summary>
public sealed class SettingsStore : IAppSettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _settings;

    public SettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "readSounds",
            "settings.json"))
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
        _settings = Load(path);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public event EventHandler? ServerChanged;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var previous = Current;
        lock (_gate)
        {
            _settings = settings;
            WriteAtomically(_path, settings);
        }

        if (!string.Equals(previous.ServerUrl, settings.ServerUrl, StringComparison.Ordinal)
            || previous.RequestTimeoutSeconds != settings.RequestTimeoutSeconds)
        {
            ServerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    return Normalize(loaded);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Повреждённый файл настроек не должен мешать запуску — берём значения по умолчанию.
        }

        return Normalize(new AppSettings());
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            settings.ServerUrl = AppSettings.DefaultServerUrl;
        }

        if (string.IsNullOrWhiteSpace(settings.ProjectsRoot))
        {
            settings.ProjectsRoot = DefaultProjectsRoot();
        }

        settings.RequestTimeoutSeconds = Math.Clamp(settings.RequestTimeoutSeconds, 10, 7200);
        settings.NewProjectDefaults ??= new ProjectOptions();
        return settings;
    }

    public static string DefaultProjectsRoot() => Path.Combine(DocumentsFolder(), "readSounds Projects");

    /// <summary>
    /// Папка с документами пользователя.
    /// </summary>
    /// <remarks>
    /// В Windows это «Документы» из профиля. В Linux
    /// <c>SpecialFolder.MyDocuments</c> возвращает домашнюю папку, из-за чего
    /// проекты оказываются рядом с файлами пользователя, а не в документах.
    /// Поэтому там сначала спрашиваем <c>XDG_DOCUMENTS_DIR</c>, как это
    /// предусмотрено freedesktop, и только потом откатываемся на домашнюю папку.
    /// </remarks>
    private static string DocumentsFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DOCUMENTS_DIR");
            if (!string.IsNullOrWhiteSpace(xdg) && Directory.Exists(xdg))
            {
                return xdg;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static void WriteAtomically(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Пишем во временный файл и подменяем, чтобы обрыв записи не убил настройки.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
