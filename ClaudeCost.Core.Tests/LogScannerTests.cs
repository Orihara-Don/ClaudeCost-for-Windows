using ClaudeCost.Core;
using Xunit;

namespace ClaudeCost.Core.Tests;

public sealed class LogScannerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _root;
    private readonly string _statePath;
    private readonly TimeZoneInfo _tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");

    public LogScannerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _root = Path.Combine(_directory, "projects");
        _statePath = Path.Combine(_directory, "state.json");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string LogFile(string relative = "proj/session.jsonl")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string Line(string id, long input = 10, string time = "2026-11-10T01:00:00.000Z", string model = "claude-opus-5-5") =>
        "{\"timestamp\":\"" + time + "\",\"requestId\":\"req_" + id + "\",\"message\":{\"id\":\"msg_" + id
        + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":1}}}";

    private static void Append(string text, string path) => File.AppendAllText(path, text);

    private LogScanner Scanner() => new(_statePath, _tokyo);

    private static long TotalInput(ScanResult result) => result.Rows.Sum(r => r.Tokens.Input);

    [Fact]
    public void FixtureDedupesAndSkipsBadLines()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic.jsonl"), LogFile());

        var result = Scanner().ScanOnce(_root, _now);

        Assert.Equal(2, result.Rows.Count); // synthetic と欠損行は除外され、モデルは2種類
        var opus = Assert.Single(result.Rows, r => r.Model == "claude-opus-5-5");
        Assert.Equal(new TokenCounts { Input = 1000, Output = 2000, CacheWrite5m = 1000, CacheWrite1h = 3000, CacheRead = 3000 }, opus.Tokens);
        Assert.Equal("2026-11-10", opus.Day);
        Assert.Equal("proj", opus.Project);
        var haiku = Assert.Single(result.Rows, r => r.Model == "claude-haiku-4-5");
        Assert.Equal(300, haiku.Tokens.CacheWrite5m); // cache_creation が無ければ全部5分
    }

    [Fact]
    public void PartialLineIsReadNextTime()
    {
        var file = LogFile();
        var scanner = Scanner();
        var whole = Line("1");
        Append(Line("0") + "\n" + whole[..20], file);

        Assert.Equal(10, TotalInput(scanner.ScanOnce(_root, _now)));

        Append(whole[20..], file);
        Assert.Equal(10, TotalInput(scanner.ScanOnce(_root, _now))); // 改行がまだ無い

        Append("\n", file);
        Assert.Equal(20, TotalInput(scanner.ScanOnce(_root, _now)));
    }

    [Fact]
    public void RescanAfterRestartReadsOnlyDifference()
    {
        var file = LogFile();
        Append(Line("1") + "\n", file);
        Scanner().ScanOnce(_root, _now);

        Append(Line("2") + "\n", file);
        var result = Scanner().ScanOnce(_root, _now);
        Assert.Equal(20, TotalInput(result));
        var store = UsageStore.Load(_statePath);
        Assert.Equal(new FileInfo(file).Length, store.Cursors[Path.GetFullPath(file)].Offset);
    }

    [Fact]
    public void RecreatedFileIsReadFromStartWithoutDoubleCount()
    {
        var file = LogFile();
        var scanner = Scanner();
        Append(Line("1") + "\n" + Line("2") + "\n", file);
        scanner.ScanOnce(_root, _now);

        File.Delete(file);
        Thread.Sleep(20); // 作成日時で識別する環境（Windows 以外）向け
        Append(Line("1") + "\n" + Line("3") + "\n", file);
        Assert.Equal(30, TotalInput(scanner.ScanOnce(_root, _now)));
    }

    [Fact]
    public void ShrunkFileIsReadFromStart()
    {
        var file = LogFile();
        var scanner = Scanner();
        Append(Line("1") + "\n" + Line("2") + "\n", file);
        scanner.ScanOnce(_root, _now);

        using (var stream = new FileStream(file, FileMode.Open)) stream.SetLength(0);
        Append(Line("3") + "\n", file);
        Assert.Equal(30, TotalInput(scanner.ScanOnce(_root, _now)));
    }

    [Fact]
    public void HistorySurvivesLogDeletion()
    {
        var file = LogFile();
        Append(Line("1") + "\n", file);
        Scanner().ScanOnce(_root, _now);

        File.Delete(file);
        var result = Scanner().ScanOnce(_root, _now);
        Assert.Equal(10, TotalInput(result));
        Assert.False(result.LogsFound);
    }

    [Fact]
    public void OldKeysArePrunedButRereadDoesNotDoubleCount()
    {
        var file = LogFile();
        var scanner = Scanner();
        Append(Line("old", time: "2026-09-01T00:00:00.000Z") + "\n", file);
        Assert.Equal(10, TotalInput(scanner.ScanOnce(_root, _now)));
        Assert.Empty(UsageStore.Load(_statePath).Seen);

        File.Delete(file);
        Thread.Sleep(20);
        Append(Line("old", time: "2026-09-01T00:00:00.000Z") + "\n", file);
        Assert.Equal(10, TotalInput(scanner.ScanOnce(_root, _now)));
    }

    [Fact]
    public void SubagentLogsUseFirstPathComponentAsProject()
    {
        Append(Line("1") + "\n", LogFile("proj-a/session/subagents/agent.jsonl"));
        Assert.Equal("proj-a", Scanner().ScanOnce(_root, _now).Rows[0].Project);
    }

    [Fact]
    public void HaikuLongPromptIsCountedSeparately()
    {
        const string cacheHeavy = """{"timestamp":"2026-11-10T01:00:00.000Z","requestId":"req_c","message":{"id":"msg_c","model":"claude-haiku-5-5","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":100000}}}""";
        string[] lines =
        [
            Line("a", input: 100_000, model: "claude-haiku-5-5"),
            cacheHeavy,
            Line("o", input: 200_000, model: "claude-opus-5-5"),
        ];
        Append(string.Join("\n", lines) + "\n", LogFile());
        var rows = Scanner().ScanOnce(_root, _now).Rows;
        Assert.Equal(
            new[] { "claude-haiku-5-5", "claude-haiku-5-5-over-100k", "claude-opus-5-5" },
            rows.Select(r => r.Model).Order().ToArray());
        Assert.Equal(100_000, rows.Single(r => r.Model == "claude-haiku-5-5").Tokens.Input); // ちょうど10万は通常単価
        Assert.Equal(100_000, rows.Single(r => r.Model == "claude-haiku-5-5-over-100k").Tokens.CacheRead); // キャッシュ読込も数える
    }

    [Fact]
    public void DayUsesLocalTimeZone()
    {
        Append(Line("1", time: "2026-11-09T16:00:00.000Z") + "\n", LogFile());
        Assert.Equal("2026-11-10", Scanner().ScanOnce(_root, _now).Rows[0].Day); // JSTでは翌日の1時
    }

    [Fact]
    public void WarnsWhenLogsGrowWithoutUsage()
    {
        var file = LogFile();
        var scanner = Scanner();
        Append("""{"type":"user","new_format":true}""" + "\n", file);
        Assert.False(scanner.ScanOnce(_root, _now).LogFormatWarning);

        Append("""{"type":"user","new_format":true}""" + "\n", file);
        Assert.True(scanner.ScanOnce(_root, _now.AddSeconds(3700)).LogFormatWarning);

        Append(Line("1") + "\n", file);
        Assert.False(scanner.ScanOnce(_root, _now.AddSeconds(3800)).LogFormatWarning);
    }

    [Fact]
    public void MultipleSourcesArePrefixedAndDeduped()
    {
        var wslRoot = Path.Combine(_directory, "wsl-projects");
        var wslFile = Path.Combine(wslRoot, "-home-me-app", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(wslFile)!);
        Append(Line("w") + "\n" + Line("1") + "\n", wslFile);
        Append(Line("1") + "\n", LogFile());

        var sources = new[] { new LogSource(_root, "", "Windows"), new LogSource(wslRoot, "wsl/Ubuntu/", "WSL Ubuntu") };
        var rows = Scanner().ScanOnce(sources, _now).Rows;

        Assert.Equal(20, rows.Sum(r => r.Tokens.Input)); // 同じメッセージは片方だけ数える
        Assert.Contains(rows, r => r.Project == "wsl/Ubuntu/-home-me-app");
        Assert.Contains(rows, r => r.Project == "proj");
    }

    [Fact]
    public void UnreachableSourceKeepsCursors()
    {
        var wslRoot = Path.Combine(_directory, "wsl-projects");
        var wslFile = Path.Combine(wslRoot, "p", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(wslFile)!);
        Append(Line("old", time: "2026-09-01T00:00:00.000Z") + "\n", wslFile);
        var scanner = Scanner();
        var both = new[] { new LogSource(_root, "", ""), new LogSource(wslRoot, "wsl/U/", "") };
        Assert.Equal(10, TotalInput(scanner.ScanOnce(both, _now)));

        // ディストリビューションが止まって一覧に出てこない間も、読み取り位置は残る
        Assert.Equal(10, TotalInput(scanner.ScanOnce(new[] { new LogSource(_root, "", "") }, _now)));
        Assert.Equal(10, TotalInput(scanner.ScanOnce(both, _now)));
    }

    [Fact]
    public void ParsesWslDistroList()
    {
        Assert.Equal(new[] { "Ubuntu", "Debian" }, Wsl.ParseDistroList("Ubuntu\r\ndocker-desktop\r\nDebian\r\n\r\n").ToArray());
    }

    [Fact]
    public void WslProjectNames()
    {
        Assert.Equal("app（WSL）", Format.ProjectName("wsl/Ubuntu/-home-me-app", @"C:\Users\me"));
        Assert.Equal("~（WSL）", Format.ProjectName("wsl/Ubuntu/-home-me", @"C:\Users\me"));
        Assert.Equal("srv-x（WSL）", Format.ProjectName("wsl/Debian/-root-srv-x", @"C:\Users\me"));
    }

    [Fact]
    public void CrlfLinesAreRead()
    {
        Append(Line("1") + "\r\n" + Line("2") + "\r\n", LogFile());
        Assert.Equal(20, TotalInput(Scanner().ScanOnce(_root, _now)));
    }
}
