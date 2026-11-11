namespace ClaudeCost.Core;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\ClaudeCost（集計結果。端末ごとのデータ）</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCost");

    public static string State => Path.Combine(DataDirectory, "state.json");
    public static string Settings => Path.Combine(DataDirectory, "settings.json");
    public static string UserPrices => Path.Combine(DataDirectory, "prices.json");

    /// <summary>設定 &gt; CLAUDE_CONFIG_DIR &gt; ~/.claude の順で決める。</summary>
    public static string ProjectsDirectory(string customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath)) return customPath;
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(config)) return Path.Combine(config, "projects");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    }
}
