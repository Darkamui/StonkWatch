using Microsoft.EntityFrameworkCore;
using StonkWatch.Web.Data.Entities;

namespace StonkWatch.Web.Data;

public class StonkWatchDbContext(DbContextOptions<StonkWatchDbContext> options) : DbContext(options)
{
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<ReviewLogEntry> ReviewLogs => Set<ReviewLogEntry>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<WatchlistGroup> WatchlistGroups => Set<WatchlistGroup>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<QuestradeToken> QuestradeTokens => Set<QuestradeToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Candidate>(e =>
        {
            e.ToTable("candidates");
            e.HasKey(c => c.Id);
            e.Property(c => c.Ticker).IsRequired().HasMaxLength(20);
            e.HasIndex(c => c.Ticker).IsUnique();
            e.Property(c => c.Priority).HasConversion<string>().HasMaxLength(10);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Conviction).HasConversion<string>().HasMaxLength(1);
            e.Property(c => c.DataQuality).HasConversion<string>().HasMaxLength(20);

            foreach (var price in new[]
            {
                nameof(Candidate.CurrentPrice), nameof(Candidate.ReviewedPrice),
                nameof(Candidate.SupportLow), nameof(Candidate.SupportHigh),
                nameof(Candidate.SecondarySupportLow), nameof(Candidate.SecondarySupportHigh),
                nameof(Candidate.ReclaimTrigger1), nameof(Candidate.ReclaimTrigger2),
                nameof(Candidate.Invalidation), nameof(Candidate.T1), nameof(Candidate.T2),
                nameof(Candidate.LastQuote)
            })
            {
                e.Property(price).HasPrecision(18, 4);
            }

            e.HasMany(c => c.Alerts)
                .WithOne(a => a.Candidate)
                .HasForeignKey(a => a.CandidateId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(c => c.ReviewLogs)
                .WithOne(r => r.Candidate)
                .HasForeignKey(r => r.CandidateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Alert>(e =>
        {
            e.ToTable("alerts");
            e.HasKey(a => a.Id);
            e.Property(a => a.AlertType).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.LevelLow).HasPrecision(18, 4);
            e.Property(a => a.LevelHigh).HasPrecision(18, 4);
            e.Property(a => a.TriggerPrice).HasPrecision(18, 4);
            e.Property(a => a.LevelKey).HasMaxLength(32);

            // Lets the worker upsert its own rows by (candidate, level) without colliding
            // with hand-created alerts, which carry no LevelKey.
            e.HasIndex(a => new { a.CandidateId, a.LevelKey })
                .IsUnique()
                .HasFilter("level_key IS NOT NULL");

            // Kept explicitly: the composite above is a partial index, so Postgres cannot
            // use it to satisfy a plain "alerts for this candidate" lookup.
            e.HasIndex(a => a.CandidateId);
        });

        modelBuilder.Entity<JobRun>(e =>
        {
            e.ToTable("job_runs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Job).IsRequired().HasMaxLength(50);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(j => new { j.Job, j.StartedAt });
        });

        modelBuilder.Entity<ReviewLogEntry>(e =>
        {
            e.ToTable("review_log");
            e.HasKey(r => r.Id);
            e.Property(r => r.StatusAtReview).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.ThesisImpact).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Price).HasPrecision(18, 4);
        });

        modelBuilder.Entity<WatchlistGroup>(e =>
        {
            e.ToTable("watchlist_groups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).IsRequired().HasMaxLength(40);
            e.HasIndex(g => g.Name).IsUnique();

            // Orphan rather than cascade: deleting a group is a re-organisation, and
            // silently losing the symbols inside it would be a nasty surprise.
            e.HasMany(g => g.Items)
                .WithOne(i => i.Group)
                .HasForeignKey(i => i.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WatchlistItem>(e =>
        {
            e.ToTable("watchlist_items");
            e.HasKey(i => i.Id);
            e.Property(i => i.Symbol).IsRequired().HasMaxLength(20);
            e.Property(i => i.DisplayName).HasMaxLength(60);

            // One list, so a symbol appears at most once across every group.
            e.HasIndex(i => i.Symbol).IsUnique();
            e.HasIndex(i => new { i.GroupId, i.SortOrder });
        });

        modelBuilder.Entity<QuestradeToken>(e =>
        {
            // The check constraint is the point: a second row is impossible at the database
            // level, so no amount of concurrency can produce two competing refresh tokens.
            e.ToTable("questrade_token", t => t.HasCheckConstraint("ck_questrade_token_singleton", "id = 1"));
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.ProtectedRefreshToken).IsRequired();
        });
    }
}
