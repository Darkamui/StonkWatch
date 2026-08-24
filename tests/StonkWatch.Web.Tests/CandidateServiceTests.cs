using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class CandidateServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (CandidateService Service, StonkWatchDbContext Db) NewService()
    {
        var db = fixture.CreateContext();
        return (new CandidateService(db, _time), db);
    }

    private async Task<CandidateDto> SeedAsync(CreateCandidateRequest? request = null)
    {
        var (service, db) = NewService();
        await using var _ = db;
        return await service.CreateAsync(request ?? new CreateCandidateRequest("ASTS"));
    }

    // ---------- Create ----------

    [Theory]
    [InlineData("asts")]
    [InlineData("  asts  ")]
    [InlineData("AsTs")]
    public async Task CreateAsync_normalises_ticker_to_trimmed_uppercase(string input)
    {
        var (service, db) = NewService();
        await using var _ = db;

        var created = await service.CreateAsync(new CreateCandidateRequest(input));

        Assert.Equal("ASTS", created.Ticker);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_rejects_blank_ticker(string input)
    {
        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(new CreateCandidateRequest(input)));
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_ticker_regardless_of_casing()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS"));

        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(new CreateCandidateRequest("asts")));
    }

    [Fact]
    public async Task CreateAsync_stamps_timestamps_from_the_time_provider()
    {
        var created = await SeedAsync();

        Assert.Equal(Now, created.CreatedAt);
        Assert.Equal(Now, created.UpdatedAt);
    }

    [Fact]
    public async Task CreateAsync_applies_defaults_for_omitted_enums()
    {
        var created = await SeedAsync();

        Assert.Equal(nameof(Priority.Medium), created.Priority);
        Assert.Equal(nameof(CandidateStatus.Idea), created.Status);
        Assert.Equal(nameof(DataQuality.Unavailable), created.DataQuality);
        Assert.Null(created.Conviction);
    }

    [Fact]
    public async Task CreateAsync_coerces_loosely_typed_enum_strings()
    {
        var created = await SeedAsync(new CreateCandidateRequest(
            "ASTS", Priority: "high", Status: "near trigger", Conviction: "a",
            DataQuality: "PARTIAL"));

        Assert.Equal(nameof(Priority.High), created.Priority);
        Assert.Equal(nameof(CandidateStatus.NearTrigger), created.Status);
        Assert.Equal("A", created.Conviction);
        Assert.Equal(nameof(DataQuality.Partial), created.DataQuality);
    }

    [Fact]
    public async Task CreateAsync_round_trips_decimals_at_full_precision()
    {
        var created = await SeedAsync(new CreateCandidateRequest(
            "ASTS", SupportLow: 52.1234m, Invalidation: 48.9999m));

        var (service, db) = NewService();
        await using var _ = db;
        var fetched = await service.GetByTickerAsync("ASTS");

        Assert.Equal(52.1234m, fetched!.Candidate.SupportLow);
        Assert.Equal(48.9999m, fetched.Candidate.Invalidation);
    }

    // ---------- Update: the three-way PATCH contract ----------

    [Fact]
    public async Task UpdateAsync_leaves_omitted_text_fields_unchanged()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", Thesis: "Original thesis"));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateAsync("ASTS", new UpdateCandidateRequest(Company: "AST"));

        Assert.Equal("Original thesis", updated!.Thesis);
    }

    [Fact]
    public async Task UpdateAsync_clears_a_text_field_on_empty_string()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", Thesis: "Original thesis"));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateAsync("ASTS", new UpdateCandidateRequest(Thesis: ""));

        Assert.Null(updated!.Thesis);
    }

    [Fact]
    public async Task UpdateAsync_sets_a_text_field_on_a_value()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", Thesis: "Original thesis"));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateAsync("ASTS", new UpdateCandidateRequest(Thesis: "New thesis"));

        Assert.Equal("New thesis", updated!.Thesis);
    }

    [Fact]
    public async Task UpdateAsync_leaves_omitted_numeric_fields_unchanged()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", SupportLow: 52m, Invalidation: 48m));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateAsync("ASTS", new UpdateCandidateRequest(SupportLow: 53m));

        Assert.Equal(53m, updated!.SupportLow);
        Assert.Equal(48m, updated.Invalidation);
    }

    [Fact]
    public async Task UpdateAsync_advances_updated_at_but_not_created_at()
    {
        var created = await SeedAsync();
        _time.Advance(TimeSpan.FromHours(3));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateAsync("ASTS", new UpdateCandidateRequest(Company: "AST"));

        Assert.Equal(created.CreatedAt, updated!.CreatedAt);
        Assert.Equal(Now.AddHours(3), updated.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_returns_null_for_an_unknown_ticker()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.UpdateAsync("NOPE", new UpdateCandidateRequest(Company: "x")));
    }

    [Fact]
    public async Task UpdateAsync_accepts_a_lowercase_ticker()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS"));

        var (service, db) = NewService();
        await using var _ = db;

        Assert.NotNull(await service.UpdateAsync("asts", new UpdateCandidateRequest(Company: "AST")));
    }

    // ---------- Update with history (the JSON "Update" flow) ----------

    [Fact]
    public async Task UpdateWithHistoryAsync_snapshots_the_prior_state_before_applying_changes()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", Thesis: "Original thesis", SupportLow: 52m));

        var (service, db) = NewService();
        await using var _ = db;
        var updated = await service.UpdateWithHistoryAsync(
            "ASTS", new UpdateCandidateRequest(Thesis: "New thesis", SupportLow: 53m));

        Assert.Equal("New thesis", updated!.Thesis);
        Assert.Equal(53m, updated.SupportLow);

        await using var check = fixture.CreateContext();
        var entry = Assert.Single(await check.CandidateHistoryEntries.ToListAsync());
        Assert.Contains("Original thesis", entry.PreviousState);
        Assert.Contains("52", entry.PreviousState);
        Assert.Equal(Now, entry.SnapshotAt);
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_appends_a_new_entry_per_update_rather_than_replacing()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", Thesis: "First"));

        var (service, db) = NewService();
        await using var _ = db;
        await service.UpdateWithHistoryAsync("ASTS", new UpdateCandidateRequest(Thesis: "Second"));
        _time.Advance(TimeSpan.FromHours(1));
        await service.UpdateWithHistoryAsync("ASTS", new UpdateCandidateRequest(Thesis: "Third"));

        await using var check = fixture.CreateContext();
        var entries = await check.CandidateHistoryEntries.OrderBy(e => e.SnapshotAt).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains("First", entries[0].PreviousState);
        Assert.Contains("Second", entries[1].PreviousState);
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_returns_null_for_an_unknown_ticker_without_recording_history()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.UpdateWithHistoryAsync("NOPE", new UpdateCandidateRequest(Company: "x")));

        await using var check = fixture.CreateContext();
        Assert.Empty(await check.CandidateHistoryEntries.ToListAsync());
    }

    // ---------- Review logging ----------

    [Fact]
    public async Task LogReviewAsync_with_a_price_updates_last_reviewed_and_both_prices()
    {
        await SeedAsync();
        _time.Advance(TimeSpan.FromDays(2));

        var (service, db) = NewService();
        await using var _ = db;
        await service.LogReviewAsync("ASTS", new LogReviewRequest(Price: 57.25m));

        var detail = await service.GetByTickerAsync("ASTS");
        Assert.Equal(Now.AddDays(2), detail!.Candidate.LastReviewed);
        Assert.Equal(57.25m, detail.Candidate.ReviewedPrice);
        Assert.Equal(57.25m, detail.Candidate.CurrentPrice);
    }

    [Fact]
    public async Task LogReviewAsync_without_a_price_leaves_prices_untouched()
    {
        await SeedAsync(new CreateCandidateRequest("ASTS", CurrentPrice: 50m));

        var (service, db) = NewService();
        await using var _ = db;
        await service.LogReviewAsync("ASTS", new LogReviewRequest(WhatChanged: "Nothing"));

        var detail = await service.GetByTickerAsync("ASTS");
        Assert.Equal(50m, detail!.Candidate.CurrentPrice);
        Assert.Null(detail.Candidate.ReviewedPrice);
        Assert.NotNull(detail.Candidate.LastReviewed);
    }

    [Fact]
    public async Task LogReviewAsync_normalises_a_non_utc_review_date()
    {
        await SeedAsync();
        // Npgsql rejects a non-UTC DateTimeOffset on a timestamptz column, so the service
        // must convert. This is the regression test for that conversion.
        var eastern = new DateTimeOffset(2026, 7, 30, 9, 45, 0, TimeSpan.FromHours(-4));

        var (service, db) = NewService();
        await using var _ = db;
        var entry = await service.LogReviewAsync("ASTS", new LogReviewRequest(ReviewDate: eastern));

        Assert.Equal(eastern.ToUniversalTime(), entry!.ReviewDate);
        Assert.Equal(TimeSpan.Zero, entry.ReviewDate.Offset);
    }

    [Fact]
    public async Task LogReviewAsync_returns_null_for_an_unknown_ticker()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.LogReviewAsync("NOPE", new LogReviewRequest()));
    }

    [Fact]
    public async Task LogReviewAsync_appends_rather_than_replacing()
    {
        await SeedAsync();
        var (service, db) = NewService();
        await using var _ = db;

        await service.LogReviewAsync("ASTS", new LogReviewRequest(WhatChanged: "First"));
        _time.Advance(TimeSpan.FromDays(1));
        await service.LogReviewAsync("ASTS", new LogReviewRequest(WhatChanged: "Second"));

        var detail = await service.GetByTickerAsync("ASTS");
        Assert.Equal(2, detail!.ReviewLogs.Count);
        // Newest first.
        Assert.Equal("Second", detail.ReviewLogs[0].WhatChanged);
    }

    // ---------- Listing and deletion ----------

    [Fact]
    public async Task ListAsync_filters_by_status_and_priority()
    {
        await SeedAsync(new CreateCandidateRequest("AAA", Priority: "high", Status: "watch"));
        await SeedAsync(new CreateCandidateRequest("BBB", Priority: "low", Status: "watch"));
        await SeedAsync(new CreateCandidateRequest("CCC", Priority: "high", Status: "idea"));

        var (service, db) = NewService();
        await using var _ = db;

        Assert.Equal(2, (await service.ListAsync(CandidateStatus.Watch, null)).Count);
        Assert.Equal(2, (await service.ListAsync(null, Priority.High)).Count);

        var both = await service.ListAsync(CandidateStatus.Watch, Priority.High);
        Assert.Equal("AAA", Assert.Single(both).Ticker);
    }

    [Fact]
    public async Task ListAsync_orders_by_ticker()
    {
        await SeedAsync(new CreateCandidateRequest("ZZZ"));
        await SeedAsync(new CreateCandidateRequest("AAA"));

        var (service, db) = NewService();
        await using var _ = db;
        var all = await service.ListAsync(null, null);

        Assert.Equal(["AAA", "ZZZ"], all.Select(c => c.Ticker));
    }

    [Fact]
    public async Task DeleteAsync_cascades_to_alerts_and_reviews()
    {
        await SeedAsync();
        var (service, db) = NewService();
        await using var _ = db;

        await service.AddAlertAsync("ASTS", new CreateAlertRequest("PrimarySupport", 52m, 55m));
        await service.LogReviewAsync("ASTS", new LogReviewRequest(WhatChanged: "note"));
        await service.UpdateWithHistoryAsync("ASTS", new UpdateCandidateRequest(Company: "AST"));

        Assert.True(await service.DeleteAsync("ASTS"));

        await using var check = fixture.CreateContext();
        Assert.Empty(await check.Candidates.ToListAsync());
        Assert.Empty(await check.Alerts.ToListAsync());
        Assert.Empty(await check.ReviewLogs.ToListAsync());
        Assert.Empty(await check.CandidateHistoryEntries.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_an_unknown_ticker()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.False(await service.DeleteAsync("NOPE"));
    }

    // ---------- Alerts ----------

    [Fact]
    public async Task GetAlertsAsync_can_filter_to_triggered_only()
    {
        await SeedAsync();
        var (service, db) = NewService();
        await using var _ = db;

        var quiet = await service.AddAlertAsync("ASTS", new CreateAlertRequest("PrimarySupport", 52m, 55m));
        var loud = await service.AddAlertAsync("ASTS", new CreateAlertRequest("ReclaimTrigger", 59m));
        await service.UpdateAlertAsync("ASTS", loud!.Id, new UpdateAlertRequest(Triggered: true));

        Assert.Equal(2, (await service.GetAlertsAsync(null)).Count);

        var triggered = Assert.Single(await service.GetAlertsAsync(true));
        Assert.Equal(loud.Id, triggered.Id);
        Assert.NotEqual(quiet!.Id, triggered.Id);
    }

    [Fact]
    public async Task AddAlertAsync_returns_null_for_an_unknown_ticker()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.AddAlertAsync("NOPE", new CreateAlertRequest("PrimarySupport")));
    }

    [Fact]
    public async Task AcknowledgeAlertAsync_stamps_the_time()
    {
        await SeedAsync();
        var (service, db) = NewService();
        await using var _ = db;

        var alert = await service.AddAlertAsync("ASTS", new CreateAlertRequest("PrimarySupport"));
        var acknowledged = await service.AcknowledgeAlertAsync("ASTS", alert!.Id);

        Assert.Equal(Now, acknowledged!.AcknowledgedAt);
    }

    [Fact]
    public async Task AcknowledgeAlertAsync_returns_null_for_an_unknown_alert()
    {
        await SeedAsync();
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.AcknowledgeAlertAsync("ASTS", Guid.NewGuid()));
    }

    [Fact]
    public async Task GetLastJobRunAsync_returns_the_most_recent_run()
    {
        await using (var seed = fixture.CreateContext())
        {
            seed.JobRuns.AddRange(
                new JobRun { Id = Guid.NewGuid(), Job = JobNames.PriceCheck, StartedAt = Now.AddHours(-2), Status = JobStatus.Succeeded },
                new JobRun { Id = Guid.NewGuid(), Job = JobNames.PriceCheck, StartedAt = Now, Status = JobStatus.Failed, Error = "boom" });
            await seed.SaveChangesAsync();
        }

        var (service, db) = NewService();
        await using var _ = db;
        var run = await service.GetLastJobRunAsync(JobNames.PriceCheck);

        Assert.Equal(nameof(JobStatus.Failed), run!.Status);
        Assert.Equal("boom", run.Error);
    }

    [Fact]
    public async Task GetLastJobRunAsync_returns_null_when_the_job_has_never_run()
    {
        var (service, db) = NewService();
        await using var _ = db;

        Assert.Null(await service.GetLastJobRunAsync(JobNames.PriceCheck));
    }
}
