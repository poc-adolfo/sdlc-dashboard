using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivePerfilCredentialUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_perfil_credential_active_per_perfil",
                table: "perfil_credential",
                columns: new[] { "WorkspaceId", "Perfil" },
                unique: true,
                filter: "status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_perfil_credential_active_per_perfil",
                table: "perfil_credential");
        }
    }
}
