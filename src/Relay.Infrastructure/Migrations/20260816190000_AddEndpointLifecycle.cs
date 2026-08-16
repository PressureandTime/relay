using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations;

[DbContext(typeof(RelayDbContext))]
[Migration("20260816190000_AddEndpointLifecycle")]
public sealed class AddEndpointLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "State",
            table: "webhook_endpoints",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Active");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "State",
            table: "webhook_endpoints");
    }
}
