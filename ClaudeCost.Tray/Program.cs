using ClaudeCost.Core;

namespace ClaudeCost.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ClaudeCost.Tray", out var isFirst);
        ApplicationConfiguration.Initialize();
        if (!isFirst)
        {
            MessageBox.Show(
                "ClaudeCost はすでに起動しています。通知領域（タスクバー右下）のアイコンから開いてください。",
                "ClaudeCost", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);

        // 起動後の処理をメッセージループに積めるよう、先に同期コンテキストを用意する
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayContext());
    }
}

/// <summary>不具合の調査用に、予期しない例外を %LOCALAPPDATA%\ClaudeCost\error.log に残す。</summary>
internal static class ErrorLog
{
    public static void Write(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(
                Path.Combine(AppPaths.DataDirectory, "error.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
