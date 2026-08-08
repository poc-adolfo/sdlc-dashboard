using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spec_publication",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<long>(type: "INTEGER", nullable: false),
                    SpecId = table.Column<long>(type: "INTEGER", nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spec_publication", x => x.Id);
                    table.ForeignKey(
                        name: "FK_spec_publication_spec_SpecId",
                        column: x => x.SpecId,
                        principalTable: "spec",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_spec_publication_workspace_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_spec_publication_SpecId",
                table: "spec_publication",
                column: "SpecId");

            migrationBuilder.CreateIndex(
                name: "IX_spec_publication_workspace_external_ref",
                table: "spec_publication",
                columns: new[] { "WorkspaceId", "ExternalRef" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spec_publication");
        }
    }
}
