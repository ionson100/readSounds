namespace readSounds.Models;

/// <summary>Настройки приложения, общие для всех проектов.</summary>
public sealed class AppSettings
{
    public const string DefaultServerUrl = "http://localhost:5002";

    /// <summary>Адрес XTTS-сервера, включая порт.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>Папка, в которой создаются проекты.</summary>
    public string ProjectsRoot { get; set; } = string.Empty;

    /// <summary>Таймаут одного запроса синтеза. XTTS на GPU стартует медленно, поэтому большой.</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    public ProjectOptions NewProjectDefaults { get; set; } = new();
}
