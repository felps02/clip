using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Infrastructure.Persistence;

/// <summary>
/// EF Core context. Table/column names are snake_case (EFCore.NamingConventions); enums are stored as text,
/// JSON documents as jsonb, timestamps as timestamptz. See docs/DATA_MODEL.md.
/// </summary>
public class AppDbContext : IdentityUserContext<AppUser, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionEvent> SubscriptionEvents => Set<SubscriptionEvent>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<ProcessingJobEvent> ProcessingJobEvents => Set<ProcessingJobEvent>();
    public DbSet<ClipCandidate> ClipCandidates => Set<ClipCandidate>();
    public DbSet<Clip> Clips => Set<Clip>();
    public DbSet<ClipRender> ClipRenders => Set<ClipRender>();
    public DbSet<IntegrationConnection> IntegrationConnections => Set<IntegrationConnection>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<CompanionDevice> CompanionDevices => Set<CompanionDevice>();
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<LiveSegment> LiveSegments => Set<LiveSegment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Every enum of the domain is stored as snake_case text.
        foreach (var enumType in typeof(JobStatus).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(JobStatus).Namespace))
        {
            var converterType = typeof(SnakeEnumConverter<>).MakeGenericType(enumType);
            configurationBuilder.Properties(enumType).HaveConversion(converterType).HaveMaxLength(40);
            var nullableType = typeof(Nullable<>).MakeGenericType(enumType);
            configurationBuilder.Properties(nullableType).HaveConversion(converterType).HaveMaxLength(40);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureIdentity(builder);
        ConfigureProfileAndTokens(builder);
        ConfigureBilling(builder);
        ConfigureMedia(builder);
        ConfigureProjectsAndJobs(builder);
        ConfigureClips(builder);
        ConfigureMisc(builder);
        ConfigureLive(builder);
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.DisplayName).HasMaxLength(120).IsRequired();
            e.Property(u => u.CreatedAt).IsRequired();
            e.HasIndex(u => u.DeletionRequestedAt).HasFilter("deletion_requested_at IS NOT NULL");
        });
        builder.Entity<IdentityUserClaim<Guid>>(e => e.ToTable("user_claims"));
        builder.Entity<IdentityUserLogin<Guid>>(e => e.ToTable("user_logins"));
        builder.Entity<IdentityUserToken<Guid>>(e => e.ToTable("user_tokens"));
    }

    private static void ConfigureProfileAndTokens(ModelBuilder builder)
    {
        builder.Entity<UserProfile>(e =>
        {
            e.ToTable("user_profiles");
            e.HasKey(p => p.UserId);
            e.Property(p => p.Locale).HasMaxLength(16).HasDefaultValue("pt-BR");
            e.Property(p => p.Timezone).HasMaxLength(64);
            e.Property(p => p.EditingPreferences).HasJsonConversion();
            e.Property(p => p.NotificationPreferences).HasJsonConversion();
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(t => t.CreatedByIp).HasMaxLength(64);
            e.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureBilling(ModelBuilder builder)
    {
        builder.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.HasKey(p => p.Id);
            e.Property(p => p.Code).HasMaxLength(40).IsRequired();
            e.Property(p => p.Name).HasMaxLength(120).IsRequired();
            e.Property(p => p.Description).HasMaxLength(1000);
            e.Property(p => p.Currency).HasMaxLength(8).IsRequired();
            e.Property(p => p.ProviderPriceId).HasMaxLength(200);
            e.Property(p => p.Limits).HasJsonConversion();
            e.Property(p => p.Features).HasJsonConversion();
            e.HasIndex(p => p.Code).IsUnique();
        });

        builder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(s => s.Id);
            e.Property(s => s.ProviderCustomerId).HasMaxLength(200);
            e.Property(s => s.ProviderSubscriptionId).HasMaxLength(200);
            e.HasIndex(s => s.UserId);
            e.HasIndex(s => s.ProviderSubscriptionId).IsUnique().HasFilter("provider_subscription_id IS NOT NULL");
            e.HasIndex(s => s.ProviderCustomerId);
            e.HasOne(s => s.Plan).WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.Events).WithOne().HasForeignKey(ev => ev.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SubscriptionEvent>(e =>
        {
            e.ToTable("subscription_events");
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.FromPlanCode).HasMaxLength(40);
            e.Property(ev => ev.ToPlanCode).HasMaxLength(40);
            e.Property(ev => ev.Data).HasRawJson();
            e.HasIndex(ev => new { ev.SubscriptionId, ev.CreatedAt });
        });

        builder.Entity<UsageRecord>(e =>
        {
            e.ToTable("usage_records");
            e.HasKey(u => u.Id);
            e.Property(u => u.Quantity).HasPrecision(18, 3);
            e.Property(u => u.Metadata).HasRawJson();
            e.HasIndex(u => new { u.UserId, u.Kind, u.PeriodStart });
            e.HasIndex(u => u.JobId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<PaymentEvent>(e =>
        {
            e.ToTable("payment_events");
            e.HasKey(p => p.Id);
            e.Property(p => p.Provider).HasMaxLength(40).IsRequired();
            e.Property(p => p.ProviderEventId).HasMaxLength(200).IsRequired();
            e.Property(p => p.Type).HasMaxLength(120).IsRequired();
            e.Property(p => p.Kind).HasMaxLength(40);
            e.Property(p => p.ProviderSubscriptionId).HasMaxLength(200);
            e.Property(p => p.Payload).HasRawJsonRequired();
            e.Property(p => p.Error).HasMaxLength(2000);
            e.HasIndex(p => new { p.Provider, p.ProviderEventId }).IsUnique();
            e.HasIndex(p => p.UserId);
            // Webhook processing asks whether the provider already reported a subscription as deleted.
            e.HasIndex(p => new { p.Provider, p.ProviderSubscriptionId });
        });
    }

    private static void ConfigureMedia(ModelBuilder builder)
    {
        builder.Entity<MediaAsset>(e =>
        {
            e.ToTable("media_assets");
            e.HasKey(m => m.Id);
            e.Property(m => m.StorageKey).HasMaxLength(512).IsRequired();
            e.Property(m => m.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(m => m.ContentType).HasMaxLength(120).IsRequired();
            e.Property(m => m.Sha256).HasMaxLength(64);
            e.Property(m => m.DurationSeconds).HasPrecision(12, 3);
            e.Property(m => m.Container).HasMaxLength(32);
            e.Property(m => m.VideoCodec).HasMaxLength(64);
            e.Property(m => m.AudioCodec).HasMaxLength(64);
            e.HasIndex(m => m.StorageKey).IsUnique();
            e.HasIndex(m => new { m.UserId, m.Status });
            e.HasIndex(m => m.ExpiresAt);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureProjectsAndJobs(ModelBuilder builder)
    {
        builder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Title).HasMaxLength(200).IsRequired();
            e.Property(p => p.SourceUrl).HasMaxLength(2048);
            e.Property(p => p.ClientRequestId).HasMaxLength(128);
            e.Property(p => p.Settings).HasJsonConversion();
            e.HasIndex(p => new { p.UserId, p.ClientRequestId }).IsUnique().HasFilter("client_request_id IS NOT NULL");
            e.HasIndex(p => new { p.UserId, p.CreatedAt }).IsDescending(false, true);
            e.HasIndex(p => p.MediaAssetId);
            e.HasOne(p => p.MediaAsset).WithMany().HasForeignKey(p => p.MediaAssetId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Jobs).WithOne(j => j.Project).HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Clips).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Candidates).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProcessingJob>(e =>
        {
            e.ToTable("processing_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.StatusMessage).HasMaxLength(500);
            e.Property(j => j.WorkerId).HasMaxLength(120);
            e.Property(j => j.Input).HasRawJson();
            e.Property(j => j.Result).HasJsonConversion();
            e.Property(j => j.ErrorCode).HasMaxLength(80);
            e.Property(j => j.ErrorMessage).HasMaxLength(2000);
            e.Property(j => j.TranscriptStorageKey).HasMaxLength(512);
            e.Property(j => j.SignalsStorageKey).HasMaxLength(512);
            e.HasIndex(j => new { j.Status, j.Priority, j.QueuedAt }).IsDescending(false, true, false);
            e.HasIndex(j => new { j.UserId, j.CreatedAt }).IsDescending(false, true);
            e.HasIndex(j => j.ProjectId);
            e.HasIndex(j => new { j.UserId, j.Status })
                .HasDatabaseName("ix_processing_jobs_active")
                .HasFilter("status IN (" + string.Join(", ", JobStateMachine.ActiveStatuses.Select(s => "'" + EnumNames.ToSnake(s) + "'")) + ")");
            e.HasIndex(j => j.LeaseExpiresAt).HasFilter("lease_expires_at IS NOT NULL");
            e.HasOne<AppUser>().WithMany().HasForeignKey(j => j.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.Events).WithOne().HasForeignKey(ev => ev.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.Candidates).WithOne(c => c.Job).HasForeignKey(c => c.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProcessingJobEvent>(e =>
        {
            e.ToTable("processing_job_events");
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.Message).HasMaxLength(1000).IsRequired();
            e.Property(ev => ev.Data).HasRawJson();
            e.HasIndex(ev => new { ev.JobId, ev.CreatedAt });
        });

        builder.Entity<ClipCandidate>(e =>
        {
            e.ToTable("clip_candidates");
            e.HasKey(c => c.Id);
            e.Property(c => c.StartSeconds).HasPrecision(12, 3);
            e.Property(c => c.EndSeconds).HasPrecision(12, 3);
            e.Property(c => c.DurationSeconds).HasPrecision(12, 3).HasComputedColumnSql("end_seconds - start_seconds", stored: true);
            e.Property(c => c.Reasoning).HasMaxLength(2000);
            e.Property(c => c.Signals).HasRawJson();
            e.HasIndex(c => new { c.ProjectId, c.Score }).IsDescending(false, true);
            e.HasIndex(c => c.JobId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureClips(ModelBuilder builder)
    {
        builder.Entity<Clip>(e =>
        {
            e.ToTable("clips");
            e.HasKey(c => c.Id);
            e.Property(c => c.Title).HasMaxLength(200).IsRequired();
            e.Property(c => c.StartSeconds).HasPrecision(12, 3);
            e.Property(c => c.EndSeconds).HasPrecision(12, 3);
            e.Property(c => c.AspectRatio).HasMaxLength(8).IsRequired();
            e.Property(c => c.Subtitles).HasJsonConversion();
            e.Property(c => c.Framing).HasJsonConversion();
            e.Property(c => c.ThumbnailStorageKey).HasMaxLength(512);
            e.HasIndex(c => new { c.UserId, c.CreatedAt }).IsDescending(false, true);
            e.HasIndex(c => c.ProjectId);
            e.HasIndex(c => c.CandidateId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Candidate).WithMany().HasForeignKey(c => c.CandidateId).OnDelete(DeleteBehavior.SetNull);
            // The render shown to the user. Real FK with ON DELETE SET NULL (clips <-> clip_renders form a cycle:
            // inserts set it in a second step, after the render row exists; purges clear it before deleting renders).
            e.HasIndex(c => c.CurrentRenderId);
            e.HasOne<ClipRender>().WithMany().HasForeignKey(c => c.CurrentRenderId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(c => c.Renders).WithOne(r => r.Clip).HasForeignKey(r => r.ClipId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ClipRender>(e =>
        {
            e.ToTable("clip_renders");
            e.HasKey(r => r.Id);
            e.Property(r => r.AspectRatio).HasMaxLength(8).IsRequired();
            e.Property(r => r.StorageKey).HasMaxLength(512);
            e.Property(r => r.DurationSeconds).HasPrecision(12, 3);
            e.Property(r => r.Error).HasMaxLength(2000);
            e.HasIndex(r => new { r.ClipId, r.Version }).IsUnique();
            e.HasIndex(r => r.JobId);
            e.HasOne<ProcessingJob>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureMisc(ModelBuilder builder)
    {
        builder.Entity<IntegrationConnection>(e =>
        {
            e.ToTable("integration_connections");
            e.HasKey(i => i.Id);
            e.Property(i => i.ExternalAccountId).HasMaxLength(200);
            e.Property(i => i.DisplayName).HasMaxLength(200);
            e.Property(i => i.Metadata).HasRawJson();
            e.HasIndex(i => new { i.UserId, i.Provider, i.ExternalAccountId }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(n => n.Id);
            e.Property(n => n.Title).HasMaxLength(200).IsRequired();
            e.Property(n => n.Body).HasMaxLength(2000);
            e.Property(n => n.Data).HasRawJson();
            e.HasIndex(n => new { n.UserId, n.ReadAt, n.CreatedAt }).IsDescending(false, false, true);
            e.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>Live capture (docs/LIVE_CAPTURE.md, docs/DATA_MODEL.md "companion_devices / live_sessions / live_segments").</summary>
    private static void ConfigureLive(ModelBuilder builder)
    {
        builder.Entity<CompanionDevice>(e =>
        {
            e.ToTable("companion_devices");
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(60).IsRequired();
            e.Property(d => d.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(d => d.TokenPrefix).HasMaxLength(16).IsRequired();
            e.HasIndex(d => d.TokenHash).IsUnique();
            e.HasIndex(d => new { d.UserId, d.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        var nonTerminal = "status IN (" + string.Join(", ", LiveSession.NonTerminalStatuses.Select(s => "'" + EnumNames.ToSnake(s) + "'")) + ")";
        builder.Entity<LiveSession>(e =>
        {
            e.ToTable("live_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Settings).HasJsonConversion();
            e.Property(s => s.CompanionState).HasJsonConversion();
            e.Property(s => s.Obs).HasJsonConversion();
            e.Property(s => s.EndReason).HasMaxLength(500);
            e.HasIndex(s => new { s.UserId, s.StartedAt }).IsDescending(false, true);
            e.HasIndex(s => s.ProjectId).IsUnique();
            // At most one waiting/live/ending session per device.
            e.HasIndex(s => s.DeviceId).IsUnique().HasFilter(nonTerminal).HasDatabaseName("ix_live_sessions_device_active");
            e.HasIndex(s => new { s.Status, s.LastHeartbeatAt }).HasFilter(nonTerminal).HasDatabaseName("ix_live_sessions_open");
            e.HasOne(s => s.Device).WithMany().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Project).WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LiveSegment>(e =>
        {
            e.ToTable("live_segments");
            e.HasKey(s => s.Id);
            e.Property(s => s.StartOffsetSeconds).HasPrecision(12, 3);
            e.Property(s => s.DurationSeconds).HasPrecision(12, 3);
            e.Property(s => s.Error).HasMaxLength(2000);
            e.Property(s => s.LiveContext).HasRawJson();
            e.HasIndex(s => new { s.SessionId, s.Sequence }).IsUnique();
            e.HasIndex(s => s.JobId);
            e.HasIndex(s => s.MediaAssetId);
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.Session).WithMany().HasForeignKey(s => s.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.MediaAsset).WithMany().HasForeignKey(s => s.MediaAssetId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ProcessingJob>().WithMany().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
