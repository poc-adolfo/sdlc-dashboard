using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhaseTransitionDeliveryIdDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_phase_transition_PipelineInstanceId",
                table: "phase_transition");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryId",
                table: "phase_transition",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_phase_transition_delivery_dedup",
                table: "phase_transition",
                columns: new[] { "PipelineInstanceId", "DeliveryId" },
                unique: true,
                filter: "DeliveryId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_phase_transition_delivery_dedup",
                table: "phase_transition");

            migrationBuilder.DropColumn(
                name: "DeliveryId",
                table: "phase_transition");

            migrationBuilder.CreateIndex(
                name: "IX_phase_transition_PipelineInstanceId",
                table: "phase_transition",
                column: "PipelineInstanceId");
        }
    }
}
