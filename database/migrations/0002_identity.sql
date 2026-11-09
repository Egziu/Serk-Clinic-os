-- Module: Identity (owner: identity). Tenants, facilities, staff users, practitioners, affiliations, sessions.
-- Directory tables are not tenant-RLS protected because authentication precedes tenant selection; they contain no
-- clinical data, are read-only for the runtime role except session/lockout columns, and are accessed only by Identity.
CREATE SCHEMA IF NOT EXISTS identity;
REVOKE ALL ON SCHEMA identity FROM PUBLIC;
GRANT USAGE ON SCHEMA identity TO serk_app_role;

CREATE TABLE identity.tenants (
  id          uuid PRIMARY KEY,
  name        text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  created_at  timestamptz NOT NULL
);

CREATE TABLE identity.facilities (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES identity.tenants (id),
  code            text NOT NULL CHECK (length(code) BETWEEN 1 AND 20),
  name            text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  address_line    text NOT NULL DEFAULT '',
  phone           text NOT NULL DEFAULT '',
  license_number  text NOT NULL DEFAULT '',
  time_zone       text NOT NULL DEFAULT 'Africa/Addis_Ababa',
  UNIQUE (tenant_id, code)
);

CREATE TABLE identity.staff_users (
  id                   uuid PRIMARY KEY,
  username             text NOT NULL UNIQUE CHECK (username = lower(username) AND length(username) BETWEEN 3 AND 64),
  display_name         text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
  password_hash        text NOT NULL,
  is_active            boolean NOT NULL DEFAULT true,
  failed_login_count   int NOT NULL DEFAULT 0,
  locked_until         timestamptz NULL,
  last_login_at        timestamptz NULL,
  created_at           timestamptz NOT NULL
);

CREATE TABLE identity.practitioners (
  id                  uuid PRIMARY KEY,
  user_id             uuid NOT NULL REFERENCES identity.staff_users (id),
  tenant_id           uuid NOT NULL REFERENCES identity.tenants (id),
  professional_title  text NOT NULL,
  specialty           text NOT NULL,
  license_number      text NOT NULL,
  license_expires_on  date NOT NULL,
  UNIQUE (user_id, tenant_id)
);

CREATE TABLE identity.affiliations (
  id           uuid PRIMARY KEY,
  user_id      uuid NOT NULL REFERENCES identity.staff_users (id),
  tenant_id    uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id  uuid NOT NULL REFERENCES identity.facilities (id),
  roles        text[] NOT NULL CHECK (cardinality(roles) > 0),
  is_active    boolean NOT NULL DEFAULT true,
  valid_from   timestamptz NOT NULL,
  valid_until  timestamptz NULL,
  UNIQUE (user_id, facility_id)
);

CREATE TABLE identity.sessions (
  id                     uuid PRIMARY KEY,
  user_id                uuid NOT NULL REFERENCES identity.staff_users (id),
  token_hash             text NOT NULL UNIQUE,
  csrf_token             text NOT NULL,
  active_affiliation_id  uuid NULL REFERENCES identity.affiliations (id),
  created_at             timestamptz NOT NULL,
  last_seen_at           timestamptz NOT NULL,
  absolute_expires_at    timestamptz NOT NULL,
  revoked_at             timestamptz NULL,
  revoked_reason         text NULL
);
CREATE INDEX ix_sessions_user ON identity.sessions (user_id) WHERE revoked_at IS NULL;

GRANT SELECT ON identity.tenants, identity.facilities, identity.practitioners, identity.affiliations TO serk_app_role;
GRANT SELECT ON identity.staff_users TO serk_app_role;
GRANT UPDATE (failed_login_count, locked_until, last_login_at) ON identity.staff_users TO serk_app_role;
GRANT SELECT, INSERT ON identity.sessions TO serk_app_role;
GRANT UPDATE (last_seen_at, active_affiliation_id, revoked_at, revoked_reason) ON identity.sessions TO serk_app_role;
