using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readSounds.Models;

namespace readSounds.ViewModels;

/// <summary>Строка в списке проектов.</summary>
public partial class ProjectListItem(ProjectModel model) : ViewModelBase
{
    public ProjectModel Model { get; } = model;

    public string Name => Model.Meta.Name;

    public string Source => $"{Model.Meta.SourceFileName} · {Model.Meta.SourceCharCount:N0} симв.";

    public string Voice => string.IsNullOrEmpty(Model.Options.SpeakerId)
        ? $"Голос по умолчанию · {Model.Options.LanguageId}"
        : $"{Model.Options.SpeakerId} · {Model.Options.LanguageId}";

    public string Updated => Model.Meta.UpdatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public string Status => Model.Progress.Status switch
    {
        ProjectStatus.InProgress => "В работе",
        ProjectStatus.Completed => "Готово",
        ProjectStatus.Failed => "Ошибка",
        _ => "Не начат",
    };

    /// <summary>Одно из четырёх состояний — для цвета в списке.</summary>
    public string StatusKind => Model.Progress.Status switch
    {
        ProjectStatus.InProgress => "inprogress",
        ProjectStatus.Completed => "done",
        ProjectStatus.Failed => "failed",
        _ => "idle",
    };

    public double Progress => Model.ProgressFraction;

    /// <summary>Полоску показываем, только когда есть с чем сравнивать.</summary>
    public bool HasProgress => Model.Progress.TotalChunks > 0;

    public string ProgressText => Model.ProgressText;

    public bool HasError => !string.IsNullOrEmpty(Model.Progress.LastError);

    public string ErrorText => Model.Progress.LastError ?? string.Empty;

    /// <summary>Перечитывает модель после изменения прогресса.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Voice));
        OnPropertyChanged(nameof(Updated));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
    }
}
