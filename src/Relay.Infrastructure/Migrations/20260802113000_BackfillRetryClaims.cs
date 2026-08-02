using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations;

[DbContext(typeof(RelayDbContext))]
[Migration("20260802113000_BackfillRetryClaims")]
public sealed class BackfillRetryClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE deliveries d
            SET "AttemptCount" = COALESCE(
                (
                    SELECT COUNT(*)::integer
                    FROM delivery_attempts da
                    WHERE da."DeliveryId" = d."Id"
                ),
                0
            );

            UPDATE delivery_attempts
            SET "State" = 'Failed',
                "ErrorCode" = 'migration_backfill',
                "ErrorMessage" = 'Migrated while the delivery was in flight.',
                "CompletedAtUtc" = CURRENT_TIMESTAMP,
                "DurationMilliseconds" = GREATEST(
                    0,
                    (EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP - "StartedAtUtc")) * 1000)::bigint
                )
            WHERE "State" = 'Processing';

            UPDATE deliveries
            SET "State" = 'Failed',
                "ClaimToken" = NULL,
                "ClaimedAtUtc" = NULL,
                "ClaimExpiresAtUtc" = NULL,
                "NextAttemptAtUtc" = NULL,
                "ErrorCode" = 'migration_backfill',
                "ErrorMessage" = 'Migrated while the delivery was in flight.',
                "CompletedAtUtc" = CURRENT_TIMESTAMP
            WHERE "State" = 'Processing';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
