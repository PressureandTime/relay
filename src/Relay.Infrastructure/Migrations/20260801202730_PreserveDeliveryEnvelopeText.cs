using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreserveDeliveryEnvelopeText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE deliveries
                ALTER COLUMN "EnvelopeJson" TYPE text
                USING "EnvelopeJson"::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE deliveries
                ALTER COLUMN "EnvelopeJson" TYPE jsonb
                USING "EnvelopeJson"::jsonb;
                """);
        }
    }
}
