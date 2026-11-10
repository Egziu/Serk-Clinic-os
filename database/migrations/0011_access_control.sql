-- Module: Identity — platform super administration, custom roles, per-assignment permission overrides, admin change log.
-- Design (ADR-0013): the super administrator manages organisations, staff, roles and access across the platform but has NO
-- implicit tenant/patient access; clinical access still requires an ordinary affiliation granted by ANOTHER administrator.

-- Platform (system-wide) administrators. Separate from tenant roles; never derived from an affiliation.
CREATE TABLE identity.platform_admins (
  user_id     uuid PRIMARY KEY REFERENCES identity.staff_users (id),
  is_active   boolean NOT NULL DEFAULT true,
  granted_by  uuid NULL REFERENCES identity.staff_users (id),
  granted_at  timestamptz NOT NULL,
  revoked_by  uuid NULL REFERENCES identity.staff_users (id),
  revoked_at  timestamptz NULL,
  CHECK (granted_by IS NULL OR granted_by <> user_id)
);

-- Organisation-specific custom roles (built-in roles live in code, RoleCatalog, and cannot be edited, only cloned).
CREATE TABLE identity.role_definitions (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES identity.tenants (id),
  code          text NOT NULL CHECK (code ~ '^custom\.[a-z0-9][a-z0-9_-]{1,40}$'),
  name          text NOT NULL CHECK (length(name) BETWEEN 2 AND 80),
  description   text NOT NULL DEFAULT '' CHECK (length(description) <= 500),
  capabilities  text[] NOT NULL CHECK (cardinality(capabilities) BETWEEN 1 AND 200),
  is_active     boolean NOT NULL DEFAULT true,
  version       int NOT NULL DEFAULT 1,
  created_by    uuid NOT NULL REFERENCES identity.staff_users (id),
  created_at    timestamptz NOT NULL,
  updated_by    uuid NOT NULL REFERENCES identity.staff_users (id),
  updated_at    timestamptz NOT NULL,
  UNIQUE (tenant_id, code)
);

-- Per-assignment overrides: effective = (roles ∪ granted) − revoked.
ALTER TABLE identity.affiliations
  ADD COLUMN granted_capabilities text[] NOT NULL DEFAULT '{}',
  ADD COLUMN revoked_capabilities text[] NOT NULL DEFAULT '{}',
  ADD COLUMN version int NOT NULL DEFAULT 1,
  ADD COLUMN updated_by uuid NULL REFERENCES identity.staff_users (id),
  ADD COLUMN updated_at timestamptz NULL,
  ADD CONSTRAINT ck_affiliation_overrides_disjoint CHECK (NOT (granted_capabilities && revoked_capabilities)),
  ADD CONSTRAINT ck_affiliation_validity CHECK (valid_until IS NULL OR valid_until > valid_from);

-- Administrator-created or reset accounts must choose their own password at first sign-in.
ALTER TABLE identity.staff_users
  ADD COLUMN must_change_password boolean NOT NULL DEFAULT false,
  ADD COLUMN password_changed_at timestamptz NULL;

-- Append-only record of every access-control change (who, what, before/after). No PHI is stored here.
CREATE TABLE identity.admin_changes (
  id             uuid PRIMARY KEY,
  occurred_at    timestamptz NOT NULL,
  actor_user_id  uuid NOT NULL REFERENCES identity.staff_users (id),
  action         text NOT NULL CHECK (length(action) BETWEEN 3 AND 80),
  target_type    text NOT NULL,
  target_id      uuid NULL,
  tenant_id      uuid NULL REFERENCES identity.tenants (id),
  before         jsonb NULL,
  after          jsonb NULL,
  reason         text NULL CHECK (reason IS NULL OR length(reason) <= 500),
  correlation_id text NOT NULL
);
CREATE INDEX ix_admin_changes_time ON identity.admin_changes (occurred_at DESC);
CREATE INDEX ix_admin_changes_target ON identity.admin_changes (target_id);
CREATE TRIGGER admin_changes_append_only BEFORE UPDATE OR DELETE ON identity.admin_changes
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
CREATE TRIGGER admin_changes_no_truncate BEFORE TRUNCATE ON identity.admin_changes
  FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();

-- Runtime grants for administration through the application (authorization is enforced in the Identity module; nothing
-- is ever deleted — accounts, assignments, roles and admin status are deactivated instead).
GRANT INSERT ON identity.tenants, identity.facilities, identity.staff_users, identity.practitioners, identity.affiliations TO serk_app_role;
GRANT UPDATE (name) ON identity.tenants TO serk_app_role;
GRANT UPDATE (name, address_line, phone, license_number, time_zone) ON identity.facilities TO serk_app_role;
GRANT UPDATE (display_name, is_active, password_hash, must_change_password, password_changed_at) ON identity.staff_users TO serk_app_role;
GRANT UPDATE (professional_title, specialty, license_number, license_expires_on) ON identity.practitioners TO serk_app_role;
GRANT UPDATE (roles, is_active, valid_from, valid_until, granted_capabilities, revoked_capabilities, version, updated_by, updated_at)
  ON identity.affiliations TO serk_app_role;
GRANT SELECT, INSERT ON identity.role_definitions TO serk_app_role;
GRANT UPDATE (name, description, capabilities, is_active, version, updated_by, updated_at) ON identity.role_definitions TO serk_app_role;
GRANT SELECT, INSERT ON identity.platform_admins TO serk_app_role;
GRANT UPDATE (is_active, granted_by, granted_at, revoked_by, revoked_at) ON identity.platform_admins TO serk_app_role;
GRANT SELECT, INSERT ON identity.admin_changes TO serk_app_role;
