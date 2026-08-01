using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetryAndReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_deliveries_EventId",
                table: "deliveries");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimExpiresAtUtc",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAtUtc",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayIdempotencyKey",
                table: "deliveries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplayOfDeliveryId",
                table: "deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_EventId",
                table: "deliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_ReplayOfDeliveryId_ReplayIdempotencyKey",
                table: "deliveries",
                columns: new[] { "ReplayOfDeliveryId", "ReplayIdempotencyKey" },
                unique: true,
                filter: "\"ReplayOfDeliveryId\" IS NOT NULL AND \"ReplayIdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_State_NextAttemptAtUtc",
                table: "deliveries",
                columns: new[] { "State", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_deliveries_EventId",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "IX_deliveries_ReplayOfDeliveryId_ReplayIdempotencyKey",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "IX_deliveries_State_NextAttemptAtUtc",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimExpiresAtUtc",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "ReplayIdempotencyKey",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "ReplayOfDeliveryId",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_EventId",
                table: "deliveries",
                column: "EventId",
                unique: true);
        }
    }
}
