namespace readSounds.Models;

/// <summary>Состояние проекта.</summary>
public enum ProjectStatus
{
    /// <summary>Синтез ещё не запускался.</summary>
    NotStarted,

    /// <summary>Синтез частично выполнен, есть куда продолжать.</summary>
    InProgress,

    /// <summary>Все части синтезированы.</summary>
    Completed,

    /// <summary>Последний запуск прервался ошибкой.</summary>
    Failed,
}
