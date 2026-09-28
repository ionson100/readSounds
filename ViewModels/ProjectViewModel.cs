using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readSounds.Models;
using readSounds.Services;
using readSounds.Services.Playback;

namespace readSounds.ViewModels;

/// <summary>Окно одного проекта: настройки, прогресс синтеза и прослушивание результата.</summary>
public partial class ProjectViewModel : ViewModelBase, IDisposable
{
    /// <summary>Сколько символов исходника показываем в предпросмотре — целиком книгу в TextBox нельзя.</summary>
    private const int PreviewCharLimit = 200_000;

    private readonly ProjectModel _project;
    private readonly ISynthesisService _synthesis;
    private readonly IProjectStore _store;
    private readonly ITtsClient _tts;
    private readonly IShellService _shell;
    private readonly IAudioPlayer _player;
    private readonly DispatcherTimer _positionTimer;

    private CancellationTokenSource? _cancellation;
    private readonly EventHandler _playerStateHandler;
    private string? _loadedSignature;

    /// <summary>Пока идёт перезаполнение списков, сохранять настройки в проект нельзя.</summary>
    private bool _suppressPersist;

    public ProjectViewModel(
        ProjectModel project,
        ISynthesisService synthesis,
        IProjectStore store,
        ITtsClient tts,
        IShellService shell,
        IAudioPlayer player)
    {
        _project = project;
        _synthesis = synthesis;
        _store = store;
        _tts = tts;
        _shell = shell;
        _player = player;

        _languageId = project.Options.LanguageId;
        _speakerId = project.Options.SpeakerId;
        _maxChunkLength = project.Options.MaxChunkLength;

        Speakers = new ObservableCollection<string>(TtsCatalog.XttsV2Defaults.Speakers);
        Languages = new ObservableCollection<string>(TtsCatalog.XttsV2Defaults.Languages);
        Chunks = new ObservableCollection<ChunkPreviewItem>();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += (_, _) => UpdatePlaybackPosition();

        // Обработчик держим в поле, иначе отписаться лямбдой не получится.
        _playerStateHandler = (_, _) => OnPlaybackStateChanged();
        _player.StateChanged += _playerStateHandler;

        LoadPreview();
        AdoptProgress();
    }

    public ProjectModel Project => _project;

    public ObservableCollection<string> Speakers { get; }

    public ObservableCollection<string> Languages { get; }

    public ObservableCollection<ChunkPreviewItem> Chunks { get; }

    /// <summary>Сообщает окну списка, что пора обновить строку проекта.</summary>
    public event EventHandler<ProjectModel>? ProjectChanged;

    // Nullable: SelectedItem у ComboBox умеет прислать null, когда список очистили.
    [ObservableProperty]
    private string? _languageId;

    [ObservableProperty]
    private string? _speakerId;

    [ObservableProperty]
    private int _maxChunkLength;

    [ObservableProperty]
    private string _sourcePreview = string.Empty;

    [ObservableProperty]
    private string _sourceInfo = string.Empty;

    [ObservableProperty]
    private int _totalChunks;

    [ObservableProperty]
    private int _completedChunks;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _statusText = "Готов к работе";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isSplitStale;

    [ObservableProperty]
    private string _splitWarning = string.Empty;

    [ObservableProperty]
    private string _playbackText = "00:00 / 00:00";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>Сколько типографских символов заменено в уже отправленных частях.</summary>
    [ObservableProperty]
    private int _normalizedSoFar;

    /// <summary>Сколько всего символов нормализация тронет во всём тексте.</summary>
    [ObservableProperty]
    private int _normalizedTotal;

    /// <summary>Подсказка под списком разбивки: сколько символов будет исправлено.</summary>
    public string NormalizationNote => NormalizedTotal <= 0
        ? "Типографских символов не найдено — текст уходит на сервер как есть."
        : $"Перед отправкой заменено {NormalizedTotal} типографских символов " +
          $"(«кавычки», тире, невидимые знаки). Исходный файл не меняется.";

    /// <summary>Показывать ли подсказку во время синтеза.</summary>
    public string NormalizationRunNote => NormalizedSoFar <= 0
        ? string.Empty
        : $"Нормализовано {NormalizedSoFar} симв.";

    /// <summary>Номера частей с ненормальным темпом речи — их стоит переозвучить.</summary>
    public ObservableCollection<int> SuspiciousParts { get; } = [];

    public bool HasSuspiciousParts => SuspiciousParts.Count > 0;

    public string SuspiciousNote =>
        $"Подозрительный темп в частях: {string.Join(", ", SuspiciousParts)}. " +
        "На них слышно лишнее — переозвучите по кнопке «Переозвучить».";

    public bool IsPlayerAvailable => _player.IsAvailable;

    public bool HasAudio => File.Exists(_project.AudioPath) && _project.Progress.DataBytes > 0;

    public string AudioPath => _project.AudioPath;

    public string ProjectFolder => _project.Directory;

    public string ProgressText => TotalChunks <= 0
        ? "Нет данных"
        : $"Часть {CompletedChunks} из {TotalChunks} ({ProgressFraction * 100:0} %)";

    public bool CanStart => !IsRunning && HasValidOptions;

    public bool CanStop => IsRunning;

    /// <summary>
    /// Сервер превращает пустые language_id и speaker_id в None, и XTTS на этом отвечает 500.
    /// Поэтому синтез не запускаем, пока оба значения выбраны.
    /// </summary>
    public bool HasValidOptions =>
        !string.IsNullOrWhiteSpace(LanguageId) && !string.IsNullOrWhiteSpace(SpeakerId);

    /// <summary>Что именно не выбрано — показываем прямо под настройками.</summary>
    public string OptionsProblem
    {
        get
        {
            var noLanguage = string.IsNullOrWhiteSpace(LanguageId);
            var noSpeaker = string.IsNullOrWhiteSpace(SpeakerId);

            if (noLanguage && noSpeaker)
            {
                return "Выберите язык и голос: без них XTTS отвечает ошибкой 500.";
            }

            if (noLanguage)
            {
                return "Выберите язык: без него XTTS отвечает ошибкой 500.";
            }

            if (noSpeaker)
            {
                return "Выберите голос: без него XTTS отвечает ошибкой 500.";
            }

            return string.Empty;
        }
    }

    /// <summary>Что реально уйдёт на сервер в каждой части — видно, куда смотреть при ошибке.</summary>
    public string RequestPreview => $"language_id={LanguageId}  ·  speaker_id={SpeakerId}";

    /// <summary>
    /// Кнопка называется по состоянию. У завершённого проекта «Продолжить» вводило бы
    /// в заблуждение — продолжать нечего, поэтому предлагаем начать заново.
    /// </summary>
    public string StartButtonText => _project.Progress.Status switch
    {
        ProjectStatus.Completed => "Синтезировать заново",
        _ => _project.Progress.CompletedChunks > 0 ? "Продолжить" : "Синтезировать",
    };

    // ------------------------------------------------------------------ запуск

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        Error = string.Empty;
        IsRunning = true;
        RefreshCommands();

        // У завершённого проекта продолжать нечего: без сброса нажатие прошло бы
        // вхолостую — цикл сразу увидит «все части готовы» и закончится ничем.
        if (_project.Progress.Status == ProjectStatus.Completed)
        {
            // Плеер держит result.wav открытым, и удалить его не получится.
            StopPlayback();
            _synthesis.Reset(_project);
            AdoptProgress();
        }

        _cancellation = new CancellationTokenSource();
        var report = new Progress<SynthesisProgress>(OnSynthesisProgress);

        try
        {
            var result = await _synthesis.RunAsync(_project, report, _cancellation.Token);
            SuspiciousParts.Clear();

            if (result.HasSuspicious)
            {
                // Части с ненормальным темпом речи: на них слышно лишнее.
                foreach (var part in result.Suspicious)
                {
                    SuspiciousParts.Add(part.Part);
                }

                StatusText = $"Готово, но частей с подозрительным темпом: {result.Suspicious.Count}. " +
                             "Их можно переозвучить по отдельности.";
            }
            else
            {
                StatusText = $"Готово. {TotalChunks} {Plural(TotalChunks)} в result.wav";
            }

            AdoptProgress();
            OnPropertyChanged(nameof(HasSuspiciousParts));
            OnPropertyChanged(nameof(SuspiciousNote));
            OnPropertyChanged(nameof(HasAudio));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Остановлено. Можно продолжить с текущего места.";
            AdoptProgress();
        }
        catch (Exception ex) when (ex is TtsException or InvalidOperationException or IOException
                                       or InvalidDataException or FileNotFoundException
                                       or UnauthorizedAccessException or HttpRequestException
                                       or TaskCanceledException or ObjectDisposedException)
        {
            Error = ex.Message;
            StatusText = "Ошибка синтеза";
            AdoptProgress();
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            RefreshCommands();
            OnPropertyChanged(nameof(StartButtonText));
            ProjectChanged?.Invoke(this, _project);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cancellation?.Cancel();
        StatusText = "Останавливаю…";
    }

    /// <summary>Отматывает проект до указанной части и синтезирует её заново.</summary>
    /// <remarks>
    /// Единственный способ починить именно испорченный кусок, не трогая остальные:
    /// файл обрезается до границы предыдущей части, дальше синтез идёт обычным ходом.
    /// </remarks>
    [RelayCommand]
    private async Task RedoPartAsync(int partNumber)
    {
        if (IsRunning || partNumber < 1)
        {
            return;
        }

        Error = string.Empty;

        try
        {
            StopPlayback();
            _synthesis.RewindTo(_project, partNumber);
            AdoptProgress();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException
                                       or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            StatusText = "Не удалось переозвучить часть";
            return;
        }

        await StartAsync();
    }

    /// <summary>Стирает результат и начинает с первой части.</summary>
    [RelayCommand]
    private void Restart()
    {
        if (IsRunning)
        {
            return;
        }

        _synthesis.Reset(_project);
        StopPlayback();
        AdoptProgress();
        RefreshSplit();
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(StartButtonText));
        StatusText = "Результат очищен, можно синтезировать заново";
        ProjectChanged?.Invoke(this, _project);
    }

    // ---------------------------------------------------------------- настройки

    [RelayCommand]
    private async Task LoadVoicesAsync()
    {
        Error = string.Empty;
        StatusText = "Загружаю голоса с сервера…";

        try
        {
            var catalog = await _tts.GetCatalogAsync(CancellationToken.None);

            // Очистка ObservableCollection сбрасывает SelectedItem ComboBox в null, а
            // двусторонняя привязка записала бы null в project.json — после этого XTTS
            // получал бы пустой language_id и отвечал 500. Поэтому на время
            // перезаполнения сохранение отключаем, а выбор восстанавливаем вручную.
            var keepLanguage = LanguageId;
            var keepSpeaker = SpeakerId;

            _suppressPersist = true;
            try
            {
                // Сервер может не вернуть список (модель с одним голосом, например) —
                // тогда оставляем запасной набор XTTS, а не пустой выпадающий список.
                if (catalog.SupportsLanguages)
                {
                    Languages.Clear();
                    foreach (var language in catalog.Languages)
                    {
                        Languages.Add(language);
                    }
                }

                if (catalog.SupportsSpeakers)
                {
                    Speakers.Clear();
                    foreach (var speaker in catalog.Speakers)
                    {
                        Speakers.Add(speaker);
                    }
                }
            }
            finally
            {
                _suppressPersist = false;
            }

            LanguageId = ResolveChoice(keepLanguage, Languages, TtsCatalog.XttsV2Defaults.Languages);
            SpeakerId = ResolveChoice(keepSpeaker, Speakers, TtsCatalog.XttsV2Defaults.Speakers);
            PersistOptions();

            StatusText = catalog.SupportsSpeakers
                ? $"Загружено: {catalog.Speakers.Count} голосов, {catalog.Languages.Count} языков"
                : "Сервер не вернул список голосов, используется набор XTTS по умолчанию";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TtsException
                                       or InvalidOperationException or UriFormatException)
        {
            Error = "Не удалось получить список голосов: " + ex.Message;
            StatusText = "Список голосов не обновлён";
        }
    }

    // ------------------------------------------------------------------ файлы

    [RelayCommand]
    private void OpenFolder() => _shell.OpenFolder(_project.Directory);

    [RelayCommand]
    private void OpenAudioFile()
    {
        if (HasAudio)
        {
            _shell.OpenFile(_project.AudioPath);
        }
    }

    // ---------------------------------------------------------------- плеер

    [RelayCommand]
    private void Play()
    {
        if (!HasAudio)
        {
            return;
        }

        try
        {
            _player.Play(_project.AudioPath);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or IOException
                                       or InvalidOperationException)
        {
            Error = "Не удалось включить воспроизведение: " + ex.Message;
        }
    }

    [RelayCommand]
    private void PauseOrResume()
    {
        if (_player.State == PlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Resume();
        }
    }

    [RelayCommand]
    private void StopPlayback() => _player.Stop();

    // -------------------------------------------------------------- внутреннее

    private void OnSynthesisProgress(SynthesisProgress progress)
    {
        // Счётчик отстаёт на текущую часть нарочно: StatusText сам показывает «Часть N+1».
        CompletedChunks = progress.CompletedChunks;
        TotalChunks = progress.TotalChunks;
        ProgressFraction = progress.Fraction;
        StatusText = progress.StatusText;
        NormalizedSoFar = progress.NormalizedSymbols;

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(NormalizationNote));
        HighlightCurrentChunk(progress.CurrentText);
    }

    private void AdoptProgress()
    {
        CompletedChunks = _project.Progress.CompletedChunks;
        TotalChunks = _project.Progress.TotalChunks;
        ProgressFraction = _project.ProgressFraction;

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StartButtonText));
        RefreshCommands();
    }

    private void LoadPreview()
    {
        try
        {
            var source = TextFileReader.Read(_project.SourcePath);
            var truncated = source.Text.Length > PreviewCharLimit;
            SourcePreview = truncated ? source.Text[..PreviewCharLimit] : source.Text;

            SourceInfo = $"{source.EncodingName} · {source.CharCount:N0} символов"
                + (truncated ? $" · показаны первые {PreviewCharLimit:N0}" : string.Empty);

            _loadedSignature = _project.Options.BuildSignature(source.Sha256);
            NormalizedTotal = TextNormalizer.CountReplacements(source.Text);
            RefreshSplit(source.Text);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException
                                       or UnauthorizedAccessException)
        {
            SourceInfo = "Не удалось прочитать исходный текст: " + ex.Message;
            Error = SourceInfo;
        }
    }

    /// <summary>Пересчитывает разбивку под текущий порог длины части.</summary>
    private void RefreshSplit(string? knownText = null)
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var text = knownText ?? TextFileReader.Read(_project.SourcePath).Text;
            var parts = TextChunker.Split(text, EffectiveLimit);

            Chunks.Clear();
            foreach (var (part, index) in parts.Select((p, i) => (p, i)).Take(PreviewChunkLimit))
            {
                // ⚠ теперь означает «границу предложения нашлось негде», а не
                // «длиннее порога»: превышение порога теперь норма, так ругаться
                // было бы на каждой второй части.
                Chunks.Add(new ChunkPreviewItem(index + 1, part.Text, part.WasHardSplit));
            }

            TotalChunks = parts.Count;
            ProgressFraction = _project.ProgressFraction;
            OnPropertyChanged(nameof(ProgressText));
            EvaluateSplitStale();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException
                                       or UnauthorizedAccessException)
        {
            Error = "Не удалось разбить текст: " + ex.Message;
        }
    }

    /// <summary>Сколько частей показываем в списке — полный список книги может быть на тысячи строк.</summary>
    private const int PreviewChunkLimit = 200;

    private int EffectiveLimit => Math.Clamp(
        MaxChunkLength, ProjectOptions.MinMaxChunkLength, ProjectOptions.MaxMaxChunkLength);

    /// <summary>
    /// Предупреждает, что настройки или текст разошлись с уже записанным аудио:
    /// продолжать нельзя, придётся начинать заново.
    /// </summary>
    private void EvaluateSplitStale()
    {
        if (_project.Progress.CompletedChunks == 0 || _loadedSignature is null)
        {
            IsSplitStale = false;
            SplitWarning = string.Empty;
            return;
        }

        string signature;
        try
        {
            var source = TextFileReader.Read(_project.SourcePath);
            signature = _project.Options.BuildSignature(source.Sha256);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException
                                       or UnauthorizedAccessException)
        {
            IsSplitStale = false;
            SplitWarning = string.Empty;
            return;
        }

        var stale = !string.Equals(signature, _project.Progress.Signature, StringComparison.Ordinal)
            || _project.Progress.TotalChunks != TotalChunks;

        IsSplitStale = stale;
        SplitWarning = stale
            ? "Настройки или текст изменились после начала работы. " +
              "Уже записанное аудио нельзя продолжить — нажмите «Начать заново»."
            : string.Empty;
    }

    private void HighlightCurrentChunk(string? currentText)
    {
        if (string.IsNullOrEmpty(currentText) || Chunks.Count == 0)
        {
            return;
        }

        var target = currentText.Trim();
        foreach (var item in Chunks)
        {
            var isCurrent = item.Text.Trim() == target;
            item.IsCurrent = isCurrent;
            if (isCurrent)
            {
                item.IsDone = true;
            }
        }
    }

    private void UpdatePlaybackPosition()
    {
        PlaybackText = $"{Format(_player.Position)} / {Format(_player.Duration)}";
    }

    private void OnPlaybackStateChanged()
    {
        var state = _player.State;
        OnPropertyChanged(nameof(PlaybackText));

        if (state == PlaybackState.Playing)
        {
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }

        IsPlaying = state == PlaybackState.Playing;
        if (state == PlaybackState.Stopped)
        {
            PlaybackText = $"00:00 / {Format(_player.Duration)}";
        }
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(HasValidOptions));
        OnPropertyChanged(nameof(OptionsProblem));
        OnPropertyChanged(nameof(RequestPreview));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int count) =>
        count % 10 == 1 && count % 100 != 11 ? "часть"
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "части"
        : "частей";

    // Настройки сохраняем сразу: язык и голос должны пережить закрытие приложения.
    partial void OnLanguageIdChanged(string? value)
    {
        var normalized = ResolveChoice(value, Languages, TtsCatalog.XttsV2Defaults.Languages);

        if (normalized != value)
        {
            LanguageId = normalized;
            return;
        }

        PersistOptions();
        RefreshCommands();
    }

    partial void OnSpeakerIdChanged(string? value)
    {
        var normalized = ResolveChoice(value, Speakers, TtsCatalog.XttsV2Defaults.Speakers);

        if (normalized != value)
        {
            SpeakerId = normalized;
            return;
        }

        PersistOptions();
        RefreshCommands();
    }

    partial void OnMaxChunkLengthChanged(int value)
    {
        PersistOptions();
        RefreshSplit();
    }

    /// <summary>
    /// Приводит значение к тому, что сервер поймёт.
    /// </summary>
    /// <remarks>
    /// Пустое значение намеренно остаётся пустым. Подставлять за пользователя первый
    /// язык из списка нельзя: русский текст, озвученный голосом «en», получится тихим
    /// браком, о котором он даже не узнает. Лучше показать подсказку и дать выбрать
    /// явно. А незнакомое непустое значение заменяем первым доступным — сервер всё
    /// равно его не примет.
    /// </remarks>
    private static string ResolveChoice(
        string? value, ObservableCollection<string> current, IReadOnlyList<string> fallback)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (current.Count == 0 || current.Contains(trimmed))
        {
            return trimmed;
        }

        return current.Count > 0 ? current[0] : fallback[0];
    }

    private void PersistOptions()
    {
        if (IsRunning || _suppressPersist)
        {
            return;
        }

        _project.Options.LanguageId = LanguageId ?? string.Empty;
        _project.Options.SpeakerId = SpeakerId ?? string.Empty;
        _project.Options.MaxChunkLength = EffectiveLimit;
        _store.Save(_project);
        EvaluateSplitStale();
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _player.StateChanged -= _playerStateHandler;
    }
}

/// <summary>Строка предпросмотра разбивки.</summary>
public partial class ChunkPreviewItem(int number, string text, bool wasHardSplit) : ViewModelBase
{
    public int Number { get; } = number;

    public string Text { get; } = text;

    /// <summary>Часть получилась длиннее порога — значит, предложения не нашлось и текст резали принудительно.</summary>
    public bool WasHardSplit { get; } = wasHardSplit;

    public int Length => Text.Length;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isDone;
}
