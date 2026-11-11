namespace ClaudeCost.Core;

public sealed record DayBar(string Date, double Usd, long Tokens = 0);

public sealed record ModelRow(string Model, double? Usd, long Tokens);

public sealed record ProjectRow(string Project, double Usd, long Tokens = 0);

/// <summary>Windows／WSL のディストリビューションごとの今月の合計。</summary>
public sealed record SourceRow(string Label, double Usd, long Tokens);

/// <summary>パネルとトレイの表示に使う集計結果。金額はセント単位に丸め済み（内訳を除く）。</summary>
public sealed record Summary(
    DateTimeOffset GeneratedAt,
    DayBar Today,
    string Month,
    double MonthUsd,
    double ForecastUsd,
    // 直近14日。最後の要素が今日。
    IReadOnlyList<DayBar> Days,
    // 月曜始まりの直近8週。Date は週の開始日で、最後の要素が今週。
    IReadOnlyList<DayBar> Weeks,
    IReadOnlyList<ModelRow> Models,
    IReadOnlyList<ProjectRow> Projects,
    IReadOnlyList<string> UnpricedModels,
    bool LogsFound)
{
    public long MonthTokens { get; init; }
    public IReadOnlyList<SourceRow> Sources { get; init; } = [];
}

public static class SummaryBuilder
{
    public const int DayCount = 14;
    public const int WeekCount = 8;

    private static double Cents(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>"wsl/Ubuntu/..." → "WSL Ubuntu"、それ以外は "Windows"。</summary>
    public static string SourceLabel(string project) =>
        Format.WslProject(project) is { } wsl ? "WSL " + wsl.Distro : "Windows";

    public static Summary Build(
        IEnumerable<UsageRow> rows,
        PriceTable prices,
        bool regionalPricing,
        bool logsFound,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var todayKey = DayKey.Of(today);
        var monthKey = DayKey.Month(todayKey);

        var perDay = new Dictionary<string, double>();
        var perDayTokens = new Dictionary<string, long>();
        var perModel = new Dictionary<string, (double? Usd, long Tokens)>();
        var perProject = new Dictionary<string, (double Usd, long Tokens)>();
        var perSource = new Dictionary<string, (double Usd, long Tokens)>();
        long monthTokens = 0;

        foreach (var row in rows)
        {
            var cost = prices.Cost(row.Tokens, row.Model, regionalPricing);
            if (cost is { } c) perDay[row.Day] = perDay.GetValueOrDefault(row.Day) + c;
            perDayTokens[row.Day] = perDayTokens.GetValueOrDefault(row.Day) + row.Tokens.Total;
            if (DayKey.Month(row.Day) != monthKey) continue;
            monthTokens += row.Tokens.Total;
            var entry = perModel.GetValueOrDefault(row.Model, (null, 0));
            entry.Tokens += row.Tokens.Total;
            if (cost is { } c2) entry.Usd = (entry.Usd ?? 0) + c2;
            perModel[row.Model] = entry;

            var project = perProject.GetValueOrDefault(row.Project);
            perProject[row.Project] = (project.Usd + (cost ?? 0), project.Tokens + row.Tokens.Total);
            var label = SourceLabel(row.Project);
            var source = perSource.GetValueOrDefault(label);
            perSource[label] = (source.Usd + (cost ?? 0), source.Tokens + row.Tokens.Total);
        }

        var monthUsd = perDay.Where(e => DayKey.Month(e.Key) == monthKey).Sum(e => e.Value);
        var forecast = monthUsd / today.Day * DateTime.DaysInMonth(today.Year, today.Month);

        DayBar Day(string key) => new(key, Cents(perDay.GetValueOrDefault(key)), perDayTokens.GetValueOrDefault(key));

        var days = Enumerable.Range(0, DayCount).Reverse()
            .Select(offset => Day(DayKey.Of(today.AddDays(-offset))))
            .ToList();

        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeks = Enumerable.Range(0, WeekCount).Reverse()
            .Select(offset => thisMonday.AddDays(-7 * offset))
            .Select(start =>
            {
                var keys = Enumerable.Range(0, 7).Select(d => DayKey.Of(start.AddDays(d))).ToList();
                return new DayBar(
                    DayKey.Of(start),
                    Cents(keys.Sum(k => perDay.GetValueOrDefault(k))),
                    keys.Sum(k => perDayTokens.GetValueOrDefault(k)));
            })
            .ToList();

        var models = perModel
            .Select(e => new ModelRow(e.Key, e.Value.Usd, e.Value.Tokens))
            .OrderByDescending(r => r.Usd ?? -1)
            .ThenByDescending(r => r.Model, StringComparer.Ordinal)
            .ToList();

        var projects = perProject
            .Where(e => e.Value.Usd > 0)
            .Select(e => new ProjectRow(e.Key, e.Value.Usd, e.Value.Tokens))
            .OrderByDescending(r => r.Usd)
            .ThenByDescending(r => r.Project, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        var sources = perSource
            .Select(e => new SourceRow(e.Key, e.Value.Usd, e.Value.Tokens))
            .OrderByDescending(r => r.Usd)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ToList();

        return new Summary(
            GeneratedAt: now,
            Today: Day(todayKey),
            Month: monthKey,
            MonthUsd: Cents(monthUsd),
            ForecastUsd: Cents(forecast),
            Days: days,
            Weeks: weeks,
            Models: models,
            Projects: projects,
            UnpricedModels: models.Where(m => m.Usd is null).Select(m => m.Model).Order(StringComparer.Ordinal).ToList(),
            LogsFound: logsFound)
        {
            MonthTokens = monthTokens,
            Sources = sources,
        };
    }
}
