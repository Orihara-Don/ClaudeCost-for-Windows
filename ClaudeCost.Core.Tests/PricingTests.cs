using ClaudeCost.Core;
using Xunit;

namespace ClaudeCost.Core.Tests;

public sealed class PricingTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Theory]
    [InlineData("us.anthropic.claude-opus-5-5-v1:0", "claude-opus-5-5")]
    [InlineData("global.anthropic.claude-sonnet-5-5-v1:0", "claude-sonnet-5-5")]
    [InlineData("jp.anthropic.claude-haiku-4-5-20251001-v1:0", "claude-haiku-4-5")]
    [InlineData("apac.anthropic.claude-sonnet-4-20250514-v1:0", "claude-sonnet-4")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, ModelName.Normalize(raw));

    [Fact]
    public void LookupDoesNotCrossVersions()
    {
        var prices = PriceTable.LoadBundled();
        Assert.Equal(4, prices.PriceFor("claude-opus-5-5")?.Input);
        Assert.Equal(5, prices.PriceFor("claude-opus-5")?.Input);
        Assert.Equal(2, prices.PriceFor("claude-sonnet-5")?.Input);
        Assert.Equal(0.10, prices.PriceFor("claude-sonnet-5-5")?.CacheRead);
        Assert.Equal(0.50, prices.PriceFor("claude-haiku-5-5-over-100k")?.Input);
        Assert.Null(prices.PriceFor("claude-sonnet-5-9")); // 別バージョンに一致させない
        Assert.Null(prices.PriceFor("claude-opus-5-9"));
        Assert.Equal(4, prices.PriceFor("claude-opus-5-5-preview")?.Input);
    }

    [Fact]
    public void CostFormula()
    {
        var prices = PriceTable.LoadBundled();
        var tokens = new TokenCounts { Input = 1_000_000, Output = 1_000_000, CacheWrite5m = 1_000_000, CacheWrite1h = 1_000_000, CacheRead = 1_000_000 };
        // Opus 5.5: 4 + 20 + 5 + 8 + 0.2
        Assert.Equal(37.2, prices.Cost(tokens, "claude-opus-5-5", regionalPricing: false)!.Value, 9);
        Assert.Equal(37.2 * 1.1, prices.Cost(tokens, "claude-opus-5-5", regionalPricing: true)!.Value, 9);
    }

    [Fact]
    public void RegionalAppliesOnlyToEligibleModels()
    {
        var prices = PriceTable.LoadBundled();
        var tokens = new TokenCounts { Input = 1_000_000 };
        Assert.Equal(3, prices.Cost(tokens, "claude-sonnet-4", true)!.Value, 9);
        Assert.Equal(3.3, prices.Cost(tokens, "claude-sonnet-4-5", true)!.Value, 9);
    }

    [Fact]
    public void UnpricedModelIsReportedAndExcluded()
    {
        var tokens = new TokenCounts { Input = 1_000_000 };
        UsageRow[] rows =
        [
            new("2026-11-10", "claude-unknown-1", "p", tokens),
            new("2026-11-10", "claude-opus-5-5", "p", tokens),
        ];
        var summary = SummaryBuilder.Build(rows, PriceTable.LoadBundled(), regionalPricing: false, logsFound: true,
            DateTimeOffset.Parse("2026-11-10T03:00:00Z"), Tokyo);
        Assert.Equal(4, summary.Today.Usd);
        Assert.Equal(new[] { "claude-unknown-1" }, summary.UnpricedModels);
        Assert.Equal(12, summary.ForecastUsd); // 4 ÷ 10日 × 30日
        Assert.Equal(14, summary.Days.Count);
        Assert.Equal("2026-11-10", summary.Days[^1].Date);
        Assert.Null(summary.Models[^1].Usd);
    }

    [Fact]
    public void WeeksStartOnMonday()
    {
        var tokens = new TokenCounts { Input = 1_000_000 };
        var rows = new[] { "2026-11-08", "2026-11-09", "2026-11-10" }
            .Select(d => new UsageRow(d, "claude-opus-5-5", "p", tokens));
        var summary = SummaryBuilder.Build(rows, PriceTable.LoadBundled(), false, true,
            DateTimeOffset.Parse("2026-11-10T03:00:00Z"), Tokyo);
        Assert.Equal(8, summary.Weeks.Count);
        Assert.Equal(new DayBar("2026-11-09", 8), summary.Weeks[^1]); // 11/9(月)と11/10の2日分
        Assert.Equal(new DayBar("2026-11-02", 4), summary.Weeks[6]); // 11/8(日)は前の週
    }

    [Fact]
    public void ProjectNameDropsWindowsHome()
    {
        Assert.Equal("develop-foo", Format.ProjectName("C--Users-me-develop-foo", @"C:\Users\me"));
        Assert.Equal("other", Format.ProjectName("other", @"C:\Users\me"));
    }

    [Fact]
    public void UserPricesOverrideBundled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"models":{"claude-opus-5-5":{"input":1,"output":2},"claude-new-9":{"input":3,"output":4}}}""");
        try
        {
            var (table, error) = PriceTable.LoadMerged(path);
            Assert.Null(error);
            Assert.Equal(1, table.PriceFor("claude-opus-5-5")?.Input);
            Assert.Equal(3, table.PriceFor("claude-new-9")?.Input);
            Assert.Equal(2, table.PriceFor("claude-sonnet-5")?.Input);

            File.WriteAllText(path, "not json");
            (table, error) = PriceTable.LoadMerged(path);
            Assert.NotNull(error);
            Assert.Equal(4, table.PriceFor("claude-opus-5-5")?.Input);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
