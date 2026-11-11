using System.Drawing.Drawing2D;
using System.Globalization;
using ClaudeCost.Core;

namespace ClaudeCost.Tray;

/// <summary>
/// 内訳パネルの中身。部品を並べると DPI やフォントで配置が崩れやすいため、全体をここで描く。
/// 測るときも描くときも同じ <see cref="Draw"/> を通すので、高さと見た目がずれない。
/// </summary>
internal sealed class PanelView : Control
{
    private const int LogicalWidth = 360;
    private const int Pad = 16;

    private const string IconRefresh = "";
    private const string IconSettings = "";
    private const string IconQuit = "";
    private const string IconWarning = "";

    private enum Hit { None, SegmentDays, SegmentWeeks, Refresh, Settings, Quit }

    private readonly TrayContext _app;
    private readonly Action _hidePanel;
    private readonly ToolTip _toolTip = new() { InitialDelay = 400 };
    private readonly List<(Rectangle Rect, Hit Hit)> _hits = new();
    private Theme _theme = Theme.Light;
    private bool _showsWeeks;
    private Hit _hot;
    private int _hoverBar = -1;
    private Rectangle _plot;
    private int _barCount;
    private Fonts? _fonts;

    public PanelView(TrayContext app, Action hidePanel)
    {
        _app = app;
        _hidePanel = hidePanel;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetTheme(Theme theme)
    {
        _theme = theme;
        BackColor = theme.Background;
        Invalidate();
    }

    // MARK: フォント

    private sealed class Fonts : IDisposable
    {
        public required int Dpi { get; init; }
        public required Font Caption { get; init; }
        public required Font Body { get; init; }
        public required Font BodyStrong { get; init; }
        public required Font Hero { get; init; }
        public required Font Stat { get; init; }
        public required Font Icon { get; init; }
        public required Font SmallIcon { get; init; }

        public void Dispose()
        {
            foreach (var font in new[] { Caption, Body, BodyStrong, Hero, Stat, Icon, SmallIcon }) font.Dispose();
        }
    }

    private Fonts CurrentFonts()
    {
        if (_fonts is { } f && f.Dpi == DeviceDpi) return f;
        _fonts?.Dispose();
        var ui = (SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily.Name;
        var number = Drawing.FirstInstalled("Segoe UI Semibold", "Segoe UI", ui);
        var icon = Drawing.FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets", ui);
        Font Px(string family, float px, FontStyle style = FontStyle.Regular) =>
            new(family, px * DeviceDpi / 96f, style, GraphicsUnit.Pixel);
        _fonts = new Fonts
        {
            Dpi = DeviceDpi,
            Caption = Px(ui, 12),
            Body = Px(ui, 13),
            BodyStrong = Px(number, 13),
            Hero = Px(number, 34),
            Stat = Px(number, 17),
            Icon = Px(icon, 14),
            SmallIcon = Px(icon, 12),
        };
        return _fonts;
    }

    // MARK: 測る

    /// <summary>今の内容を描くのに必要な大きさ。</summary>
    public Size Measure()
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        var height = Draw(g, paint: false);
        return new Size(S(LogicalWidth), height);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    // MARK: 描く

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme.Background);
        Draw(e.Graphics, paint: true);
    }

    private static Size TextSize(Graphics g, string text, Font font, int maxWidth = int.MaxValue)
    {
        var flags = TextFormatFlags.NoPadding;
        if (maxWidth != int.MaxValue) flags |= TextFormatFlags.WordBreak;
        return TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue), flags);
    }

    private static void Put(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags = 0) =>
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPadding);

    /// <summary>描いた高さを返す。paint が false なら高さを測るだけ。</summary>
    private int Draw(Graphics g, bool paint)
    {
        var f = CurrentFonts();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();
        var left = S(Pad);
        var width = S(LogicalWidth) - S(Pad) * 2;
        var y = S(Pad);

        if (_app.Summary is { } summary)
        {
            y = DrawHeader(g, paint, f, summary, left, width, y);
            y = DrawTiles(g, paint, f, summary, left, width, y);
            y = DrawChart(g, paint, f, summary, left, width, y);
            if (summary.Sources.Count > 1 || summary.Sources.Any(s => s.Label != "Windows"))
            {
                var max = summary.Sources.Max(s => s.Usd);
                y = DrawBreakdown(g, paint, f, "環境別（今月）", summary.Sources.Select(s => (
                    s.Label,
                    _app.Amounts(s.Usd).Primary,
                    TokensText(s.Tokens),
                    max > 0 ? s.Usd / max : 0)).ToList(), left, width, y);
            }
            if (summary.Models.Count > 0)
            {
                var max = summary.Models.Max(m => m.Usd ?? 0);
                y = DrawBreakdown(g, paint, f, "モデル別（今月）", summary.Models.Select(m => (
                    Format.ModelName(m.Model),
                    m.Usd is { } usd ? _app.Amounts(usd).Primary : "単価未登録",
                    TokensText(m.Tokens),
                    m.Usd is { } u && max > 0 ? u / max : 0)).ToList(), left, width, y);
            }
            if (summary.Projects.Count > 0)
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var max = summary.Projects.Max(p => p.Usd);
                y = DrawBreakdown(g, paint, f, "プロジェクト別（今月・上位5件）", summary.Projects.Select(p => (
                    Format.ProjectName(p.Project, home),
                    _app.Amounts(p.Usd).Primary,
                    TokensText(p.Tokens),
                    max > 0 ? p.Usd / max : 0)).ToList(), left, width, y);
            }
            y = DrawWarnings(g, paint, f, summary, left, width, y);
        }
        else
        {
            var text = _app.Progress is { } p ? $"集計中…  {p.Done} / {p.Total} ファイル" : "集計中…";
            var size = TextSize(g, text, f.Body);
            if (paint) Put(g, text, f.Body, _theme.SubText, new Rectangle(left, y + S(8), width, size.Height));
            y += size.Height + S(16);
            if (_app.Progress is { Total: > 0 } progress)
            {
                var track = new RectangleF(left, y, width, S(4));
                if (paint)
                {
                    Drawing.FillRounded(g, _theme.Track, track, S(2));
                    Drawing.FillRounded(g, _theme.Accent,
                        track with { Width = Math.Max(S(4), width * progress.Done / (float)progress.Total) }, S(2));
                }
                y += S(4) + S(16);
            }
        }

        return DrawFooter(g, paint, f, left, width, y);
    }

    private int DrawHeader(Graphics g, bool paint, Fonts f, Summary s, int left, int width, int y)
    {
        var date = DateTime.Now.ToString("M月d日（ddd）", CultureInfo.GetCultureInfo("ja-JP"));
        var labelHeight = TextSize(g, "今日", f.Caption).Height;
        if (paint)
        {
            Put(g, "今日", f.Caption, _theme.SubText, new Rectangle(left, y, width, labelHeight));
            Put(g, date, f.Caption, _theme.SubText, new Rectangle(left, y, width, labelHeight), TextFormatFlags.Right);
        }
        y += labelHeight + S(2);

        var (primary, secondary) = _app.Amounts(s.Today.Usd);
        var hero = TextSize(g, primary, f.Hero);
        if (paint) Put(g, primary, f.Hero, _theme.Text, new Rectangle(left - S(1), y, width, hero.Height));
        y += hero.Height;
        var subText = secondary is null ? TokensText(s.Today.Tokens) : $"{secondary}  ・  {TokensText(s.Today.Tokens)}";
        var sub = TextSize(g, subText, f.Body);
        if (paint) Put(g, subText, f.Body, _theme.SubText, new Rectangle(left, y, width, sub.Height));
        y += sub.Height;
        return y + S(14);
    }

    private int DrawTiles(Graphics g, bool paint, Fonts f, Summary s, int left, int width, int y)
    {
        var tiles = new (string Title, double Usd, string Note, bool Danger)[]
        {
            ("今週", s.Weeks.Count > 0 ? s.Weeks[^1].Usd : 0, s.Weeks.Count > 0 ? TokensText(s.Weeks[^1].Tokens) : "", false),
            ("今月", s.MonthUsd, TokensText(s.MonthTokens), false),
            ("月末見込み", s.ForecastUsd, _app.Settings.MonthlyBudget is { } b ? "予算 " + _app.Amounts(b).Primary : "", _app.IsOverBudget),
        };
        var gap = S(8);
        var inner = S(10);
        var tileWidth = (width - gap * (tiles.Length - 1)) / tiles.Length;
        var captionHeight = TextSize(g, "今", f.Caption).Height;
        var statHeight = TextSize(g, "$0", f.Stat).Height;
        var hasSecondary = _app.Amounts(0).Secondary is not null;
        var tileHeight = inner * 2 + captionHeight + S(2) + statHeight + (hasSecondary ? captionHeight : 0) + S(2) + captionHeight;

        if (paint)
        {
            for (var i = 0; i < tiles.Length; i++)
            {
                var (title, usd, note, danger) = tiles[i];
                var rect = new Rectangle(left + i * (tileWidth + gap), y, tileWidth, tileHeight);
                Drawing.FillRounded(g, _theme.Card, rect, S(8));
                Drawing.StrokeRounded(g, _theme.CardBorder, new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), S(8));
                var x = rect.X + inner;
                var w = rect.Width - inner * 2;
                var ty = rect.Y + inner;
                Put(g, title, f.Caption, _theme.SubText, new Rectangle(x, ty, w, captionHeight), TextFormatFlags.EndEllipsis);
                ty += captionHeight + S(2);
                var (primary, secondary) = _app.Amounts(usd);
                Put(g, primary, f.Stat, danger ? _theme.Danger : _theme.Text, new Rectangle(x, ty, w, statHeight), TextFormatFlags.EndEllipsis);
                ty += statHeight;
                if (secondary is not null)
                {
                    Put(g, secondary, f.Caption, _theme.SubText, new Rectangle(x, ty, w, captionHeight), TextFormatFlags.EndEllipsis);
                    ty += captionHeight;
                }
                Put(g, note, f.Caption, _theme.Faint, new Rectangle(x, ty + S(2), w, captionHeight), TextFormatFlags.EndEllipsis);
            }
        }
        return y + tileHeight + S(20);
    }

    private int DrawChart(Graphics g, bool paint, Fonts f, Summary s, int left, int width, int y)
    {
        // 見出しと期間の切り替え
        var title = _showsWeeks ? "直近8週" : "直近14日";
        var segmentLabels = new[] { "日別", "週別" };
        var segmentWidth = segmentLabels.Max(l => TextSize(g, l, f.Caption).Width) + S(20);
        var segmentHeight = TextSize(g, "日", f.Caption).Height + S(8);
        var track = new Rectangle(left + width - segmentWidth * 2 - S(4), y, segmentWidth * 2 + S(4), segmentHeight + S(4));
        for (var i = 0; i < 2; i++)
        {
            var rect = new Rectangle(track.X + S(2) + i * segmentWidth, track.Y + S(2), segmentWidth, segmentHeight);
            _hits.Add((rect, i == 0 ? Hit.SegmentDays : Hit.SegmentWeeks));
        }
        if (paint)
        {
            var titleHeight = TextSize(g, title, f.BodyStrong).Height;
            Put(g, title, f.BodyStrong, _theme.Text,
                new Rectangle(left, track.Y + (track.Height - titleHeight) / 2, width, titleHeight));
            Drawing.FillRounded(g, _theme.Track, track, S(6));
            for (var i = 0; i < 2; i++)
            {
                var rect = new Rectangle(track.X + S(2) + i * segmentWidth, track.Y + S(2), segmentWidth, segmentHeight);
                var selected = (i == 1) == _showsWeeks;
                if (selected)
                {
                    Drawing.FillRounded(g, _theme.Thumb, rect, S(5));
                    if (!_theme.IsDark) Drawing.StrokeRounded(g, _theme.CardBorder, new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), S(5));
                }
                var hot = _hot == (i == 0 ? Hit.SegmentDays : Hit.SegmentWeeks) && !selected;
                Put(g, segmentLabels[i], f.Caption, selected || hot ? _theme.Text : _theme.SubText, rect,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        y = track.Bottom + S(10);

        // グラフ
        var bars = _showsWeeks ? s.Weeks : s.Days;
        var captionHeight = TextSize(g, "0", f.Caption).Height;
        var chart = new Rectangle(left, y, width, S(124));
        var plot = Rectangle.FromLTRB(chart.Left, chart.Top + captionHeight + S(4), chart.Right, chart.Bottom - captionHeight - S(6));
        _plot = plot;
        _barCount = bars.Count;
        if (paint && bars.Count > 0) PaintBars(g, f, bars, plot, captionHeight);
        return chart.Bottom + S(18);
    }

    private void PaintBars(Graphics g, Fonts f, IReadOnlyList<DayBar> bars, Rectangle plot, int captionHeight)
    {
        var max = bars.Max(b => b.Usd);
        var slot = plot.Width / (float)bars.Count;
        var barWidth = Math.Max(S(3), slot * 0.62f);

        using (var grid = new Pen(_theme.Separator) { DashStyle = DashStyle.Dash })
        {
            if (max > 0)
            {
                g.DrawLine(grid, plot.Left, plot.Top, plot.Right, plot.Top);
                g.DrawLine(grid, plot.Left, plot.Top + plot.Height / 2f, plot.Right, plot.Top + plot.Height / 2f);
                Put(g, Format.Usd(max), f.Caption, _theme.Faint,
                    new Rectangle(plot.Left, plot.Top - captionHeight - S(3), plot.Width, captionHeight), TextFormatFlags.Right);
            }
        }
        using (var baseline = new Pen(_theme.CardBorder))
        {
            g.DrawLine(baseline, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }

        if (max <= 0)
        {
            Put(g, "この期間の利用はありません", f.Caption, _theme.Faint, plot,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using var normal = new SolidBrush(_theme.Accent);
            using var hover = new SolidBrush(_theme.AccentHover);
            for (var i = 0; i < bars.Count; i++)
            {
                if (bars[i].Usd <= 0) continue;
                var height = Math.Max(S(3), (float)(bars[i].Usd / max * plot.Height));
                var rect = new RectangleF(plot.Left + i * slot + (slot - barWidth) / 2, plot.Bottom - height, barWidth, height);
                using var path = Drawing.TopRoundedRect(rect, Math.Min(S(3), barWidth / 2));
                g.FillPath(i == _hoverBar ? hover : normal, path);
            }
        }

        // 日付のラベル（今日・今週から数えて一定間隔）
        var every = _showsWeeks ? 2 : 3;
        for (var i = 0; i < bars.Count; i++)
        {
            if ((bars.Count - 1 - i) % every != 0) continue;
            var label = Format.ShortDay(bars[i].Date);
            var size = TextSize(g, label, f.Caption);
            var center = plot.Left + i * slot + slot / 2;
            var x = (int)Math.Clamp(center - size.Width / 2f, plot.Left, plot.Right - size.Width);
            Put(g, label, f.Caption, i == _hoverBar ? _theme.Text : _theme.Faint,
                new Rectangle(x, plot.Bottom + S(5), size.Width, size.Height));
        }

        if (_hoverBar >= 0 && _hoverBar < bars.Count) PaintBubble(g, f, bars[_hoverBar], plot, slot, max);
    }

    private void PaintBubble(Graphics g, Fonts f, DayBar bar, Rectangle plot, float slot, double max)
    {
        var title = _showsWeeks ? $"{Format.ShortDay(bar.Date)} からの週" : Format.ShortDay(bar.Date);
        var (primary, secondary) = _app.Amounts(bar.Usd);
        var amount = secondary is null ? primary : $"{primary}  {secondary}";
        title += "  ・  " + TokensText(bar.Tokens);
        var titleSize = TextSize(g, title, f.Caption);
        var amountSize = TextSize(g, amount, f.BodyStrong);
        var pad = S(8);
        var box = new Rectangle(0, 0,
            Math.Max(titleSize.Width, amountSize.Width) + pad * 2,
            titleSize.Height + S(2) + amountSize.Height + S(6) * 2);

        var center = plot.Left + _hoverBar * slot + slot / 2;
        var barTop = max > 0 ? plot.Bottom - (float)(bar.Usd / max * plot.Height) : plot.Bottom;
        box.X = (int)Math.Clamp(center - box.Width / 2f, plot.Left, Math.Max(plot.Left, plot.Right - box.Width - 1));
        box.Y = (int)Math.Max(plot.Top - S(18), barTop - box.Height - S(6));

        Drawing.FillRounded(g, _theme.Card, box, S(6));
        Drawing.StrokeRounded(g, _theme.CardBorder, box, S(6));
        Put(g, title, f.Caption, _theme.SubText, new Rectangle(box.X + pad, box.Y + S(6), titleSize.Width, titleSize.Height));
        Put(g, amount, f.BodyStrong, _theme.Text,
            new Rectangle(box.X + pad, box.Y + S(6) + titleSize.Height + S(2), amountSize.Width, amountSize.Height));
    }

    private int DrawBreakdown(Graphics g, bool paint, Fonts f, string title,
        List<(string Name, string Value, string Detail, double Share)> rows, int left, int width, int y)
    {
        var titleHeight = TextSize(g, title, f.Caption).Height;
        if (paint) Put(g, title, f.Caption, _theme.SubText, new Rectangle(left, y, width, titleHeight));
        y += titleHeight + S(8);

        var bodyHeight = TextSize(g, "あ", f.Body).Height;
        var captionHeight = TextSize(g, "0", f.Caption).Height;
        var valueColumn = S(64);
        var detailColumn = S(76);
        foreach (var (name, value, detail, share) in rows)
        {
            if (paint)
            {
                var valueWidth = Math.Max(valueColumn, TextSize(g, value, f.BodyStrong).Width);
                var nameWidth = width - valueWidth - detailColumn - S(8);
                Put(g, FitKeepingEnd(g, name, f.Body, nameWidth), f.Body, _theme.Text, new Rectangle(left, y, nameWidth, bodyHeight),
                    TextFormatFlags.EndEllipsis);
                Put(g, detail, f.Caption, _theme.Faint,
                    new Rectangle(left + nameWidth, y + bodyHeight - captionHeight - S(1), detailColumn, captionHeight), TextFormatFlags.Right);
                Put(g, value, f.BodyStrong, _theme.Text, new Rectangle(left, y, width, bodyHeight), TextFormatFlags.Right);
                var track = new RectangleF(left, y + bodyHeight + S(4), width, S(3));
                Drawing.FillRounded(g, _theme.Track, track, S(1.5f));
                if (share > 0)
                {
                    Drawing.FillRounded(g, Color.FromArgb(_theme.IsDark ? 190 : 150, _theme.Accent),
                        track with { Width = Math.Max(S(3), (float)(width * share)) }, S(1.5f));
                }
            }
            y += bodyHeight + S(4) + S(3) + S(9);
        }
        return y + S(8);
    }

    private float S(float logical) => logical * DeviceDpi / 96f;

    private int DrawWarnings(Graphics g, bool paint, Fonts f, Summary s, int left, int width, int y)
    {
        var warnings = new List<string>();
        if (!s.LogsFound) warnings.Add("Claude Code のログが見つかりません");
        if (s.UnpricedModels.Count > 0)
            warnings.Add("単価未登録のため金額に含まれていません: " + string.Join(", ", s.UnpricedModels.Select(Format.ModelName)));
        if (_app.LogFormatWarning)
            warnings.Add("ログは増えていますが利用量を読み取れません。ログ形式が変わった可能性があります");
        if (_app.PricesError is { } error) warnings.Add(error);

        var iconWidth = S(20);
        foreach (var text in warnings)
        {
            var size = TextSize(g, text, f.Caption, width - iconWidth);
            if (paint)
            {
                Put(g, IconWarning, f.SmallIcon, _theme.Warning, new Rectangle(left, y + S(1), iconWidth, size.Height));
                Put(g, text, f.Caption, _theme.Warning, new Rectangle(left + iconWidth, y, width - iconWidth, size.Height),
                    TextFormatFlags.WordBreak);
            }
            y += size.Height + S(8);
        }
        return warnings.Count > 0 ? y + S(4) : y;
    }

    private int DrawFooter(Graphics g, bool paint, Fonts f, int left, int width, int y)
    {
        var right = left + width;
        var buttonSize = S(32);

        if (paint)
        {
            using var pen = new Pen(_theme.Separator);
            g.DrawLine(pen, 0, y, S(LogicalWidth), y);
        }
        y += S(8);

        var buttons = new (Hit Hit, string Glyph, bool Enabled)[]
        {
            (Hit.Refresh, IconRefresh, !_app.IsScanning),
            (Hit.Settings, IconSettings, true),
            (Hit.Quit, IconQuit, true),
        };
        var x = right - buttonSize * buttons.Length - S(2) * (buttons.Length - 1) + S(6);
        foreach (var (hit, glyph, enabled) in buttons)
        {
            var rect = new Rectangle(x, y, buttonSize, buttonSize);
            if (enabled) _hits.Add((rect, hit));
            if (paint)
            {
                if (_hot == hit && enabled) Drawing.FillRounded(g, _theme.Hover, rect, S(6));
                Put(g, glyph, f.Icon, enabled ? _theme.Text : _theme.Faint, rect,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            x += buttonSize + S(2);
        }

        if (paint)
        {
            var status = _app.IsScanning
                ? "更新中…"
                : _app.Summary is { } s ? $"{s.GeneratedAt.LocalDateTime:H:mm} 更新{SourcesText()} ・ 推計値" : "";
            var height = TextSize(g, "0", f.Caption).Height;
            Put(g, status, f.Caption, _theme.Faint,
                new Rectangle(left, y + (buttonSize - height) / 2, width - buttonSize * 3, height), TextFormatFlags.EndEllipsis);
        }
        return y + buttonSize + S(10);
    }

    private static string TokensText(long tokens) => Format.Tokens(tokens) + " tokens";

    /// <summary>プロジェクト名は末尾（フォルダ名）が大事なので、入りきらないときは頭を「…」で省く。</summary>
    private static string FitKeepingEnd(Graphics g, string text, Font font, int width)
    {
        if (TextSize(g, text, font).Width <= width) return text;
        for (var start = 1; start < text.Length; start++)
        {
            var candidate = "…" + text[start..];
            if (TextSize(g, candidate, font).Width <= width) return candidate;
        }
        return "…";
    }

    /// <summary>WSL も読んでいるときは " ・ Windows + WSL Ubuntu" のように出す。</summary>
    private string SourcesText()
    {
        var labels = _app.Sources.Select(s => s.Label).Distinct().ToList();
        return labels.Any(l => l != "Windows") ? " ・ " + string.Join(" + ", labels) : "";
    }

    /// <summary>開発用: 今の内容を画像に描く。</summary>
    public Bitmap RenderSnapshot()
    {
        var size = Measure();
        var bitmap = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(_theme.Background);
        Draw(g, paint: true);
        return bitmap;
    }

    // MARK: マウス

    private Hit HitTest(Point p)
    {
        foreach (var (rect, hit) in _hits)
        {
            if (rect.Contains(p)) return hit;
        }
        return Hit.None;
    }

    private static string? ToolTipText(Hit hit) => hit switch
    {
        Hit.Refresh => "今すぐ更新",
        Hit.Settings => "設定",
        Hit.Quit => "終了",
        _ => null,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        var bar = -1;
        if (_barCount > 0 && e.X >= _plot.Left && e.X < _plot.Right && e.Y >= _plot.Top - S(20) && e.Y <= _plot.Bottom + S(24))
        {
            bar = Math.Clamp((int)((e.X - _plot.Left) / (_plot.Width / (float)_barCount)), 0, _barCount - 1);
        }
        if (hit == _hot && bar == _hoverBar) return;
        if (hit != _hot) _toolTip.SetToolTip(this, ToolTipText(hit));
        _hot = hit;
        _hoverBar = bar;
        Cursor = hit == Hit.None ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = Hit.None;
        _hoverBar = -1;
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case Hit.SegmentDays when _showsWeeks:
                _showsWeeks = false;
                Invalidate();
                break;
            case Hit.SegmentWeeks when !_showsWeeks:
                _showsWeeks = true;
                Invalidate();
                break;
            case Hit.Refresh:
                _app.Refresh();
                break;
            case Hit.Settings:
                _hidePanel();
                _app.ShowSettings();
                break;
            case Hit.Quit:
                _app.Quit();
                break;
        }
    }

    public void ResetHover()
    {
        _hot = Hit.None;
        _hoverBar = -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts?.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
