using Microsoft.EntityFrameworkCore;
using Relay.Core;

namespace Relay.Infrastructure;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options)
    : DbContext(options)
{
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEndpoint(modelBuilder);
        ConfigureEvent(modelBuilder);
        ConfigureDelivery(modelBuilder);
        ConfigureAttempt(modelBuilder);
    }

    private static void ConfigureEndpoint(ModelBuilder modelBuilder)
    {
        var endpoint = modelBuilder.Entity<WebhookEndpoint>();
        endpoint.ToTable("webhook_endpoints");
        endpoint.HasKey(value => value.Id);
        endpoint.Property(value => value.Name).HasMaxLength(RelayLimits.EndpointNameLength);
        endpoint.Property(value => value.TargetUrl).HasMaxLength(RelayLimits.EndpointUrlLength);
        endpoint.Property(value => value.ProtectedSigningSecret)
            .HasMaxLength(RelayLimits.ProtectedSecretLength);
        endpoint.Property(value => value.CreatedAtUtc).HasColumnType("timestamp with time zone");
    }

    private static void ConfigureEvent(ModelBuilder modelBuilder)
    {
        var webhookEvent = modelBuilder.Entity<WebhookEvent>();
        webhookEvent.ToTable("webhook_events");
        webhookEvent.HasKey(value => value.Id);
        webhookEvent.Property(value => value.EventType).HasMaxLength(RelayLimits.EventTypeLength);
        webhookEvent.Property(value => value.PayloadJson).HasColumnType("jsonb");
        webhookEvent.Property(value => value.IdempotencyKey)
            .HasMaxLength(RelayLimits.IdempotencyKeyLength);
        webhookEvent.Property(value => value.RequestFingerprint)
            .HasMaxLength(RelayLimits.FingerprintLength);
        webhookEvent.Property(value => value.CorrelationId)
            .HasMaxLength(RelayLimits.CorrelationIdLength);
        webhookEvent.Property(value => value.CreatedAtUtc).HasColumnType("timestamp with time zone");
        webhookEvent.HasIndex(value => new { value.EndpointId, value.IdempotencyKey }).IsUnique();
        webhookEvent.HasOne(value => value.Endpoint)
            .WithMany()
            .HasForeignKey(value => value.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder)
    {
        var delivery = modelBuilder.Entity<Delivery>();
        delivery.ToTable("deliveries");
        delivery.HasKey(value => value.Id);
        delivery.Property(value => value.State)
            .HasConversion<string>()
            .HasMaxLength(32);
        delivery.Property(value => value.EnvelopeJson).HasColumnType("text");
        delivery.Property(value => value.EnvelopeHash)
            .HasMaxLength(RelayLimits.EnvelopeHashLength);
        delivery.Property(value => value.CorrelationId)
            .HasMaxLength(RelayLimits.CorrelationIdLength);
        delivery.Property(value => value.ErrorCode).HasMaxLength(RelayLimits.ErrorCodeLength);
        delivery.Property(value => value.ErrorMessage).HasMaxLength(RelayLimits.ErrorMessageLength);
        
        delivery.Property(value => value.NextAttemptAtUtc).HasColumnType("timestamp with time zone");
        delivery.Property(value => value.ClaimedAtUtc).HasColumnType("timestamp with time zone");
        delivery.Property(value => value.ClaimExpiresAtUtc).HasColumnType("timestamp with time zone");
        delivery.Property(value => value.ReplayIdempotencyKey).HasMaxLength(RelayLimits.IdempotencyKeyLength);

        delivery.Property(value => value.CreatedAtUtc).HasColumnType("timestamp with time zone");
        delivery.Property(value => value.StartedAtUtc).HasColumnType("timestamp with time zone");
        delivery.Property(value => value.CompletedAtUtc).HasColumnType("timestamp with time zone");
        
        delivery.HasIndex(value => value.EventId);
        delivery.HasIndex(value => new { value.State, value.CreatedAtUtc });
        delivery.HasIndex(value => new { value.State, value.NextAttemptAtUtc });
        delivery.HasIndex(value => new { value.ReplayOfDeliveryId, value.ReplayIdempotencyKey })
            .IsUnique()
            .HasFilter("\"ReplayOfDeliveryId\" IS NOT NULL AND \"ReplayIdempotencyKey\" IS NOT NULL");
            
        delivery.HasOne(value => value.Event)
            .WithMany()
            .HasForeignKey(value => value.EventId)
            .OnDelete(DeleteBehavior.Cascade);
        delivery.HasOne(value => value.Endpoint)
            .WithMany()
            .HasForeignKey(value => value.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAttempt(ModelBuilder modelBuilder)
    {
        var attempt = modelBuilder.Entity<DeliveryAttempt>();
        attempt.ToTable("delivery_attempts");
        attempt.HasKey(value => value.Id);
        attempt.Property(value => value.State)
            .HasConversion<string>()
            .HasMaxLength(32);
        attempt.Property(value => value.ErrorCode).HasMaxLength(RelayLimits.ErrorCodeLength);
        attempt.Property(value => value.ErrorMessage).HasMaxLength(RelayLimits.ErrorMessageLength);
        attempt.Property(value => value.StartedAtUtc).HasColumnType("timestamp with time zone");
        attempt.Property(value => value.CompletedAtUtc).HasColumnType("timestamp with time zone");
        attempt.HasIndex(value => new { value.DeliveryId, value.AttemptNumber }).IsUnique();
        attempt.HasOne(value => value.Delivery)
            .WithMany()
            .HasForeignKey(value => value.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
