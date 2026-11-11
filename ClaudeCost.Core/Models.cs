namespace ClaudeCost.Core;

public sealed record TokenCounts
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite5m { get; set; }
    public long CacheWrite1h { get; set; }
    public long CacheRead { get; set; }

    public long Total => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    public void Add(TokenCounts other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheWrite5m += other.CacheWrite5m;
        CacheWrite1h += other.CacheWrite1h;
        CacheRead += other.CacheRead;
    }
}

public sealed record UsageRow(string Day, string Model, string Project, TokenCounts Tokens);

public readonly record struct UsageKey(string Day, string Model, string Project);

/// <summary>Modified は最後に読んだときの更新日時（UTC ticks）。大きさと更新日時が同じなら開かずに済ませる。</summary>
public sealed record FileCursor(long Offset, string Identity, long? Modified = null);

public sealed record ScanProgress(int Done, int Total);

public sealed record ScanResult(IReadOnlyList<UsageRow> Rows, bool LogsFound, bool LogFormatWarning);
