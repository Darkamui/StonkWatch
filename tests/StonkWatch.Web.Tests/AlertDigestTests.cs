using System.Globalization;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;
using StonkWatch.Web.Services.Monitoring;
using StonkWatch.Web.Services.Notifications;

namespace StonkWatch.Web.Tests;

public class AlertDigestTests
{
    private const string BaseUrl = "https://stonks.example.com";

    private static AlertNotification Alert(
        string ticker, string key, AlertType type, CrossingDirection direction,
        decimal level, string label, decimal previous, decimal price, string? company = null) =>
        new(ticker, company,
            new LevelCrossing(new MonitoredLevel(key, type, direction, level, label), previous, price));

    private static AlertNotification Invalidation(string ticker, string? company = null) =>
        Alert(ticker, LevelKeys.Invalidation, AlertType.Invalidation, CrossingDirection.Down,
            42m, "invalidation", 43m, 41.12m, company);

    private static AlertNotification Reclaim(string ticker) =>
        Alert(ticker, LevelKeys.ReclaimTrigger1, AlertType.ReclaimTrigger, CrossingDirection.Up,
            59m, "reclaim trigger 1", 58m, 60.5m);

    private static AlertNotification Support(string ticker) =>
        Alert(ticker, LevelKeys.SupportZone, AlertType.PrimarySupport, CrossingDirection.Down,
            55m, "primary support", 57m, 54m);

    // ---------- Subject ----------

    [Fact]
    public void A_single_alert_names_the_ticker_and_what_happened()
    {
        var message = AlertDigest.Build([Invalidation("ASTS")]);

        Assert.Equal("StonkWatch: ASTS broke invalidation 42.00 — now 41.12", message.Subject);
    }

    [Fact]
    public void Several_alerts_are_counted_and_the_tickers_listed()
    {
        var message = AlertDigest.Build([Invalidation("ASTS"), Reclaim("HIVE"), Support("BITF")]);

        Assert.Equal("StonkWatch: 3 alerts — ASTS, BITF, HIVE", message.Subject);
    }

    [Fact]
    public void Long_ticker_lists_are_truncated_in_the_subject()
    {
        var message = AlertDigest.Build([
            Invalidation("AAA"), Invalidation("BBB"), Invalidation("CCC"),
            Invalidation("DDD"), Invalidation("EEE")
        ]);

        Assert.Equal("StonkWatch: 5 alerts — AAA, BBB, CCC +2 more", message.Subject);
    }

    [Fact]
    public void Several_alerts_on_one_ticker_count_individually()
    {
        var message = AlertDigest.Build([Support("ASTS"), Invalidation("ASTS")]);

        Assert.Equal("StonkWatch: 2 alerts — ASTS", message.Subject);
    }

    [Fact]
    public void An_empty_digest_is_rejected_rather_than_sending_a_blank_email()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AlertDigest.Build([]));
    }

    // ---------- Wording ----------

    [Theory]
    [InlineData(AlertType.Invalidation, CrossingDirection.Down, "broke")]
    [InlineData(AlertType.PrimarySupport, CrossingDirection.Down, "entered")]
    [InlineData(AlertType.SecondarySupport, CrossingDirection.Down, "entered")]
    [InlineData(AlertType.ReclaimTrigger, CrossingDirection.Up, "reclaimed")]
    [InlineData(AlertType.Target, CrossingDirection.Up, "hit")]
    public void Each_level_type_gets_its_own_verb(
        AlertType type, CrossingDirection direction, string expectedVerb)
    {
        var crossing = new LevelCrossing(
            new MonitoredLevel("k", type, direction, 50m, "the level"), 51m, 49m);

        Assert.StartsWith(expectedVerb + " the level", AlertDigest.Describe(crossing));
    }

    [Fact]
    public void Prices_show_at_least_two_decimals()
    {
        var message = AlertDigest.Build([Reclaim("HIVE")]);

        Assert.Contains("59.00", message.PlainTextBody);
        Assert.Contains("60.50", message.PlainTextBody);
    }

    [Fact]
    public void Sub_penny_prices_keep_their_precision()
    {
        var alert = Alert("PENNY", LevelKeys.Invalidation, AlertType.Invalidation,
            CrossingDirection.Down, 0.0125m, "invalidation", 0.02m, 0.0119m);

        Assert.Contains("0.0125", AlertDigest.Build([alert]).PlainTextBody);
    }

    [Fact]
    public void Prices_render_invariantly_regardless_of_host_locale()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var message = AlertDigest.Build([Invalidation("ASTS")]);

            Assert.Contains("41.12", message.PlainTextBody);
            Assert.DoesNotContain("41,12", message.PlainTextBody);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---------- Body ----------

    [Fact]
    public void The_plain_body_groups_alerts_under_their_ticker()
    {
        var message = AlertDigest.Build([Support("ASTS"), Invalidation("ASTS"), Reclaim("HIVE")]);

        Assert.Contains("ASTS", message.PlainTextBody);
        Assert.Contains("HIVE", message.PlainTextBody);
        Assert.Contains("broke invalidation", message.PlainTextBody);
        Assert.Contains("entered primary support", message.PlainTextBody);
        Assert.Contains("reclaimed reclaim trigger 1", message.PlainTextBody);
    }

    [Fact]
    public void The_worst_news_for_a_ticker_is_listed_first()
    {
        var message = AlertDigest.Build([Support("ASTS"), Invalidation("ASTS")]);

        Assert.True(
            message.PlainTextBody.IndexOf("broke invalidation", StringComparison.Ordinal) <
            message.PlainTextBody.IndexOf("entered primary support", StringComparison.Ordinal));
    }

    [Fact]
    public void The_company_name_is_included_when_known()
    {
        var message = AlertDigest.Build([Invalidation("ASTS", "AST SpaceMobile")]);

        Assert.Contains("AST SpaceMobile", message.PlainTextBody);
        Assert.Contains("AST SpaceMobile", message.HtmlBody);
    }

    [Fact]
    public void Links_point_at_the_candidate_detail_page()
    {
        var message = AlertDigest.Build([Invalidation("ASTS")], BaseUrl);

        Assert.Contains($"{BaseUrl}/Candidates/Detail/ASTS", message.PlainTextBody);
        Assert.Contains($"href=\"{BaseUrl}/Candidates/Detail/ASTS\"", message.HtmlBody);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        var message = AlertDigest.Build([Invalidation("ASTS")], BaseUrl + "/");

        Assert.Contains($"{BaseUrl}/Candidates/Detail/ASTS", message.PlainTextBody);
        Assert.DoesNotContain("//Candidates", message.PlainTextBody);
    }

    [Fact]
    public void No_links_are_emitted_when_no_base_url_is_configured()
    {
        var message = AlertDigest.Build([Invalidation("ASTS")]);

        Assert.DoesNotContain("href", message.HtmlBody);
        Assert.DoesNotContain("http", message.PlainTextBody);
    }

    // ---------- Escaping ----------

    [Fact]
    public void Company_names_are_html_escaped()
    {
        // Company is free text, so it must not be able to inject markup into the email.
        var message = AlertDigest.Build([Invalidation("ASTS", "Dodgy <script>alert(1)</script> Co")]);

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
        // The plain-text part is not markup, so it stays readable.
        Assert.Contains("<script>", message.PlainTextBody);
    }

    [Fact]
    public void Ampersands_in_company_names_are_escaped()
    {
        var message = AlertDigest.Build([Invalidation("ASTS", "Smith & Wesson")]);

        Assert.Contains("Smith &amp; Wesson", message.HtmlBody);
    }

    [Fact]
    public void The_html_body_is_balanced_markup()
    {
        var message = AlertDigest.Build([Support("ASTS"), Reclaim("HIVE")], BaseUrl);

        Assert.Equal(
            message.HtmlBody.Split("<li>").Length,
            message.HtmlBody.Split("</li>").Length);
        Assert.StartsWith("<div", message.HtmlBody);
        Assert.EndsWith("</div>", message.HtmlBody);
    }
}
