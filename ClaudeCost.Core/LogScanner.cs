using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCost.Core;

/// <summary>
/// Claude Code のログ（*.jsonl）を差分で読み、日別・モデル別・プロジェクト別のトークン数を集計する。
/// 1つのインスタンスを同時に複数のスレッドから呼ばないこと（呼び出し側で直列化する）。
/// </summary>
public sealed class LogScanner
{
    public static readonly TimeSpan SeenRetention = TimeSpan.FromDays(31);
    public static readonly TimeSpan FormatWarningInterval = TimeSpan.FromHours(1);
    private const int ChunkSize = 4 << 20;
    private static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();

    private readonly string _statePath;
    private readonly TimeZoneInfo _timeZone;
    private UsageStore? _store;

    public LogScanner(string statePath, TimeZoneInfo? timeZone = null)
    {
        _statePath = statePath;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public ScanResult ScanOnce(string root, DateTimeOffset now, Action<ScanProgress?>? onProgress = null) =>
        ScanOnce([new LogSource(root, "", "")], now, onProgress);

    public ScanResult ScanOnce(IReadOnlyList<LogSource> sources, DateTimeOffset now, Action<ScanProgress?>? onProgress = null)
    {
        var store = _store ?? UsageStore.Load(_statePath);
        _store = null;
        try
        {
            return Scan(store, sources, now, onProgress);
        }
        finally
        {
            _store = store;
        }
    }

    private sealed record LogFile(string Path, long Length, long Modified, string Project);

    private ScanResult Scan(UsageStore store, IReadOnlyList<LogSource> sources, DateTimeOffset now, Action<ScanProgress?>? onProgress)
    {
        var isInitial = store.Cursors.Count == 0;
        var files = new List<LogFile>();
        // 一覧を取れた場所。取れなかった場所（WSL の停止など）の読み取り位置は消さずに残す
        var listedRoots = new List<string>();
        foreach (var source in sources)
        {
            if (ListLogFiles(source) is not { } listed) continue;
            listedRoots.Add(EnsureTrailingSeparator(Path.GetFullPath(source.Root)));
            files.AddRange(listed);
        }
        var dirty = false;
        var foundUsage = false;

        for (var i = 0; i < files.Count; i++)
        {
            if (isInitial) onProgress?.Invoke(new ScanProgress(i, files.Count));
            var (moved, usage) = Ingest(files[i], now, store);
            dirty |= moved;
            foundUsage |= usage;
        }
        if (isInitial) onProgress?.Invoke(null);

        var existing = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var stale = store.Cursors.Keys
            .Where(p => !existing.Contains(p)
                && listedRoots.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var path in stale) store.Cursors.Remove(path);

        var cutoff = (now - SeenRetention).ToUnixTimeSeconds();
        var countBefore = store.Seen.Count;
        foreach (var key in store.Seen.Where(e => e.Value < cutoff).Select(e => e.Key).ToList())
        {
            store.Seen.Remove(key);
        }

        if (foundUsage)
        {
            store.GrowthWithoutUsageStart = null;
            store.GrowthWithoutUsageLast = null;
        }
        else if (dirty)
        {
            store.GrowthWithoutUsageStart ??= now;
            store.GrowthWithoutUsageLast = now;
        }

        if (dirty || stale.Count > 0 || countBefore != store.Seen.Count)
        {
            try
            {
                store.Save(_statePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"ClaudeCost: state.json の保存に失敗: {e.Message}");
            }
        }

        var warning = store.GrowthWithoutUsageStart is { } start
            && store.GrowthWithoutUsageLast is { } last
            && last - start >= FormatWarningInterval;
        return new ScanResult(store.Rows, files.Count > 0, warning);
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>フォルダが無い・読めないときは null。</summary>
    private static List<LogFile>? ListLogFiles(LogSource source)
    {
        var root = source.Root;
        try
        {
            if (!Directory.Exists(root)) return null;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            // 大きさも一覧から取り、変わっていないファイルは開かずに済ませる（WSL のネットワーク越しでは開くのが遅い）
            return new DirectoryInfo(root).EnumerateFiles("*.jsonl", options)
                .Select(f => new LogFile(Path.GetFullPath(f.FullName), f.Length, f.LastWriteTimeUtc.Ticks, source.Prefix + ProjectName(f.FullName, root)))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ProjectName(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 ? parts[0] : "(unknown)";
    }

    /// <summary>読めないファイルは位置を進めず、次回に再試行する。</summary>
    private (bool Moved, bool FoundUsage) Ingest(LogFile log, DateTimeOffset now, UsageStore store)
    {
        var file = log.Path;
        if (store.Cursors.TryGetValue(file, out var known) && known.Offset == log.Length && known.Modified == log.Modified)
            return (false, false);

        FileStream stream;
        try
        {
            // Claude Code が書き込み中でも読めるよう、共有を許可して開く
            stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, false);
        }

        using (stream)
        {
            long size;
            string identity;
            try
            {
                size = stream.Length;
                identity = FileIdentity.Of(stream, file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (false, false);
            }

            long offset = 0;
            var isReread = false;
            if (store.Cursors.TryGetValue(file, out var cursor))
            {
                if (cursor.Identity == identity && size >= cursor.Offset)
                    offset = cursor.Offset;
                else
                    isReread = true;
            }
            if (size == offset && !isReread) return (false, false);

            var project = log.Project;
            var committed = offset;
            var foundUsage = false;
            var buffer = new byte[ChunkSize];
            var carry = new List<byte>();
            try
            {
                stream.Seek(offset, SeekOrigin.Begin);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] data;
                    if (carry.Count > 0)
                    {
                        carry.AddRange(buffer.AsSpan(0, read));
                        data = carry.ToArray();
                    }
                    else
                    {
                        data = buffer.AsSpan(0, read).ToArray();
                    }
                    var lastNewline = Array.LastIndexOf(data, (byte)'\n');
                    if (lastNewline < 0)
                    {
                        carry = new List<byte>(data);
                        continue;
                    }
                    var complete = data.AsSpan(0, lastNewline + 1);
                    if (ProcessLines(complete, project, isReread, now, store)) foundUsage = true;
                    committed += complete.Length;
                    carry = new List<byte>(data.AsSpan(lastNewline + 1).ToArray());
                }
            }
            catch (IOException)
            {
                // 読めたところまでを記録する
            }

            store.Cursors[file] = new FileCursor(committed, identity, committed == log.Length ? log.Modified : null);
            return (committed != offset || isReread, foundUsage);
        }
    }

    private bool ProcessLines(ReadOnlySpan<byte> data, string project, bool isReread, DateTimeOffset now, UsageStore store)
    {
        var foundUsage = false;
        while (!data.IsEmpty)
        {
            var end = data.IndexOf((byte)'\n');
            var line = end < 0 ? data : data[..end];
            data = end < 0 ? ReadOnlySpan<byte>.Empty : data[(end + 1)..];
            if (line.IndexOf(UsageMarker) < 0) continue;
            if (ProcessLine(line, project, isReread, now, store)) foundUsage = true;
        }
        return foundUsage;
    }

    /// <summary>形式どおりの usage 行なら true（重複や synthetic でも形式確認としては true）。</summary>
    private bool ProcessLine(ReadOnlySpan<byte> line, string project, bool isReread, DateTimeOffset now, UsageStore store)
    {
        LogLine? record;
        try
        {
            record = JsonSerializer.Deserialize(line, LogJsonContext.Default.LogLine);
        }
        catch (JsonException)
        {
            return false;
        }
        var message = record?.Message;
        var usage = message?.Usage;
        if (record is null || message is null || usage is null
            || usage.InputTokens is not { } input
            || usage.OutputTokens is not { } output
            || message.Model is not { } rawModel
            || message.Id is not { } messageId
            || record.RequestId is not { } requestId
            || record.Timestamp is not { } timestamp
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
        {
            return false;
        }

        if (rawModel == "<synthetic>") return true;

        var key = messageId + "|" + requestId;
        if (store.Seen.ContainsKey(key)) return true;

        // 重複排除キーを捨てた古い行は、読み直しでは集計済みなので数えない
        if (isReread && date - now < -SeenRetention) return true;

        store.Seen[key] = date.ToUnixTimeSeconds();

        var tokens = new TokenCounts
        {
            Input = input,
            Output = output,
            CacheRead = usage.CacheReadInputTokens ?? 0,
        };
        var write5m = usage.CacheCreation?.Ephemeral5mInputTokens ?? 0;
        var write1h = usage.CacheCreation?.Ephemeral1hInputTokens ?? 0;
        if (write5m + write1h > 0)
        {
            tokens.CacheWrite5m = write5m;
            tokens.CacheWrite1h = write1h;
        }
        else
        {
            tokens.CacheWrite5m = usage.CacheCreationInputTokens ?? 0;
        }

        var model = ModelName.ApplyingLongPromptTier(
            ModelName.Normalize(rawModel),
            tokens.Input + tokens.CacheRead + tokens.CacheWrite5m + tokens.CacheWrite1h);
        store.Add(DayKey.Of(date, _timeZone), model, project, tokens);
        return true;
    }
}

/// <summary>必要なフィールドだけを読む。未知のフィールドは無視する。</summary>
internal sealed class LogLine
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("message")] public LogMessage? Message { get; set; }
}

internal sealed class LogMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("usage")] public LogUsage? Usage { get; set; }
}

internal sealed class LogUsage
{
    [JsonPropertyName("input_tokens")] public long? InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; set; }
    [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; set; }
    [JsonPropertyName("cache_creation_input_tokens")] public long? CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cache_creation")] public LogCacheCreation? CacheCreation { get; set; }
}

internal sealed class LogCacheCreation
{
    [JsonPropertyName("ephemeral_5m_input_tokens")] public long? Ephemeral5mInputTokens { get; set; }
    [JsonPropertyName("ephemeral_1h_input_tokens")] public long? Ephemeral1hInputTokens { get; set; }
}

[JsonSerializable(typeof(LogLine))]
internal partial class LogJsonContext : JsonSerializerContext;

/// <summary>
/// ファイルが作り直されたかを判定するための識別子。
/// Windows はファイルID、それ以外（テスト用）は作成日時を使う。
/// </summary>
internal static class FileIdentity
{
    public static string Of(FileStream stream, string path)
    {
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(stream.SafeFileHandle, out var info))
        {
            return $"{info.VolumeSerialNumber:x8}-{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        }
        return File.GetCreationTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
