-- Module: Clinical (owner: clinical record). Encounters, editable note drafts, immutable signed note versions.
CREATE SCHEMA IF NOT EXISTS clinical;
REVOKE ALL ON SCHEMA clinical FROM PUBLIC;
GRANT USAGE ON SCHEMA clinical TO serk_app_role;

CREATE TABLE clinical.encounters (
  id                         uuid PRIMARY KEY,
  tenant_id                  uuid NOT NULL,
  facility_id                uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id                 uuid NOT NULL REFERENCES patients.patients (id),
  appointment_id             uuid NULL UNIQUE REFERENCES scheduling.appointments (id),
  attending_practitioner_id  uuid NOT NULL REFERENCES identity.practitioners (id),
  status                     text NOT NULL CHECK (status IN ('InProgress', 'Signed')),
  started_at                 timestamptz NOT NULL,
  signed_at                  timestamptz NULL,
  version                    int NOT NULL DEFAULT 1
);
CREATE INDEX ix_encounters_practitioner ON clinical.encounters (tenant_id, attending_practitioner_id, started_at);
CREATE INDEX ix_encounters_patient ON clinical.encounters (tenant_id, patient_id, started_at);

CREATE TABLE clinical.note_drafts (
  encounter_id     uuid PRIMARY KEY REFERENCES clinical.encounters (id),
  tenant_id        uuid NOT NULL,
  chief_complaint  text NOT NULL DEFAULT '' CHECK (length(chief_complaint) <= 1000),
  history          text NOT NULL DEFAULT '' CHECK (length(history) <= 8000),
  examination      text NOT NULL DEFAULT '' CHECK (length(examination) <= 8000),
  assessment       text NOT NULL DEFAULT '' CHECK (length(assessment) <= 4000),
  plan             text NOT NULL DEFAULT '' CHECK (length(plan) <= 4000),
  updated_at       timestamptz NOT NULL,
  updated_by       uuid NOT NULL,
  version          int NOT NULL DEFAULT 1
);

-- Signed versions are immutable: runtime role has no UPDATE/DELETE grant and a trigger rejects mutation for everyone.
CREATE TABLE clinical.note_versions (
  id                         uuid PRIMARY KEY,
  tenant_id                  uuid NOT NULL,
  encounter_id               uuid NOT NULL REFERENCES clinical.encounters (id),
  version_no                 int NOT NULL CHECK (version_no >= 1),
  kind                       text NOT NULL CHECK (kind IN ('Original', 'Addendum')),
  amends_version_id          uuid NULL REFERENCES clinical.note_versions (id),
  content_schema_version     int NOT NULL,
  content                    jsonb NOT NULL,
  content_sha256             text NOT NULL CHECK (length(content_sha256) = 64),
  signed_by_user_id          uuid NOT NULL,
  signed_by_practitioner_id  uuid NOT NULL REFERENCES identity.practitioners (id),
  signer_display_name        text NOT NULL,
  signer_credential          text NOT NULL,
  signed_at                  timestamptz NOT NULL,
  allergies_reviewed         boolean NOT NULL,
  addendum_reason            text NULL CHECK (addendum_reason IS NULL OR length(addendum_reason) <= 500),
  UNIQUE (encounter_id, version_no),
  CONSTRAINT ck_addendum_shape CHECK (
    (kind = 'Original' AND amends_version_id IS NULL AND addendum_reason IS NULL AND version_no = 1) OR
    (kind = 'Addendum' AND amends_version_id IS NOT NULL AND addendum_reason IS NOT NULL AND version_no > 1))
);
CREATE TRIGGER note_versions_immutable BEFORE UPDATE OR DELETE ON clinical.note_versions
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
CREATE TRIGGER note_versions_no_truncate BEFORE TRUNCATE ON clinical.note_versions
  FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['clinical.encounters', 'clinical.note_drafts', 'clinical.note_versions'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
  END LOOP;
END $$;
GRANT SELECT, INSERT, UPDATE ON clinical.encounters, clinical.note_drafts TO serk_app_role;
GRANT SELECT, INSERT ON clinical.note_versions TO serk_app_role;
