using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace readSounds.Views;

/// <summary>
/// Показ ошибок пользователю.
/// </summary>
/// <remarks>
/// Без этого любое исключение вне команды (в обработчике события окна, в таймере)
/// убивало приложение молча. Здесь три уровня: диалог для известных операций,
/// перехват исключений в UI-потоке и, наконец, отказ всего процесса.
/// </remarks>
public static class ErrorDialog
{
    /// <summary>Показывает ошибку. Из обработчика события — <c>await</c> здесь не нужен.</summary>
    public static void Show(Window? owner, string title, Exception exception)
    {
        var message = Describe(exception);

        try
        {
            var topLevel = owner ?? GetActiveWindow();
            if (topLevel is not null)
            {
                var dialog = new Window
                {
                    Title = title,
                    Width = 560,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                };

                var text = new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 520,
                    Margin = new Thickness(18),
                };

                var close = new Button
                {
                    Content = "Закрыть",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Thickness(18, 0, 18, 14),
                };

                close.Click += (_, _) => dialog.Close();

                var panel = new StackPanel();
                panel.Children.Add(text);
                panel.Children.Add(close);
                dialog.Content = panel;

                // Модальность здесь не нужна: исключение уже поймано, а блокировать
                // поток UI синхронным Show нельзя.
                dialog.Show(topLevel);
                return;
            }
        }
        catch (Exception)
        {
            // Если окно показать не удалось — падаем в сообщение ниже.
        }

        Report(title, message);
    }

    /// <summary>
    /// Последний рубеж: пишем в stderr и показываем системное окно.
    /// </summary>
    /// <remarks>
    /// WPF-оверлей <c>System.Windows.MessageBox</c> здесь недоступен — проект на Avalonia,
    /// поэтому вызываем user32 напрямую. Окно нужно в том случае, когда Avalonia-диалог
    /// показать уже нечем: исключение случилось до инициализации UI или в фоновом потоке.
    /// </remarks>
    public static void Report(string title, string message)
    {
        Console.Error.WriteLine($"[{title}] {message}");

        try
        {
            NativeMessageBox.Show(title, message);
        }
        catch (Exception)
        {
            // Если и это не вышло — молча, но в stderr запись уже есть.
        }
    }

    /// <summary>Человеческое описание ошибки вместо типа и стектрейса.</summary>
    public static string Describe(Exception exception)
    {
        var text = exception switch
        {
            AggregateException { InnerExceptions.Count: > 0 } aggregate
                => Describe(aggregate.InnerExceptions[0]),
            _ => exception.Message,
        };

        return string.IsNullOrWhiteSpace(text) ? exception.GetType().Name : text;
    }

    public static Window? GetActiveWindow()
    {
        // Application.Windows в Avalonia 12 — это IReadOnlyList, но обход через
        // ApplicationLifetime надёжнее: ссылки на окна живут именно там.
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? desktop.MainWindow;
        }

        return null;
    }

    /// <summary>Включается один раз при старте: ловит всё, что не поймали выше.</summary>
    public static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Show(GetActiveWindow(), "Ошибка", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                Report("Необработанная ошибка", Describe(exception));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Забытый await не должен ни шуметь в лог, ни ронять процесс.
            e.SetObserved();
        };
    }
}
