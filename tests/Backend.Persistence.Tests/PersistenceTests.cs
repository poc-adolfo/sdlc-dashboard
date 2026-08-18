using Backend.Persistence.Data;
using Backend.Persistence.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Persistence.Tests;

public sealed class PersistenceTests
{
    private static (SqliteConnection Connection, AppDbContext Context) CreateContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        return (connection, new AppDbContext(options));
    }

    [Fact]
    public void MigrationCreatesExpectedSchemaAndIndexes()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();

        var tables = context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
            .ToHashSet();
        Assert.Contains("client", tables);
        Assert.Contains("workspace", tables);
        Assert.Contains("assessment", tables);
        Assert.Contains("spec", tables);
        Assert.Contains("pipeline_instance", tables);
        Assert.Contains("phase_transition", tables);
        Assert.Contains("perfil_credential", tables);

        var indexes = context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'index'")
            .ToHashSet();
        Assert.Contains("IX_workspace_Slug", indexes);
        Assert.Contains("IX_spec_WorkspaceId_Path", indexes);
        Assert.Contains("IX_pipeline_instance_WorkspaceId_ExternalRef", indexes);

        var migrations = context.Database.SqlQueryRaw<string>(
            "SELECT MigrationId AS Value FROM __EFMigrationsHistory ORDER BY MigrationId")
            .ToList();
        Assert.Equal(new[] { "20260806010204_InitialCreate", "20260808172246_AddActivePerfilCredentialUniqueIndex", "20260808180645_AddPhaseTransitionDeliveryIdDedup", "20260808204544_AddSpecPublication", "20260818143323_AddUxGate", "20260818152259_AddAssessmentSelectedDesignSystem" }, migrations);
    }

    [Fact]
    public void DatabaseDefaultsAreApplied()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();
        var workspace = new Workspace { Name = "Workspace", Slug = "workspace", PlatformRef = "org/repo" };
        context.Workspaces.Add(workspace);
        context.SaveChanges();

        var saved = context.Workspaces.AsNoTracking().Single();
        Assert.Equal("default", saved.TenantId);
        Assert.Equal("specs/", saved.SpecsPath);
        Assert.Equal("User Story", saved.AdoWorkItemType);
    }

    [Fact]
    public void UniqueKeysAndForeignKeysAreEnforced()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();
        var workspace = new Workspace { Name = "Workspace", Slug = "workspace", PlatformRef = "ref" };
        context.Add(workspace);
        context.SaveChanges();

        context.Add(new Workspace { Name = "Duplicate", Slug = "workspace", PlatformRef = "other" });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        context.ChangeTracker.Clear();

        context.Add(new Spec { WorkspaceId = workspace.Id, Path = "a.md", Title = "A", Status = "draft" });
        context.SaveChanges();
        context.Add(new Spec { WorkspaceId = workspace.Id, Path = "a.md", Title = "Duplicate", Status = "draft" });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        context.ChangeTracker.Clear();

        context.Add(new Assessment { WorkspaceId = 9999, ClientId = 9999, Content = "invalid" });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void AtMostOneActivePerfilCredentialIsEnforcedPerWorkspaceAndPerfil()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();
        var workspace = new Workspace { Name = "Workspace", Slug = "workspace", PlatformRef = "ref" };
        context.Add(workspace);
        context.SaveChanges();

        context.Add(new PerfilCredential { WorkspaceId = workspace.Id, Perfil = Perfil.Dev, PlatformUsername = "a", SecretRef = "secret-a/value", Status = CredentialStatus.Active, CreatedAt = DateTime.UtcNow });
        context.SaveChanges();

        // A second active row for the same (workspace, perfil) must be rejected at the DB level -
        // this is what actually protects against the read-then-write race CredentialEndpoints.Create
        // has between checking for a previous active credential and inserting the new one.
        context.Add(new PerfilCredential { WorkspaceId = workspace.Id, Perfil = Perfil.Dev, PlatformUsername = "b", SecretRef = "secret-b/value", Status = CredentialStatus.Active, CreatedAt = DateTime.UtcNow });
        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        context.ChangeTracker.Clear();

        // A second row is fine once the first is revoked (only one Active-status row is constrained).
        var toRevoke = context.PerfilCredentials.Single(c => c.WorkspaceId == workspace.Id && c.Perfil == Perfil.Dev);
        toRevoke.Status = CredentialStatus.Revoked;
        context.Add(new PerfilCredential { WorkspaceId = workspace.Id, Perfil = Perfil.Dev, PlatformUsername = "c", SecretRef = "secret-c/value", Status = CredentialStatus.Active, CreatedAt = DateTime.UtcNow });
        context.SaveChanges();
        Assert.Equal(2, context.PerfilCredentials.Count(c => c.WorkspaceId == workspace.Id));

        // A different perfil in the same workspace is a different constraint bucket.
        context.Add(new PerfilCredential { WorkspaceId = workspace.Id, Perfil = Perfil.Qa, PlatformUsername = "d", SecretRef = "secret-d/value", Status = CredentialStatus.Active, CreatedAt = DateTime.UtcNow });
        context.SaveChanges();
    }

    [Fact]
    public void RestrictAndCascadeDeleteBehaviorsAreEnforced()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();
        var client = new Client { Name = "Client" };
        var workspace = new Workspace { Name = "Workspace", Slug = "workspace", PlatformRef = "ref", Client = client };
        var spec = new Spec { Workspace = workspace, Path = "a.md", Title = "A", Status = "draft" };
        var pipeline = new PipelineInstance { Workspace = workspace, Spec = spec, ExternalRef = "run-1" };
        pipeline.PhaseTransitions.Add(new PhaseTransition { Fase = PipelinePhase.Dev, SourceEvent = "test" });
        context.Add(pipeline);
        context.SaveChanges();

        Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw("DELETE FROM client WHERE Id = {0}", client.Id));
        context.ChangeTracker.Clear();

        context.Remove(workspace);
        context.SaveChanges();
        Assert.Empty(context.Specs);
        Assert.Empty(context.PipelineInstances);
        Assert.Empty(context.PhaseTransitions);
    }

    [Fact]
    public void WorkspaceCheckConstraintsRejectInvalidEnumValues()
    {
        var setup = CreateContext();
        using var connection = setup.Connection;
        using var context = setup.Context;
        context.Database.Migrate();

        Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw(
            "INSERT INTO workspace (Name, Slug, Platform, PlatformRef, Status, CreatedAt) VALUES ('x', 'x', 9, 'ref', 0, '2026-01-01')"));
        Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw(
            "INSERT INTO workspace (Name, Slug, Platform, PlatformRef, Status, CreatedAt) VALUES ('y', 'y', 0, 'ref', 9, '2026-01-01')"));
    }
}