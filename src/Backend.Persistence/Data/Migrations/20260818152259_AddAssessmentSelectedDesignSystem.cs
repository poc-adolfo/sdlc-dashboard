using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentSelectedDesignSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SelectedDesignSystemProposalId",
                table: "assessment",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessment_SelectedDesignSystemProposalId",
                table: "assessment",
                column: "SelectedDesignSystemProposalId");

            migrationBuilder.AddForeignKey(
                name: "FK_assessment_design_system_proposal_SelectedDesignSystemProposalId",
                table: "assessment",
                column: "SelectedDesignSystemProposalId",
                principalTable: "design_system_proposal",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assessment_design_system_proposal_SelectedDesignSystemProposalId",
                table: "assessment");

            migrationBuilder.DropIndex(
                name: "IX_assessment_SelectedDesignSystemProposalId",
                table: "assessment");

            migrationBuilder.DropColumn(
                name: "SelectedDesignSystemProposalId",
                table: "assessment");
        }
    }
}
