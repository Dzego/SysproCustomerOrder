using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Syspro.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImportCountersAndRetryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Runs first, inside the migration's transaction, so that invalid history stops
            // the migration before anything changes. Existing data is never altered to fit.
            // The message contains only row identifiers, not customer data.
            migrationBuilder.Sql(
                """
                DECLARE @InvalidLogs int =
                    (SELECT COUNT(*) FROM [ImportLogs]
                     WHERE [ProcessedCount] <> [CreatedCount] + [UpdatedCount] + [FailedCount]);

                DECLARE @DuplicateErrors int =
                    (SELECT COUNT(*) FROM
                        (SELECT [ImportLogId], [LineNumber] FROM [ImportErrors]
                         GROUP BY [ImportLogId], [LineNumber]
                         HAVING COUNT(*) > 1) AS [Duplicates]);

                IF @InvalidLogs > 0 OR @DuplicateErrors > 0
                BEGIN
                    DECLARE @LogSample nvarchar(200) =
                        (SELECT STRING_AGG(CAST([Id] AS nvarchar(20)), ', ') FROM
                            (SELECT TOP (5) [Id] FROM [ImportLogs]
                             WHERE [ProcessedCount] <> [CreatedCount] + [UpdatedCount] + [FailedCount]
                             ORDER BY [Id]) AS [Sample]);

                    DECLARE @ErrorSample nvarchar(400) =
                        (SELECT STRING_AGG(CONCAT('log ', [ImportLogId], ' line ', [LineNumber]), ', ') FROM
                            (SELECT TOP (5) [ImportLogId], [LineNumber] FROM [ImportErrors]
                             GROUP BY [ImportLogId], [LineNumber]
                             HAVING COUNT(*) > 1
                             ORDER BY [ImportLogId], [LineNumber]) AS [Sample]);

                    DECLARE @Message nvarchar(2047) = CONCAT(
                        'Migration stopped and nothing was changed. Historical import data violates the new constraints. ',
                        'ImportLogs where ProcessedCount <> CreatedCount + UpdatedCount + FailedCount: ', @InvalidLogs,
                        ' (first Ids: ', COALESCE(@LogSample, 'none'), '). ',
                        'Duplicate ImportErrors per ImportLogId and LineNumber: ', @DuplicateErrors,
                        ' (first: ', COALESCE(@ErrorSample, 'none'), '). ',
                        'Review and correct these rows, then run the migration again.');

                    THROW 50000, @Message, 1;
                END
                """);

            // The default stays, so that instances still running the previous code can
            // insert rows while a deployment is in progress.
            migrationBuilder.DropIndex(
                name: "IX_ImportErrors_ImportLogId",
                table: "ImportErrors");

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                table: "ImportLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImportLogs_Counters",
                table: "ImportLogs",
                sql: "[ProcessedCount] = [CreatedCount] + [UpdatedCount] + [FailedCount]");

            migrationBuilder.CreateIndex(
                name: "IX_ImportErrors_ImportLogId_LineNumber",
                table: "ImportErrors",
                columns: new[] { "ImportLogId", "LineNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ImportLogs_Counters",
                table: "ImportLogs");

            migrationBuilder.DropIndex(
                name: "IX_ImportErrors_ImportLogId_LineNumber",
                table: "ImportErrors");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                table: "ImportLogs");

            migrationBuilder.CreateIndex(
                name: "IX_ImportErrors_ImportLogId",
                table: "ImportErrors",
                column: "ImportLogId");
        }
    }
}
