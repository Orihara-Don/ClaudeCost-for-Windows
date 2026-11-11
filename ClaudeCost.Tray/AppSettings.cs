using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCost.Core;
using Microsoft.Win32;

namespace ClaudeCost.Tray;

/// <summary>%LOCALAPPDATA%\ClaudeCost\settings.json に保存する設定。</summary>
internal sealed record AppSettings
{
    public CurrencyDisplay Currency { get; init; } = CurrencyDisplay.Both;
    public double JpyRate { get; init; } = 150;
    public bool RegionalPricing { get; init; } = true;
    public double? MonthlyBudget { get; init; }
    /// <summary>空なら既定の場所を使う</summary>
    public string CustomLogPath { get; init; } = "";
    public bool DidAskLaunchAtLogin { get; init; }
    /// <summary>起動中の WSL ディストリビューション内のログも集計する</summary>
    public bool IncludeWsl { get; init; } = true;

    [JsonIgnore]
    public string ProjectsDirectory => AppPaths.ProjectsDirectory(CustomLogPath);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), JsonOptions);
                if (loaded is not null) return loaded.JpyRate > 0 ? loaded : loaded with { JpyRate = 150 };
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(e);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(e);
        }
    }
}

/// <summary>ログイン時の起動（HKCU の Run キー）。</summary>
internal static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeCost";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>exe を移動した場合に備えて、登録済みならパスを今の場所に合わせる。</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command)
        {
            key.SetValue(ValueName, Command);
        }
    }
}
