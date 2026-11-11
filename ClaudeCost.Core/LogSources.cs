using System.Diagnostics;
using System.Text;

namespace ClaudeCost.Core;

/// <summary>
/// ログ（projects フォルダ）の場所。Prefix はプロジェクト名の頭に付けて、Windows と WSL のものを区別する。
/// Windows のプロジェクト名は "C--Users-..." の形で "/" を含まないので、"wsl/Ubuntu/" と衝突しない。
/// </summary>
public sealed record LogSource(string Root, string Prefix, string Label)
{
    public bool IsWsl => Prefix.StartsWith(Wsl.ProjectPrefix, StringComparison.Ordinal);
}

/// <summary>集計するログの場所を見つける。</summary>
public static class LogSources
{
    /// <summary>
    /// Windows 側（設定 &gt; CLAUDE_CONFIG_DIR &gt; ~/.claude と ~/.config/claude）と、
    /// 起動中の WSL ディストリビューション内の ~/.claude / ~/.config/claude を返す。
    /// </summary>
    public static List<LogSource> Discover(string customPath, bool includeWsl)
    {
        var sources = new List<LogSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(LogSource source)
        {
            if (seen.Add(Path.GetFullPath(source.Root))) sources.Add(source);
        }

        if (!string.IsNullOrWhiteSpace(customPath))
        {
            Add(new LogSource(customPath, "", "Windows"));
        }
        else
        {
            Add(new LogSource(AppPaths.ProjectsDirectory(""), "", "Windows"));
            // 新しい Claude Code は XDG の場所（~/.config/claude）にも書く
            var xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "claude", "projects");
            if (Directory.Exists(xdg)) Add(new LogSource(xdg, "", "Windows"));
        }

        if (includeWsl)
        {
            foreach (var source in Wsl.Discover()) Add(source);
        }
        return sources;
    }
}

/// <summary>WSL 内の Claude Code のログを \\wsl.localhost 経由で探す。</summary>
public static class Wsl
{
    public const string ProjectPrefix = "wsl/";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(55);
    private static readonly object Gate = new();
    private static (DateTime At, List<string> Distros)? _cache;

    /// <summary>
    /// 起動中のディストリビューションだけを対象にする。
    /// 停止中のものに \\wsl.localhost でアクセスすると起動してしまうため。停止中ならログも増えない。
    /// </summary>
    public static List<LogSource> Discover()
    {
        var sources = new List<LogSource>();
        if (!OperatingSystem.IsWindows()) return sources;
        foreach (var distro in RunningDistros())
        {
            var share = ShareRoot(distro);
            if (share is null) continue;
            foreach (var home in HomeDirectories(share))
            {
                foreach (var relative in new[] { Path.Combine(".claude", "projects"), Path.Combine(".config", "claude", "projects") })
                {
                    var root = Path.Combine(home, relative);
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                    sources.Add(new LogSource(root, $"{ProjectPrefix}{distro}/", $"WSL {distro}"));
                }
            }
        }
        return sources;
    }

    private static string? ShareRoot(string distro)
    {
        foreach (var host in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            var path = host + distro;
            try
            {
                if (Directory.Exists(path)) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<string> HomeDirectories(string share)
    {
        var homes = new List<string>();
        try
        {
            var home = Path.Combine(share, "home");
            if (Directory.Exists(home)) homes.AddRange(Directory.EnumerateDirectories(home));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        homes.Add(Path.Combine(share, "root"));
        return homes;
    }

    /// <summary>wsl.exe --list --running --quiet の結果。WSL が無い・失敗したときは空。</summary>
    public static List<string> RunningDistros()
    {
        lock (Gate)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < CacheFor) return c.Distros;
            var distros = QueryRunningDistros();
            _cache = (DateTime.UtcNow, distros);
            return distros;
        }
    }

    private static List<string> QueryRunningDistros()
    {
        var result = new List<string>();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(exe)) return result;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--list --running --quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // wsl.exe の出力は UTF-16LE
                StandardOutputEncoding = Encoding.Unicode,
            });
            if (process is null) return result;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return result;
            }
            if (process.ExitCode != 0) return result;
            result.AddRange(ParseDistroList(output.Result));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
        }
        return result;
    }

    /// <summary>1行1つのディストリビューション名。Docker Desktop の内部用は除く。</summary>
    public static IEnumerable<string> ParseDistroList(string output) =>
        output.Replace("\0", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0
                && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}
