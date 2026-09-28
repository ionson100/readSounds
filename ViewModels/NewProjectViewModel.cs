using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readSounds.Models;
using readSounds.Services;

namespace readSounds.ViewModels;

/// <summary>Диалог создания проекта: исходный файл, имя папки и настройки синтеза.</summary>
public partial class NewProjectViewModel : ViewModelBase
{
    private readonly IAppSettingsProvider _settings;

    public NewProjectViewModel(IAppSettingsProvider settings)
    {
        _settings = settings;
        var defaults = settings.Current.NewProjectDefaults;
        _languageId = defaults.LanguageId;
        _speakerId = defaults.SpeakerId;
        _maxChunkLength = defaults.MaxChunkLength;

        Speakers = new ObservableCollection<string>(TtsCatalog.XttsV2Defaults.Speakers);
        Languages = new ObservableCollection<string>(TtsCatalog.XttsV2Defaults.Languages);
    }

    public ObservableCollection<string> Speakers { get; }

    public ObservableCollection<string> Languages { get; }

    [ObservableProperty]
    private string _sourceFile = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _languageId;

    [ObservableProperty]
    private string _speakerId;

    [ObservableProperty]
    private int _maxChunkLength = ProjectOptions.DefaultMaxChunkLength;

    /// <summary>Что удалось выяснить про выбранный файл.</summary>
    [ObservableProperty]
    private string _sourceInfo = string.Empty;

    [ObservableProperty]
    private int _estimatedChunks;

    [ObservableProperty]
    private string _problem = string.Empty;

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(SourceFile) && File.Exists(SourceFile)
        && !string.IsNullOrWhiteSpace(Name) && Problem.Length == 0;

    public ProjectOptions BuildOptions() => new()
    {
        LanguageId = LanguageId,
        SpeakerId = SpeakerId,
        MaxChunkLength = MaxChunkLength,
    };

    /// <summary>Запоминает последние использованные настройки как значения по умолчанию.</summary>
    public void RememberDefaults()
    {
        var current = _settings.Current;
        current.NewProjectDefaults = BuildOptions();
        _settings.Save(current);
    }

    /// <summary>Проверяет файл и сразу показывает, на сколько частей он распадётся.</summary>
    [RelayCommand]
    public void ChooseSource(string? path)
    {
        Problem = string.Empty;
        EstimatedChunks = 0;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SourceFile = string.Empty;
            SourceInfo = string.Empty;
            Problem = "Файл не найден.";
            return;
        }

        SourceFile = path;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = Path.GetFileNameWithoutExtension(path);
        }

        try
        {
            var source = TextFileReader.Read(path);
            var chunks = TextChunker.Split(source.Text, EffectiveLimit);
            EstimatedChunks = chunks.Count;
            SourceInfo =
                $"{source.EncodingName} · {source.CharCount:N0} симв. · " +
                $"{EstimatedChunks:N0} {Plural(EstimatedChunks, "часть", "части", "частей")} " +
                $"по {MaxChunkLength} симв.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SourceInfo = string.Empty;
            Problem = "Не удалось прочитать файл: " + ex.Message;
        }
    }

    private int EffectiveLimit => Math.Clamp(
        MaxChunkLength, ProjectOptions.MinMaxChunkLength, ProjectOptions.MaxMaxChunkLength);

    partial void OnSourceFileChanged(string value) => RaiseCanCreate();

    partial void OnNameChanged(string value) => RaiseCanCreate();

    partial void OnProblemChanged(string value) => RaiseCanCreate();

    partial void OnMaxChunkLengthChanged(int value) => RaiseCanCreate();

    partial void OnSourceInfoChanged(string value)
    {
    }

    private void RaiseCanCreate() => OnPropertyChanged(nameof(CanCreate));

    private static string Plural(int count, string one, string few, string many) =>
        count % 10 == 1 && count % 100 != 11 ? one
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? few
        : many;
}
