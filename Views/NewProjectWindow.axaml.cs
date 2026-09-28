using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using readSounds.ViewModels;

namespace readSounds.Views;

public partial class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        InitializeComponent();
    }

    private NewProjectViewModel? ViewModel => DataContext as NewProjectViewModel;

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите текстовый файл",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Текст") { Patterns = ["*.txt", "*.md", "*.text", "*.markdown"] },
                new FilePickerFileType("Все файлы") { Patterns = ["*"] },
            ],
        });

        if (files.Count > 0)
        {
            viewModel.ChooseSource(files[0].TryGetLocalPath());
        }
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
