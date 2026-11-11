using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace ClaudeCost.Tray;

/// <summary>Windows 11 のフライアウトに近い配色。アプリのライト／ダーク設定に合わせる。</summary>
internal sealed record Theme(
    bool IsDark,
    Color Background,
    Color Card,
    Color CardBorder,
    Color Text,
    Color SubText,
    Color Faint,
    Color Separator,
    Color Hover,
    Color Track,
    Color Thumb,
    Color Accent,
    Color AccentHover,
    Color Danger,
    Color Warning)
{
    public static readonly Theme Light = new(
        IsDark: false,
        Background: Color.FromArgb(249, 249, 249),
        Card: Color.White,
        CardBorder: Color.FromArgb(229, 229, 229),
        Text: Color.FromArgb(26, 26, 26),
        SubText: Color.FromArgb(96, 96, 96),
        Faint: Color.FromArgb(160, 160, 160),
        Separator: Color.FromArgb(229, 229, 229),
        Hover: Color.FromArgb(236, 236, 236),
        Track: Color.FromArgb(236, 236, 236),
        Thumb: Color.White,
        Accent: Color.FromArgb(217, 119, 87),
        AccentHover: Color.FromArgb(188, 92, 62),
        Danger: Color.FromArgb(196, 43, 28),
        Warning: Color.FromArgb(157, 93, 0));

    public static readonly Theme Dark = new(
        IsDark: true,
        Background: Color.FromArgb(39, 39, 39),
        Card: Color.FromArgb(50, 50, 50),
        CardBorder: Color.FromArgb(64, 64, 64),
        Text: Color.White,
        SubText: Color.FromArgb(200, 200, 200),
        Faint: Color.FromArgb(130, 130, 130),
        Separator: Color.FromArgb(58, 58, 58),
        Hover: Color.FromArgb(56, 56, 56),
        Track: Color.FromArgb(29, 29, 29),
        Thumb: Color.FromArgb(70, 70, 70),
        Accent: Color.FromArgb(226, 138, 108),
        AccentHover: Color.FromArgb(240, 165, 138),
        Danger: Color.FromArgb(255, 153, 164),
        Warning: Color.FromArgb(252, 225, 0));

    public static Theme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Light;
        }
    }
}

internal static class Drawing
{
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>上の角だけ丸めた四角（棒グラフ用）。</summary>
    public static GraphicsPath TopRoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height * 2));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundedRect(r, radius);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var pen = new Pen(color);
        using var path = RoundedRect(r, radius);
        g.DrawPath(pen, path);
    }

    private static HashSet<string>? _installed;

    /// <summary>候補のうち、インストールされている最初のフォント名を返す。</summary>
    public static string FirstInstalled(params string[] names)
    {
        if (_installed is null)
        {
            using var fonts = new InstalledFontCollection();
            _installed = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return names.FirstOrDefault(_installed.Contains) ?? names[^1];
    }
}
