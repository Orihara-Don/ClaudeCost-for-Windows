using ClaudeCost.Core;

namespace ClaudeCost.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 開発用: ClaudeCost.exe --snapshot out.png [--dark] で、集計したパネルを画像に保存して終わる
        var snapshot = args.Length >= 2 && args[0] == "--snapshot"
            ? new SnapshotRequest(Path.GetFullPath(args[1]), args.Contains("--dark"))
            : null;
        using var mutex = new Mutex(true, snapshot is null ? @"Local\ClaudeCost.Tray" : @"Local\ClaudeCost.Snapshot", out var isFirst);
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
        Application.Run(new TrayContext(snapshot));
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

internal sealed record SnapshotRequest(string Path, bool Dark);
