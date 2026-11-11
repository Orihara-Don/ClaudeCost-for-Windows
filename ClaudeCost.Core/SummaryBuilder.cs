namespace ClaudeCost.Core;

public sealed record DayBar(string Date, double Usd);

public sealed record ModelRow(string Model, double? Usd, long Tokens);

public sealed record ProjectRow(string Project, double Usd);

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
    bool LogsFound);

public static class SummaryBuilder
{
    public const int DayCount = 14;
    public const int WeekCount = 8;

    private static double Cents(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

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
        var perModel = new Dictionary<string, (double? Usd, long Tokens)>();
        var perProject = new Dictionary<string, double>();

        foreach (var row in rows)
        {
            var cost = prices.Cost(row.Tokens, row.Model, regionalPricing);
            if (cost is { } c) perDay[row.Day] = perDay.GetValueOrDefault(row.Day) + c;
            if (DayKey.Month(row.Day) != monthKey) continue;
            var entry = perModel.GetValueOrDefault(row.Model, (null, 0));
            entry.Tokens += row.Tokens.Total;
            if (cost is { } c2)
            {
                entry.Usd = (entry.Usd ?? 0) + c2;
                perProject[row.Project] = perProject.GetValueOrDefault(row.Project) + c2;
            }
            perModel[row.Model] = entry;
        }

        var monthUsd = perDay.Where(e => DayKey.Month(e.Key) == monthKey).Sum(e => e.Value);
        var forecast = monthUsd / today.Day * DateTime.DaysInMonth(today.Year, today.Month);

        var days = Enumerable.Range(0, DayCount).Reverse()
            .Select(offset => DayKey.Of(today.AddDays(-offset)))
            .Select(key => new DayBar(key, Cents(perDay.GetValueOrDefault(key))))
            .ToList();

        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeks = Enumerable.Range(0, WeekCount).Reverse()
            .Select(offset => thisMonday.AddDays(-7 * offset))
            .Select(start => new DayBar(
                DayKey.Of(start),
                Cents(Enumerable.Range(0, 7).Sum(d => perDay.GetValueOrDefault(DayKey.Of(start.AddDays(d)))))))
            .ToList();

        var models = perModel
            .Select(e => new ModelRow(e.Key, e.Value.Usd, e.Value.Tokens))
            .OrderByDescending(r => r.Usd ?? -1)
            .ThenByDescending(r => r.Model, StringComparer.Ordinal)
            .ToList();

        var projects = perProject
            .Select(e => new ProjectRow(e.Key, e.Value))
            .OrderByDescending(r => r.Usd)
            .ThenByDescending(r => r.Project, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return new Summary(
            GeneratedAt: now,
            Today: new DayBar(todayKey, Cents(perDay.GetValueOrDefault(todayKey))),
            Month: monthKey,
            MonthUsd: Cents(monthUsd),
            ForecastUsd: Cents(forecast),
            Days: days,
            Weeks: weeks,
            Models: models,
            Projects: projects,
            UnpricedModels: models.Where(m => m.Usd is null).Select(m => m.Model).Order(StringComparer.Ordinal).ToList(),
            LogsFound: logsFound);
    }
}
