using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCost.Core;

/// <summary>USD／100万トークン。キャッシュ単価を省略すると入力単価から求める。</summary>
public sealed record ModelPrice
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double? CacheRead { get; init; }
    public double? CacheWrite5m { get; init; }
    public double? CacheWrite1h { get; init; }
    /// <summary>Claude 4.5仕様以降（リージョナル係数の対象）か。</summary>
    public bool? Regional { get; init; }
}

public sealed class PriceTable
{
    public const double RegionalMultiplier = 1.1;

    private sealed class PriceFile
    {
        public Dictionary<string, ModelPrice> Models { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public Dictionary<string, ModelPrice> Models { get; }

    public PriceTable(Dictionary<string, ModelPrice> models) => Models = models;

    public static PriceTable Decode(Stream json) =>
        new(JsonSerializer.Deserialize<PriceFile>(json, JsonOptions)?.Models
            ?? throw new JsonException("models がありません"));

    /// <summary>同梱の単価表（Mac版と同じ prices.json）を返す。</summary>
    public static PriceTable LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeCost.Core.prices.json")
            ?? throw new InvalidOperationException("同梱の単価表がありません");
        return Decode(stream);
    }

    /// <summary>同梱の単価表に、ユーザーの prices.json を上書きして返す。</summary>
    public static (PriceTable Table, string? Error) LoadMerged(string userPricesPath)
    {
        PriceTable table;
        string? problem = null;
        try
        {
            table = LoadBundled();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            table = new PriceTable(new());
            problem = "同梱の単価表を読み込めません";
        }
        if (File.Exists(userPricesPath))
        {
            try
            {
                using var stream = File.OpenRead(userPricesPath);
                foreach (var (key, value) in Decode(stream).Models) table.Models[key] = value;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                problem = "prices.json の形式が不正なため、同梱の単価を使っています";
            }
        }
        return (table, problem);
    }

    /// <summary>
    /// 単価表のキーと最長一致で照合する。
    /// "claude-opus-5" が "claude-opus-5-1" のような別バージョンに一致しないよう、続きが数字のものは除外する。
    /// </summary>
    public ModelPrice? PriceFor(string model)
    {
        if (Models.TryGetValue(model, out var exact)) return exact;
        string? best = null;
        foreach (var key in Models.Keys)
        {
            if (!model.StartsWith(key + "-", StringComparison.Ordinal)) continue;
            var rest = model[(key.Length + 1)..];
            var dash = rest.IndexOf('-');
            var next = dash < 0 ? rest : rest[..dash];
            if (next.All(char.IsAsciiDigit)) continue;
            if (best is null || key.Length > best.Length) best = key;
        }
        return best is null ? null : Models[best];
    }

    /// <summary>単価が未登録なら null。</summary>
    public double? Cost(TokenCounts t, string model, bool regionalPricing)
    {
        var p = PriceFor(model);
        if (p is null) return null;
        var read = p.CacheRead ?? p.Input * 0.1;
        var write5m = p.CacheWrite5m ?? p.Input * 1.25;
        var write1h = p.CacheWrite1h ?? p.Input * 2;
        var sum = t.Input * p.Input
            + t.Output * p.Output
            + t.CacheWrite5m * write5m
            + t.CacheWrite1h * write1h
            + t.CacheRead * read;
        var multiplier = regionalPricing && (p.Regional ?? true) ? RegionalMultiplier : 1;
        return sum / 1_000_000 * multiplier;
    }
}

public static partial class ModelName
{
    /// <summary>
    /// Haiku 5.5 はプロンプト長でリクエストごとに単価が変わるため、超過したものは別の行として集計する。
    /// プロンプト長にはキャッシュの読込・書込も含む。
    /// </summary>
    public static string ApplyingLongPromptTier(string model, long promptTokens) =>
        model == "claude-haiku-5-5" && promptTokens > 100_000 ? model + "-over-100k" : model;

    [GeneratedRegex(@"^[a-z]+\.")]
    private static partial Regex RegionPrefix();

    [GeneratedRegex(@"-v[0-9]+(:[0-9a-z]+)*$")]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"-[0-9]{8}$")]
    private static partial Regex DateSuffix();

    /// <summary>us.anthropic.claude-opus-5-5-v1:0 や日付付きIDを claude-opus-5-5 の形にする。</summary>
    public static string Normalize(string raw)
    {
        var name = raw;
        if (name.EndsWith("[1m]", StringComparison.Ordinal)) name = name[..^4];
        var anthropic = name.IndexOf("anthropic.", StringComparison.Ordinal);
        name = anthropic >= 0 ? name[(anthropic + "anthropic.".Length)..] : RegionPrefix().Replace(name, "", 1);
        name = VersionSuffix().Replace(name, "", 1);
        name = DateSuffix().Replace(name, "", 1);
        return name;
    }
}
