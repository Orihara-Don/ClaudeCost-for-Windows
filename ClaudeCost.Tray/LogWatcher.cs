using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace ClaudeCost.Tray;

/// <summary>更新のきっかけ（ファイルの変更、定期実行、スリープ復帰、日付の変化）をまとめる。</summary>
internal sealed class LogWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    // Windows は書き込み中のファイルの変更通知が遅れることがあるため、定期実行を短めにする
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    private readonly SynchronizationContext _ui;
    private readonly Action _onChange;
    private readonly Action _onDayChange;
    private readonly Timer _debounce = new() { Interval = (int)Debounce.TotalMilliseconds };
    private readonly Timer _periodic = new() { Interval = (int)Period.TotalMilliseconds };
    private FileSystemWatcher? _watcher;
    private string _path = "";
    private string _day = Today();

    public LogWatcher(Action onChange, Action onDayChange)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("UIスレッドで作成してください");
        _onChange = onChange;
        _onDayChange = onDayChange;

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _onChange();
        };
        _periodic.Tick += (_, _) =>
        {
            // 起動時にログのフォルダが無かった場合も、ここで監視を始める
            StartWatcherIfPossible();
            CheckDayChange();
            _onChange();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
    }

    public void Start(string path)
    {
        _path = path;
        StopWatcher();
        StartWatcherIfPossible();
        _periodic.Start();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        StopWatcher();
        _debounce.Dispose();
        _periodic.Dispose();
    }

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private void CheckDayChange()
    {
        var today = Today();
        if (today == _day) return;
        _day = today;
        _onDayChange();
    }

    /// <summary>変更から5秒後にまとめて実行する。連続して書かれても待ち続けない。</summary>
    private void ScheduleChange()
    {
        if (!_debounce.Enabled) _debounce.Start();
    }

    private void StartWatcherIfPossible()
    {
        if (_watcher is not null || !Directory.Exists(_path)) return;
        try
        {
            var watcher = new FileSystemWatcher(_path, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => _ui.Post(_ => ScheduleChange(), null);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => _ui.Post(_ => ScheduleChange(), null);
            watcher.Error += (_, _) => _ui.Post(_ => ScheduleChange(), null);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ErrorLog.Write(e);
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.Post(_ => _onChange(), null);
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _ui.Post(_ => CheckDayChange(), null);
}
