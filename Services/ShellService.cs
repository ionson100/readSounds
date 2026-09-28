using System.Diagnostics;

namespace readSounds.Services;

/// <summary>Открытие папок и файлов в системном приложении.</summary>
public interface IShellService
{
    void OpenFolder(string path);

    void OpenFile(string path);
}

public sealed class ShellService : IShellService
{
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);

#if WINDOWS
        Start("explorer.exe", $"\"{path}\"");
#else
        // В Linux папку открывает xdg-open, он же разбирает и file://.
        // Отдельная программа не нужна: xdg-open умеет и то, и другое.
        Start("xdg-open", $"\"{path}\"");
#endif
    }

    public void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        Start(path, null);
    }

    private static void Start(string fileName, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
            };

            if (arguments is not null)
            {
                info.Arguments = arguments;
            }

            Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Нечем открыть — молча пропускаем, это не повод ронять приложение.
        }
    }
}
