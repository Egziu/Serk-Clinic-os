-- Module: Platform (owner: platform/backend). Roles, shared helper functions, outbox, idempotency, audit.
CREATE EXTENSION IF NOT EXISTS btree_gist;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'serk_app_role') THEN
    -- Runtime privilege group. Never owner of tables, never BYPASSRLS. Login users are granted this role at deployment.
    CREATE ROLE serk_app_role NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END $$;

CREATE SCHEMA IF NOT EXISTS platform;
CREATE SCHEMA IF NOT EXISTS audit;
REVOKE ALL ON SCHEMA platform, audit FROM PUBLIC;
GRANT USAGE ON SCHEMA platform, audit TO serk_app_role;
REVOKE ALL ON ALL TABLES IN SCHEMA platform FROM PUBLIC;

-- Tenant context set per transaction by the API (set_config('app.tenant_id', ..., true)). Missing => NULL => no rows.
CREATE OR REPLACE FUNCTION platform.current_tenant() RETURNS uuid
LANGUAGE sql STABLE AS $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid $$;

CREATE OR REPLACE FUNCTION platform.reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'serk_immutable: % on %.% is prohibited', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'P0001';
END $$;

CREATE TABLE platform.outbox_messages (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL,
  message_type     text NOT NULL CHECK (length(message_type) <= 200),
  schema_version   int  NOT NULL,
  payload          jsonb NOT NULL,
  correlation_id   text NOT NULL,
  status           text NOT NULL CHECK (status IN ('Pending', 'Processed', 'DeadLettered')),
  attempts         int  NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  next_attempt_at  timestamptz NOT NULL,
  last_error       text NULL,
  created_at       timestamptz NOT NULL,
  processed_at     timestamptz NULL
);
CREATE INDEX ix_outbox_due ON platform.outbox_messages (next_attempt_at, id) WHERE status = 'Pending';
GRANT SELECT, INSERT, UPDATE ON platform.outbox_messages TO serk_app_role;

CREATE TABLE platform.idempotency_records (
  tenant_id        uuid NOT NULL,
  scope            text NOT NULL,
  idempotency_key  text NOT NULL CHECK (length(idempotency_key) <= 100),
  request_hash     text NOT NULL,
  actor_user_id    uuid NOT NULL,
  status           text NOT NULL CHECK (status IN ('InProgress', 'Completed')),
  response_json    jsonb NULL,
  created_at       timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, scope, idempotency_key)
);
ALTER TABLE platform.idempotency_records ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform.idempotency_records FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON platform.idempotency_records
  USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant());
GRANT SELECT, INSERT, UPDATE ON platform.idempotency_records TO serk_app_role;

-- Append-only audit trail of sensitive reads/writes/prints/exports. No clinical free text.
CREATE TABLE audit.audit_events (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL,
  facility_id      uuid NOT NULL,
  actor_user_id    uuid NOT NULL,
  actor_session_id uuid NOT NULL,
  action           text NOT NULL,
  resource_type    text NOT NULL,
  resource_id      uuid NOT NULL,
  patient_id       uuid NULL,
  outcome          text NOT NULL,
  reason           text NULL CHECK (reason IS NULL OR length(reason) <= 500),
  correlation_id   text NOT NULL,
  occurred_at      timestamptz NOT NULL,
  metadata         jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX ix_audit_resource ON audit.audit_events (tenant_id, resource_id, occurred_at);
CREATE INDEX ix_audit_patient ON audit.audit_events (tenant_id, patient_id, occurred_at) WHERE patient_id IS NOT NULL;
ALTER TABLE audit.audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit.audit_events FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON audit.audit_events
  USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant());
CREATE TRIGGER audit_events_append_only BEFORE UPDATE OR DELETE ON audit.audit_events
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit.audit_events
  FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();
GRANT SELECT, INSERT ON audit.audit_events TO serk_app_role;

-- Security events (login failures, denials). Written outside the business transaction; app may only INSERT.
CREATE TABLE audit.security_events (
  id               uuid PRIMARY KEY,
  event_type       text NOT NULL,
  outcome          text NOT NULL,
  user_id          uuid NULL,
  tenant_id        uuid NULL,
  resource_type    text NULL,
  resource_id      uuid NULL,
  correlation_id   text NOT NULL,
  subject_hint     text NULL CHECK (subject_hint IS NULL OR length(subject_hint) <= 64),
  occurred_at      timestamptz NOT NULL
);
CREATE INDEX ix_security_events_time ON audit.security_events (occurred_at);
CREATE TRIGGER security_events_append_only BEFORE UPDATE OR DELETE ON audit.security_events
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
GRANT INSERT ON audit.security_events TO serk_app_role;
