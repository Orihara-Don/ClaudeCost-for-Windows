using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeCost.Core;

public enum CurrencyDisplay { Usd, Jpy, Both }

/// <summary>ローカル日付の "yyyy-MM-dd" / "yyyy-MM" キー。</summary>
public static class DayKey
{
    public static string Of(DateTimeOffset moment, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Of(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Month(string day) => day.Length >= 7 ? day[..7] : day;
}

public static partial class Format
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Usd(double value) => "$" + value.ToString("N2", Us);

    public static string Jpy(double usd, double rate) => "¥" + (usd * rate).ToString("N0", Us);

    /// <summary>通貨設定に応じて、主表示と副表示を返す。</summary>
    public static (string Primary, string? Secondary) Amounts(double usd, double rate, CurrencyDisplay show) => show switch
    {
        CurrencyDisplay.Usd => (Usd(usd), null),
        CurrencyDisplay.Jpy => (Jpy(usd, rate), null),
        _ => (Usd(usd), Jpy(usd, rate)),
    };

    /// <summary>"2026-10-28" → "10/28"</summary>
    public static string ShortDay(string day)
    {
        var parts = day.Split('-');
        return parts.Length == 3 && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var d)
            ? $"{m}/{d}"
            : day;
    }

    /// <summary>"claude-opus-5-5" → "opus-5-5"</summary>
    public static string ModelName(string model) =>
        model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;

    public static string Tokens(long count)
    {
        static string Short(double v) => v.ToString(v < 10 ? "0.#" : "0", Us);
        return count switch
        {
            >= 1_000_000_000 => Short(count / 1e9) + "B",
            >= 1_000_000 => Short(count / 1e6) + "M",
            >= 1_000 => Short(count / 1e3) + "K",
            _ => count.ToString(Us),
        };
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>"C--Users-me-develop-foo" のホーム部分を省いて表示する。</summary>
    public static string ProjectName(string directory, string home)
    {
        var prefix = NonAlphanumeric().Replace(home, "-");
        return directory.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
            ? directory[(prefix.Length + 1)..]
            : directory;
    }
}
