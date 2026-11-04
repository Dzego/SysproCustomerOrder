using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Syspro.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImportJobInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportJobInputs",
                columns: table => new
                {
                    ImportLogId = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJobInputs", x => x.ImportLogId);
                    table.ForeignKey(
                        name: "FK_ImportJobInputs_ImportLogs_ImportLogId",
                        column: x => x.ImportLogId,
                        principalTable: "ImportLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportJobInputs");
        }
    }
}
