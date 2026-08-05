-- Initial migration for SQLite. The EF model is the runtime source of truth;
-- this script is suitable for a clean, independently provisioned database.
CREATE TABLE clients (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE workspaces (id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL, client_id TEXT NOT NULL, name TEXT NOT NULL, slug TEXT NOT NULL, platform TEXT NOT NULL, platform_ref TEXT NOT NULL, specs_path TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL, FOREIGN KEY(client_id) REFERENCES clients(id));
CREATE UNIQUE INDEX ix_workspaces_tenant_slug ON workspaces(tenant_id,slug);
CREATE TABLE assessments (id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, client_id TEXT NOT NULL, content TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, FOREIGN KEY(workspace_id) REFERENCES workspaces(id));
CREATE INDEX ix_assessments_workspace_status ON assessments(workspace_id,status);
CREATE TABLE specs (id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, path TEXT NOT NULL, title TEXT NOT NULL, status TEXT NOT NULL, version INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, FOREIGN KEY(workspace_id) REFERENCES workspaces(id));
CREATE UNIQUE INDEX ix_specs_workspace_path ON specs(workspace_id,path);
CREATE TABLE pipeline_instances (id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, spec_id TEXT NULL, fase_atual TEXT NOT NULL, gate_status TEXT NOT NULL, external_ref TEXT NULL, created_at TEXT NOT NULL, FOREIGN KEY(workspace_id) REFERENCES workspaces(id), FOREIGN KEY(spec_id) REFERENCES specs(id));
CREATE TABLE phase_transitions (id TEXT PRIMARY KEY, pipeline_instance_id TEXT NOT NULL, fase TEXT NOT NULL, entered_at TEXT NOT NULL, source_event TEXT NOT NULL, FOREIGN KEY(pipeline_instance_id) REFERENCES pipeline_instances(id));
CREATE UNIQUE INDEX ix_phase_transitions_event ON phase_transitions(pipeline_instance_id,source_event);
CREATE TABLE perfil_credentials (id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, perfil TEXT NOT NULL, platform_username TEXT NOT NULL, secret_ref TEXT NOT NULL, scopes TEXT NOT NULL, status TEXT NOT NULL, created_at TEXT NOT NULL, rotated_at TEXT NULL, FOREIGN KEY(workspace_id) REFERENCES workspaces(id));
CREATE UNIQUE INDEX ix_credentials_workspace_profile ON perfil_credentials(workspace_id,perfil);
