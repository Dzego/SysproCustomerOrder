using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Syspro.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImportJobTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "ImportLogs",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            // The temporary defaults describe rows from before job tracking existed.
            // They are dropped below, so new rows must set these values explicitly.
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ImportLogs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ImportLogs",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "ImportLogs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ImportLogs",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "Completed");

            migrationBuilder.Sql("UPDATE [ImportLogs] SET [CreatedAt] = [StartedAt];");

            // The previous code saved a log and its completion together, so this
            // should match nothing, but the lifecycle constraint must not fail if it does.
            migrationBuilder.Sql(
                """
                UPDATE [ImportLogs]
                SET [Status] = 'Failed',
                    [CompletedAt] = [StartedAt],
                    [FailureReason] = 'The import did not finish before job tracking was introduced.'
                WHERE [CompletedAt] IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "AttemptCount",
                table: "ImportLogs",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 1);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "ImportLogs",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldDefaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "ImportLogs",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldDefaultValue: "Completed");

            migrationBuilder.CreateIndex(
                name: "IX_ImportLogs_Status_CreatedAt",
                table: "ImportLogs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImportLogs_Lifecycle",
                table: "ImportLogs",
                sql: "([Status] = 'Pending' AND [StartedAt] IS NULL AND [CompletedAt] IS NULL AND [FailureReason] IS NULL)\nOR ([Status] = 'Processing' AND [StartedAt] IS NOT NULL AND [CompletedAt] IS NULL AND [FailureReason] IS NULL)\nOR ([Status] = 'Completed' AND [StartedAt] IS NOT NULL AND [CompletedAt] IS NOT NULL AND [FailureReason] IS NULL)\nOR ([Status] = 'Failed' AND [CompletedAt] IS NOT NULL AND [FailureReason] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImportLogs_Status",
                table: "ImportLogs",
                sql: "[Status] IN ('Pending', 'Processing', 'Completed', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImportLogs_Status_CreatedAt",
                table: "ImportLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ImportLogs_Lifecycle",
                table: "ImportLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ImportLogs_Status",
                table: "ImportLogs");

            // The previous schema requires StartedAt, and jobs that never ran have none.
            migrationBuilder.Sql(
                "UPDATE [ImportLogs] SET [StartedAt] = [CreatedAt] WHERE [StartedAt] IS NULL;");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "ImportLogs");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ImportLogs");

            migrationBuilder.AlterColumn<DateTime>(
                name: "StartedAt",
                table: "ImportLogs",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }
    }
}
