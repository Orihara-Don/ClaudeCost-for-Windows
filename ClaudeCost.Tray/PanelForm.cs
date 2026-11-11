using System.Runtime.InteropServices;

namespace ClaudeCost.Tray;

/// <summary>アイコンをクリックすると開く内訳パネル。フォーカスが外れると閉じる。</summary>
internal sealed class PanelForm : Form
{
    private readonly PanelView _view;
    private DateTime _hiddenAt = DateTime.MinValue;
    private Point _anchor;

    public PanelForm(TrayContext app)
    {
        Text = "ClaudeCost";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;

        _view = new PanelView(app, HidePanel) { Dock = DockStyle.Fill };
        Controls.Add(_view);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int dropShadow = 0x00020000;
            var p = base.CreateParams;
            p.ClassStyle |= dropShadow;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 では角を丸くする（それより前の Windows では何もしない）
        const int cornerPreference = 33;
        var round = 2;
        _ = DwmSetWindowAttribute(Handle, cornerPreference, ref round, sizeof(int));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HidePanel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HidePanel();
    }

    private void HidePanel()
    {
        if (!Visible) return;
        Hide();
        _hiddenAt = DateTime.Now;
    }

    /// <summary>アイコンのクリック。開いているときにクリックすると、先にフォーカスが外れて閉じるので、直後なら開き直さない。</summary>
    public void Toggle()
    {
        if (Visible)
        {
            HidePanel();
            return;
        }
        if (DateTime.Now - _hiddenAt < TimeSpan.FromMilliseconds(300)) return;
        ShowNearCursor();
    }

    public void ShowNearCursor()
    {
        _anchor = Cursor.Position;
        _view.ResetHover();
        ApplyTheme();
        Render();
        Show();
        Render();
        Activate();
    }

    public void RefreshIfVisible()
    {
        if (Visible) Render();
    }

    /// <summary>開発用: 指定のテーマでパネルを PNG に保存する。</summary>
    public void SaveSnapshot(string path, Theme theme)
    {
        _view.SetTheme(theme);
        using var bitmap = _view.RenderSnapshot();
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void ApplyTheme()
    {
        var theme = Theme.Current();
        _view.SetTheme(theme);
        BackColor = theme.Background;
        // Windows 11 の枠線の色をカードに合わせる
        const int borderColor = 34;
        var colorRef = theme.CardBorder.R | (theme.CardBorder.G << 8) | (theme.CardBorder.B << 16);
        if (IsHandleCreated) _ = DwmSetWindowAttribute(Handle, borderColor, ref colorRef, sizeof(int));
    }

    private void Render()
    {
        ClientSize = _view.Measure();
        PlaceNearAnchor();
        _view.Invalidate();
    }

    /// <summary>タスクバーの位置に合わせて、アイコンの近くに出す。</summary>
    private void PlaceNearAnchor()
    {
        var screen = Screen.FromPoint(_anchor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var gap = LogicalToDeviceUnits(12);
        var size = Size;

        int x, y;
        if (work.Left > bounds.Left)
        {
            x = work.Left + gap;
            y = _anchor.Y - size.Height / 2;
        }
        else if (work.Right < bounds.Right)
        {
            x = work.Right - size.Width - gap;
            y = _anchor.Y - size.Height / 2;
        }
        else if (work.Top > bounds.Top)
        {
            x = _anchor.X - size.Width / 2;
            y = work.Top + gap;
        }
        else
        {
            x = _anchor.X - size.Width / 2;
            y = work.Bottom - size.Height - gap;
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - size.Width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - size.Height - gap));
        Location = new Point(x, y);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
