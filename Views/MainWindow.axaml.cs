using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using readSounds.Models;
using readSounds.ViewModels;

namespace readSounds.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _model;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private static IServiceProvider Services => App.Services;

    private Func<ProjectModel, ProjectViewModel> ProjectViewModelFactory =>
        Services.GetRequiredService<Func<ProjectModel, ProjectViewModel>>();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Подписываемся один раз: DataContext может меняться в дизайнере.
        if (_model is not null || DataContext is not MainViewModel model)
        {
            return;
        }

        _model = model;
        model.NewProjectRequested += OnNewProjectRequested;
        model.ProjectOpenRequested += OnProjectOpenRequested;
    }

    private async void OnNewProjectRequested(object? sender, EventArgs e)
    {
        try
        {
            var viewModel = Services.GetRequiredService<NewProjectViewModel>();
            var dialog = new NewProjectWindow { DataContext = viewModel };

            // ShowDialog возвращает Task<bool>: true — диалог закрыт кнопкой «Создать».
            if (await dialog.ShowDialog<bool>(this) && viewModel.CanCreate)
            {
                // Показанные настройки запоминаем как умолчания для следующих проектов.
                viewModel.RememberDefaults();
                _model?.CreateProject(viewModel.SourceFile, viewModel.Name, viewModel.BuildOptions());
            }
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "Не удалось создать проект", ex);
        }
    }

    private void OnProjectOpenRequested(object? sender, ProjectModel project)
    {
        ProjectViewModel? viewModel = null;

        try
        {
            viewModel = ProjectViewModelFactory(project);
            var window = new ProjectWindow { DataContext = viewModel };

            // Список в главном окне должен отражать прогресс, пока окно проекта открыто.
            viewModel.ProjectChanged += (_, changed) => _model?.RefreshItem(changed);
            window.Closed += (_, _) => viewModel?.Dispose();

            window.Show(this);
        }
        catch (Exception ex)
        {
            // Конструктор модели читает исходный текст, поэтому падать здесь вполне можно.
            viewModel?.Dispose();
            ErrorDialog.Show(this, "Не удалось открыть проект", ex);
        }
    }
}
