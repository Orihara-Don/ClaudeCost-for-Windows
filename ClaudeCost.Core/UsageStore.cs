using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCost.Core;

/// <summary>state.json の内容。読み取り位置、重複排除キー、全期間の日別集計を持つ。</summary>
public sealed class UsageStore
{
    public const int SchemaVersion = 1;

    public Dictionary<string, FileCursor> Cursors { get; set; } = new();
    /// <summary>重複排除キー → イベント時刻（UNIX秒）</summary>
    public Dictionary<string, long> Seen { get; set; } = new();
    public Dictionary<UsageKey, TokenCounts> Usage { get; } = new();
    /// <summary>usage 付きの行が無いままログが伸び続けている期間（形式変更の検知用）</summary>
    public DateTimeOffset? GrowthWithoutUsageStart { get; set; }
    public DateTimeOffset? GrowthWithoutUsageLast { get; set; }

    private sealed class Disk
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, FileCursor> Cursors { get; set; } = new();
        public Dictionary<string, long> Seen { get; set; } = new();
        public List<UsageRow> Rows { get; set; } = new();
        public DateTimeOffset? GrowthWithoutUsageStart { get; set; }
        public DateTimeOffset? GrowthWithoutUsageLast { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<UsageRow> Rows =>
        Usage.Select(e => new UsageRow(e.Key.Day, e.Key.Model, e.Key.Project, e.Value with { })).ToList();

    public void Add(string day, string model, string project, TokenCounts tokens)
    {
        var key = new UsageKey(day, model, project);
        if (!Usage.TryGetValue(key, out var total))
        {
            total = new TokenCounts();
            Usage[key] = total;
        }
        total.Add(tokens);
    }

    /// <summary>壊れたファイルは上書きせず退避して、空の状態から始める。</summary>
    public static UsageStore Load(string path)
    {
        if (!File.Exists(path)) return new UsageStore();
        Disk? disk;
        try
        {
            disk = JsonSerializer.Deserialize<Disk>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            disk = null;
        }
        if (disk is null || disk.SchemaVersion != SchemaVersion)
        {
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
            return new UsageStore();
        }
        var store = new UsageStore
        {
            Cursors = disk.Cursors,
            Seen = disk.Seen,
            GrowthWithoutUsageStart = disk.GrowthWithoutUsageStart,
            GrowthWithoutUsageLast = disk.GrowthWithoutUsageLast,
        };
        foreach (var row in disk.Rows) store.Add(row.Day, row.Model, row.Project, row.Tokens);
        return store;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var disk = new Disk
        {
            SchemaVersion = SchemaVersion,
            Cursors = Cursors,
            Seen = Seen,
            Rows = Rows,
            GrowthWithoutUsageStart = GrowthWithoutUsageStart,
            GrowthWithoutUsageLast = GrowthWithoutUsageLast,
        };
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(disk, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
