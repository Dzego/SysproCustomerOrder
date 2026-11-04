using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Syspro.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImportJobLeasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Processing job from before leases has no owner to fence, and more than one
            // could exist. Each goes back to Pending and resumes from its checkpoint, with its
            // counters and input untouched. All workers on the previous code must be stopped
            // first. Afterwards the lease constraint stops their claims from succeeding.
            migrationBuilder.Sql(
                """
                UPDATE [ImportLogs]
                SET [Status] = 'Pending', [StartedAt] = NULL
                WHERE [Status] = 'Processing';
                """);

            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                table: "ImportLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "ImportLogs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "ImportLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_ImportLogs_SingleProcessing",
                table: "ImportLogs",
                column: "Status",
                unique: true,
                filter: "[Status] = 'Processing'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImportLogs_Lease",
                table: "ImportLogs",
                sql: "([Status] = 'Processing' AND [LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL)\nOR ([Status] <> 'Processing' AND [LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ImportLogs_SingleProcessing",
                table: "ImportLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ImportLogs_Lease",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "FencingToken",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "ImportLogs");
        }
    }
}
