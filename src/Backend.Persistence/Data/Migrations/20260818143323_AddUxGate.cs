using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUxGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FigmaProjectUrl",
                table: "assessment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "design_system_proposal",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Nome = table.Column<string>(type: "TEXT", nullable: false),
                    PaletaJson = table.Column<string>(type: "TEXT", nullable: false),
                    Tipografia = table.Column<string>(type: "TEXT", nullable: false),
                    Estilo = table.Column<string>(type: "TEXT", nullable: false),
                    Justificativa = table.Column<string>(type: "TEXT", nullable: false),
                    Selecionado = table.Column<bool>(type: "INTEGER", nullable: false),
                    SelecionadoPor = table.Column<string>(type: "TEXT", nullable: true),
                    SelecionadoEm = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_design_system_proposal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_design_system_proposal_workspace_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ux_gate_decision",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PipelineInstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    TemTarefasDesign = table.Column<bool>(type: "INTEGER", nullable: true),
                    JustificativaDesign = table.Column<string>(type: "TEXT", nullable: true),
                    Confirmado = table.Column<bool>(type: "INTEGER", nullable: false),
                    MotivoSobrescrita = table.Column<string>(type: "TEXT", nullable: true),
                    DecididoPor = table.Column<string>(type: "TEXT", nullable: true),
                    DecididoEm = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ux_gate_decision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ux_gate_decision_pipeline_instance_PipelineInstanceId",
                        column: x => x.PipelineInstanceId,
                        principalTable: "pipeline_instance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ux_mockup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UxGateDecisionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Nome = table.Column<string>(type: "TEXT", nullable: false),
                    BlobPath = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ux_mockup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ux_mockup_ux_gate_decision_UxGateDecisionId",
                        column: x => x.UxGateDecisionId,
                        principalTable: "ux_gate_decision",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_proposal_WorkspaceId",
                table: "design_system_proposal",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ux_gate_decision_PipelineInstanceId",
                table: "ux_gate_decision",
                column: "PipelineInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ux_mockup_UxGateDecisionId",
                table: "ux_mockup",
                column: "UxGateDecisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "design_system_proposal");

            migrationBuilder.DropTable(
                name: "ux_mockup");

            migrationBuilder.DropTable(
                name: "ux_gate_decision");

            migrationBuilder.DropColumn(
                name: "FigmaProjectUrl",
                table: "assessment");
        }
    }
}
