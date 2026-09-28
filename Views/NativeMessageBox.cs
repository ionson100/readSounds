using System.Runtime.InteropServices;

namespace readSounds.Views;

#if WINDOWS

/// <summary>
/// Диалог ошибки через WinAPI. Нужен на самом последнем рубеже, когда окно Avalonia
/// ещё не создано или UI-поток уже занят: показать своё окно в этих случаях нечем.
/// </summary>
/// <remarks>
/// Здесь обычный <c>DllImport</c>, а не <c>LibraryImport</c>: source-generated
/// маршрутизация требует <c>AllowUnsafeBlocks</c>, а ради одного вызова
/// включать небезопасный код во всём проекте не хочется.
/// </remarks>
internal static class NativeMessageBox
{
    private const uint MB_OK = 0x00000000;

    private const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string caption, string message) =>
        MessageBox(IntPtr.Zero, message, caption, MB_OK | MB_ICONERROR);
}

#else

/// <summary>
/// Заглушка для Linux: <c>user32.dll</c> здесь нет.
/// </summary>
/// <remarks>
/// Показывать системный диалог нечем, и тянуть за собой <c>zenity</c> или
/// <c>kdialog</c> ради аварийного сообщения не хочется. Сообщение всё равно
/// уже записано в stderr вызывающим кодом, поэтому здесь достаточно ничего
/// не делать — вызывающая сторона этот случай перехватывает сама.
/// </remarks>
internal static class NativeMessageBox
{
    public static void Show(string caption, string message)
    {
        // Намеренно пусто: текст ошибки уже выведен в stderr в ErrorDialog.Report.
    }
}

#endif
