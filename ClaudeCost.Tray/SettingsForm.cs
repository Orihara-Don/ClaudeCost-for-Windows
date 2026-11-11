using System.Globalization;
using ClaudeCost.Core;

namespace ClaudeCost.Tray;

/// <summary>設定画面。「保存」で反映する。</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrayContext _app;
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _rate = new() { Minimum = 1, Maximum = 10000, DecimalPlaces = 2, Increment = 1 };
    private readonly CheckBox _regional = new() { Text = "リージョナル料金（×1.1）", AutoSize = true };
    private readonly TextBox _budget = new() { PlaceholderText = "未設定" };
    private readonly CheckBox _launchAtLogin = new() { Text = "Windows へのサインイン時に起動", AutoSize = true };
    private readonly TextBox _logPath = new() { ReadOnly = true };
    private readonly CheckBox _includeWsl = new() { Text = "WSL 内の Claude Code のログも集計する", AutoSize = true };
    private string _customLogPath;

    private static readonly (CurrencyDisplay Value, string Label)[] Currencies =
    [
        (CurrencyDisplay.Usd, "USD"),
        (CurrencyDisplay.Jpy, "円"),
        (CurrencyDisplay.Both, "両方"),
    ];

    public SettingsForm(TrayContext app)
    {
        _app = app;
        var s = app.Settings;
        _customLogPath = s.CustomLogPath;

        Text = "ClaudeCost の設定";
        Font = SystemFonts.MessageBoxFont ?? DefaultFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        foreach (var (_, label) in Currencies) _currency.Items.Add(label);
        _currency.SelectedIndex = Array.FindIndex(Currencies, c => c.Value == s.Currency);
        _rate.Value = (decimal)Math.Clamp(s.JpyRate, 1, 10000);
        _regional.Checked = s.RegionalPricing;
        _budget.Text = s.MonthlyBudget?.ToString(CultureInfo.InvariantCulture) ?? "";
        _launchAtLogin.Checked = LaunchAtLogin.IsEnabled;
        _includeWsl.Checked = s.IncludeWsl;
        UpdateLogPath();

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(380)));

        AddRow(table, "通貨の表示", _currency);
        AddRow(table, "USD/JPY レート", _rate);
        AddRow(table, "", _regional);
        AddRow(table, "", Note("Bedrock の global エンドポイントを使っている場合や、Bedrock を使っていない場合はオフにしてください。"));
        AddRow(table, "月の予算（USD）", _budget);
        AddRow(table, "", _launchAtLogin);

        var logButtons = Flow(
            _logPath,
            MakeButton("選択…", ChooseLogFolder),
            MakeButton("既定に戻す", () =>
            {
                _customLogPath = "";
                UpdateLogPath();
            }));
        _logPath.Width = LogicalToDeviceUnits(220);
        AddRow(table, "ログの場所", logButtons);
        AddRow(table, "", _includeWsl);
        AddRow(table, "", Note("起動中のディストリビューションの ~/.claude/projects を \\\\wsl.localhost 経由で読みます。停止中のものは起動しません。"));
        AddRow(table, "検出した場所", Note(DetectedSources()));
        AddRow(table, "単価表", MakeButton("prices.json を開く", () => _app.OpenPrices()));
        AddRow(table, "アプリ", MakeButton("ClaudeCost を終了", () =>
        {
            Close();
            _app.Quit();
        }));

        var save = new Button { Text = "保存", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = Flow(save, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 12, 0, 0);
        table.Controls.Add(buttons, 1, table.RowCount);

        Controls.Add(table);
    }

    private void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 12, 6),
            }, 0, row);
        }
        control.Margin = new Padding(0, 4, 0, 4);
        if (control is ComboBox or NumericUpDown or TextBox) control.Width = LogicalToDeviceUnits(160);
        table.Controls.Add(control, 1, row);
    }

    private Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(LogicalToDeviceUnits(380), 0),
        ForeColor = SystemColors.GrayText,
    };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true, UseMnemonic = false };
        button.Click += (_, _) => onClick();
        return button;
    }

    private string DetectedSources() =>
        _app.Sources.Count == 0
            ? "（まだ集計していません）"
            : string.Join(Environment.NewLine, _app.Sources.Select(s => $"{s.Label}: {s.Root}"));

    private void UpdateLogPath()
    {
        _logPath.Text = AppPaths.ProjectsDirectory(_customLogPath);
    }

    private void ChooseLogFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Claude Code のログのフォルダ（projects）を選んでください",
            UseDescriptionForTitle = true,
            InitialDirectory = AppPaths.ProjectsDirectory(_customLogPath),
            ShowHiddenFiles = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _customLogPath = dialog.SelectedPath;
            UpdateLogPath();
        }
    }

    private void Save()
    {
        double? budget = null;
        var budgetText = _budget.Text.Trim();
        if (budgetText.Length > 0)
        {
            if (!double.TryParse(budgetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                MessageBox.Show(this, "月の予算は 0 より大きい数で入力してください。空欄にすると未設定になります。",
                    "ClaudeCost", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _budget.Focus();
                return;
            }
            budget = value;
        }

        try
        {
            if (_launchAtLogin.Checked != LaunchAtLogin.IsEnabled) LaunchAtLogin.Set(_launchAtLogin.Checked);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            MessageBox.Show(this, "サインイン時の起動を設定できませんでした。", "ClaudeCost", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _app.ApplySettings(_app.Settings with
        {
            Currency = Currencies[Math.Max(0, _currency.SelectedIndex)].Value,
            JpyRate = (double)_rate.Value,
            RegionalPricing = _regional.Checked,
            MonthlyBudget = budget,
            CustomLogPath = _customLogPath,
            IncludeWsl = _includeWsl.Checked,
        });
        Close();
    }
}
