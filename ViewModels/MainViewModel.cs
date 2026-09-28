using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using readSounds.Models;
using readSounds.Services;

namespace readSounds.ViewModels;

/// <summary>Главное окно: список проектов и настройки подключения к XTTS.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IProjectStore _store;
    private readonly ITtsClient _tts;
    private readonly IAppSettingsProvider _settings;
    private readonly IShellService _shell;

    public MainViewModel(IProjectStore store, ITtsClient tts, IAppSettingsProvider settings, IShellService shell)
    {
        _store = store;
        _tts = tts;
        _settings = settings;
        _shell = shell;

        _serverUrl = settings.Current.ServerUrl;
        _projectsRoot = settings.Current.ProjectsRoot;

        Projects = [];
        Reload();
    }

    public ObservableCollection<ProjectListItem> Projects { get; }

    /// <summary>Просит представление показать диалог создания проекта.</summary>
    public event EventHandler? NewProjectRequested;

    /// <summary>Просит представление открыть окно проекта.</summary>
    public event EventHandler<ProjectModel>? ProjectOpenRequested;

    [ObservableProperty]
    private ProjectListItem? _selectedProject;

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _projectsRoot;

    [ObservableProperty]
    private string _serverStatus = "Не проверялось";

    [ObservableProperty]
    private bool _serverReachable;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasProjects => Projects.Count > 0;

    /// <summary>Создаёт проект и сразу открывает его. Вызывается из диалога.</summary>
    public void CreateProject(string sourceFile, string name, ProjectOptions options)
    {
        try
        {
            var project = _store.Create(sourceFile, name, options);
            var item = new ProjectListItem(project);
            Projects.Insert(0, item);
            SelectedProject = item;
            OnPropertyChanged(nameof(HasProjects));
            ProjectOpenRequested?.Invoke(this, project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Обновляет строку списка после работы над проектом.</summary>
    public void RefreshItem(ProjectModel model)
    {
        var item = Projects.FirstOrDefault(p => ReferenceEquals(p.Model, model));
        item?.Refresh();
    }

    [ObservableProperty]
    private string _lastError = string.Empty;

    [RelayCommand]
    private void Reload()
    {
        Projects.Clear();

        foreach (var project in _store.LoadAll())
        {
            Projects.Add(new ProjectListItem(project));
        }

        OnPropertyChanged(nameof(HasProjects));
        SelectedProject = Projects.FirstOrDefault();
    }

    [RelayCommand]
    private void NewProject() => NewProjectRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedProject is not null)
        {
            ProjectOpenRequested?.Invoke(this, SelectedProject.Model);
        }
    }

    [RelayCommand]
    private void OpenProjectFolder(ProjectListItem item) => _shell.OpenFolder(item.Model.Directory);

    [RelayCommand]
    private void OpenSelectedFolder()
    {
        if (SelectedProject is not null)
        {
            _shell.OpenFolder(SelectedProject.Model.Directory);
        }
    }

    [RelayCommand]
    private void OpenProjectsRoot() => _shell.OpenFolder(_settings.Current.ProjectsRoot);

    [RelayCommand]
    private void DeleteProject(ProjectListItem item)
    {
        try
        {
            _store.Delete(item.Model);
            Projects.Remove(item);
            OnPropertyChanged(nameof(HasProjects));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "Не удалось удалить проект: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveServerSettingsAsync()
    {
        var current = _settings.Current;
        current.ServerUrl = ServerUrl.Trim();
        current.ProjectsRoot = ProjectsRoot.Trim();
        _settings.Save(current);
        await CheckServerAsync();
    }

    [RelayCommand]
    private async Task CheckServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ServerStatus = "Проверяю соединение…";

        try
        {
            var current = _settings.Current;
            current.ServerUrl = ServerUrl.Trim();
            _settings.Save(current);

            await _tts.PingAsync(CancellationToken.None);
            ServerReachable = true;
            ServerStatus = $"Сервер отвечает: {ServerUrl.Trim()}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TtsException
                                       or InvalidOperationException or UriFormatException)
        {
            ServerReachable = false;
            ServerStatus = "Сервер недоступен: " + Shorten(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Shorten(string message) =>
        message.Length <= 120 ? message : message[..120] + "…";
}
