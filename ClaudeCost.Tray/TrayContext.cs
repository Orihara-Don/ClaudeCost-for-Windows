using System.Diagnostics;
using ClaudeCost.Core;

namespace ClaudeCost.Tray;

/// <summary>通知領域のアイコン、集計、パネル、設定をまとめる。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly LogScanner _scanner = new(AppPaths.State);
    private readonly LogWatcher _watcher;
    private readonly PanelForm _panel;
    private Icon? _icon;
    private bool? _iconIsWarning;
    private ScanResult? _lastResult;
    private bool _rerunRequested;
    private SettingsForm? _settingsForm;

    public AppSettings Settings { get; private set; }
    public Summary? Summary { get; private set; }
    public ScanProgress? Progress { get; private set; }
    public bool IsScanning { get; private set; }
    public bool LogFormatWarning { get; private set; }
    public string? PricesError { get; private set; }
    /// <summary>直近の集計で見つけたログの場所</summary>
    public IReadOnlyList<LogSource> Sources { get; private set; } = [];

    private readonly SnapshotRequest? _snapshot;

    public TrayContext(SnapshotRequest? snapshot = null)
    {
        _snapshot = snapshot;
        Settings = AppSettings.Load();
        _panel = new PanelForm(this);

        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => _panel.ShowNearCursor());
        menu.Items.Add("今すぐ更新", null, (_, _) => Refresh());
        menu.Items.Add("設定…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Quit());

        _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Text = "ClaudeCost（集計中…）" };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _panel.Toggle();
        };
        UpdateIcon();
        _notifyIcon.Visible = true;

        _watcher = new LogWatcher(Refresh, () =>
        {
            Rebuild();
            Refresh();
        });
        _watcher.Start(Settings.ProjectsDirectory);
        Refresh();

        if (snapshot is not null) return;
        LaunchAtLogin.RefreshPathIfEnabled();
        SynchronizationContext.Current?.Post(_ => AskLaunchAtLoginOnce(), null);
    }

    // MARK: 集計

    /// <summary>実行中に来た要求は、終わった後に1回だけ再実行する。</summary>
    public async void Refresh()
    {
        if (IsScanning)
        {
            _rerunRequested = true;
            return;
        }
        IsScanning = true;
        _panel.RefreshIfVisible();
        var ui = SynchronizationContext.Current;
        try
        {
            do
            {
                _rerunRequested = false;
                var settings = Settings;
                var result = await Task.Run(() =>
                {
                    var sources = LogSources.Discover(settings.CustomLogPath, settings.IncludeWsl);
                    Sources = sources;
                    return _scanner.ScanOnce(sources, DateTimeOffset.Now, progress =>
                        ui?.Post(_ => OnProgress(progress), null));
                });
                _lastResult = result;
                LogFormatWarning = result.LogFormatWarning;
                Rebuild();
            } while (_rerunRequested);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            IsScanning = false;
            if (_snapshot is { } snap)
            {
                try { _panel.SaveSnapshot(snap.Path, snap.Dark ? Theme.Dark : Theme.Light); }
                catch (Exception e) { ErrorLog.Write(e); }
                Quit();
            }
            _panel.RefreshIfVisible();
        }
    }

    private void OnProgress(ScanProgress? progress)
    {
        Progress = progress;
        if (Summary is null) _panel.RefreshIfVisible();
    }

    private void Rebuild()
    {
        if (_lastResult is not { } result) return;
        var (prices, error) = PriceTable.LoadMerged(AppPaths.UserPrices);
        PricesError = error;
        Summary = SummaryBuilder.Build(result.Rows, prices, Settings.RegionalPricing, result.LogsFound, DateTimeOffset.Now);
        UpdateIcon();
        _panel.RefreshIfVisible();
    }

    // MARK: 表示

    /// <summary>月末見込みが予算を超えているか。</summary>
    public bool IsOverBudget =>
        Settings.MonthlyBudget is { } budget && Summary is { } s && s.ForecastUsd > budget;

    public (string Primary, string? Secondary) Amounts(double usd) =>
        Format.Amounts(usd, Settings.JpyRate, Settings.Currency);

    private string AmountText(double usd)
    {
        var (primary, secondary) = Amounts(usd);
        return secondary is null ? primary : $"{primary} / {secondary}";
    }

    private void UpdateIcon()
    {
        var warning = IsOverBudget;
        if (_iconIsWarning != warning)
        {
            var old = _icon;
            _icon = TrayIcons.Create(warning);
            _notifyIcon.Icon = _icon;
            TrayIcons.Destroy(old);
            _iconIsWarning = warning;
        }

        if (Summary is { } s)
        {
            var text = $"ClaudeCost\n今日 {AmountText(s.Today.Usd)}\n今月 {AmountText(s.MonthUsd)}";
            if (warning) text += "\n予算を超える見込み";
            _notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        }
    }

    // MARK: 設定

    public void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    public void ApplySettings(AppSettings settings)
    {
        var old = Settings;
        if (settings == old) return;
        Settings = settings;
        Settings.Save();
        if (settings.ProjectsDirectory != old.ProjectsDirectory || settings.IncludeWsl != old.IncludeWsl)
        {
            _watcher.Start(settings.ProjectsDirectory);
            Refresh();
        }
        Rebuild();
    }

    public void OpenPrices()
    {
        var path = AppPaths.UserPrices;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                using var source = typeof(PriceTable).Assembly.GetManifestResourceStream("ClaudeCost.Core.prices.json");
                if (source is not null)
                {
                    using var target = File.Create(path);
                    source.CopyTo(target);
                }
            }
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            MessageBox.Show($"prices.json を開けませんでした。\n{path}", "ClaudeCost", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AskLaunchAtLoginOnce()
    {
        if (Settings.DidAskLaunchAtLogin) return;
        Settings = Settings with { DidAskLaunchAtLogin = true };
        Settings.Save();

        var answer = MessageBox.Show(
            "Windows へのサインイン時に ClaudeCost を起動しますか？\n\n起動しておくと、通知領域の金額が常に更新されます。設定からいつでも変更できます。",
            "ClaudeCost", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            try
            {
                LaunchAtLogin.Set(true);
            }
            catch (Exception e)
            {
                ErrorLog.Write(e);
            }
        }
    }

    // MARK: 終了

    public void Quit()
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _notifyIcon.Dispose();
            TrayIcons.Destroy(_icon);
            _settingsForm?.Dispose();
            _panel.Dispose();
        }
        base.Dispose(disposing);
    }
}
