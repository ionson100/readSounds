using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using readSounds.Models;
using readSounds.Services;
using readSounds.Services.Playback;
using readSounds.ViewModels;
using readSounds.Views;

namespace readSounds;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static IServiceProvider Services { get; private set; } = null!;

    public override void OnFrameworkInitializationCompleted()
    {
        // Нужен до чтения текстов: без него Windows-1251 в .NET недоступен.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Ставим перехват до сборки контейнера: иначе исключение в самом DI
        // не показалось бы пользователю.
        ErrorDialog.Install();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Один экземпляр настроек на всё приложение: адрес сервера меняется «на лету».
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IAppSettingsProvider>(sp => sp.GetRequiredService<SettingsStore>());

        services.AddSingleton<IProjectStore, ProjectStore>();
        services.AddSingleton<ITtsClient, TtsClient>();
        services.AddSingleton<ISynthesisService, SynthesisService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<NewProjectViewModel>();

        // ProjectViewModel требует ProjectModel, которого в контейнере нет и быть не должно:
        // проект создаёт хранилище, а окно лишь показывает его. Поэтому фабрика.
        services.AddTransient<Func<ProjectModel, ProjectViewModel>>(sp => project =>
            new ProjectViewModel(
                project,
                sp.GetRequiredService<ISynthesisService>(),
                sp.GetRequiredService<IProjectStore>(),
                sp.GetRequiredService<ITtsClient>(),
                sp.GetRequiredService<IShellService>(),
                sp.GetRequiredService<IAudioPlayer>()));
    }
}
