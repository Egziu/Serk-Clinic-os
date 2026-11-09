-- Module: Configuration (owner: platform/configuration). Facility settings (theme, defaults, automation),
-- branding assets (logo, stamp, practitioner signature) and printer profiles.
CREATE SCHEMA IF NOT EXISTS configuration;
REVOKE ALL ON SCHEMA configuration FROM PUBLIC;
GRANT USAGE ON SCHEMA configuration TO serk_app_role;

CREATE TABLE configuration.facility_settings (
  facility_id                uuid PRIMARY KEY REFERENCES identity.facilities (id),
  tenant_id                  uuid NOT NULL,
  accent_color               text NOT NULL DEFAULT '#1e4d3a' CHECK (accent_color ~ '^#[0-9a-f]{6}$'),
  default_clinical_paper     text NOT NULL DEFAULT 'A4' CHECK (default_clinical_paper IN ('A3', 'A4', 'A5')),
  auto_prepare_documents     boolean NOT NULL DEFAULT true,
  updated_by                 uuid NULL,
  updated_at                 timestamptz NOT NULL,
  version                    int NOT NULL DEFAULT 1
);

-- Uploaded images are immutable (bytes write-once in object store, hash here); only retirement may change.
CREATE TABLE configuration.branding_assets (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL,
  facility_id      uuid NOT NULL REFERENCES identity.facilities (id),
  kind             text NOT NULL CHECK (kind IN ('Logo', 'Stamp', 'Signature')),
  practitioner_id  uuid NULL REFERENCES identity.practitioners (id),
  content_type     text NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg')),
  sha256           text NOT NULL CHECK (length(sha256) = 64),
  size_bytes       int NOT NULL CHECK (size_bytes BETWEEN 1 AND 524288),
  width            int NOT NULL CHECK (width BETWEEN 16 AND 4096),
  height           int NOT NULL CHECK (height BETWEEN 16 AND 4096),
  object_key       text NOT NULL,
  status           text NOT NULL CHECK (status IN ('Active', 'Retired')),
  uploaded_by      uuid NOT NULL,
  uploaded_at      timestamptz NOT NULL,
  retired_at       timestamptz NULL,
  CONSTRAINT ck_signature_owner CHECK ((kind = 'Signature') = (practitioner_id IS NOT NULL))
);
CREATE UNIQUE INDEX ux_branding_active_facility ON configuration.branding_assets (facility_id, kind) WHERE status = 'Active' AND kind <> 'Signature';
CREATE UNIQUE INDEX ux_branding_active_signature ON configuration.branding_assets (tenant_id, practitioner_id) WHERE status = 'Active' AND kind = 'Signature';

CREATE OR REPLACE FUNCTION configuration.guard_branding_asset() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: branding assets cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF (to_jsonb(NEW) - 'status' - 'retired_at') IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'retired_at')
     OR OLD.status = 'Retired' THEN
    RAISE EXCEPTION 'serk_immutable: branding assets are immutable (only retirement is allowed)' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER branding_assets_guard BEFORE UPDATE OR DELETE ON configuration.branding_assets
  FOR EACH ROW EXECUTE FUNCTION configuration.guard_branding_asset();

CREATE TABLE configuration.printer_profiles (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL,
  facility_id  uuid NOT NULL REFERENCES identity.facilities (id),
  name         text NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
  kind         text NOT NULL CHECK (kind IN ('Standard', 'Thermal')),
  paper        text NOT NULL CHECK (paper IN ('A3', 'A4', 'A5', 'Thermal80', 'Thermal58')),
  purpose      text NOT NULL CHECK (purpose IN ('ClinicalDocuments', 'AppointmentSlips')),
  is_default   boolean NOT NULL DEFAULT false,
  is_active    boolean NOT NULL DEFAULT true,
  created_by   uuid NOT NULL,
  created_at   timestamptz NOT NULL,
  version      int NOT NULL DEFAULT 1,
  CONSTRAINT ck_printer_kind_matches_paper CHECK ((kind = 'Thermal') = (paper IN ('Thermal80', 'Thermal58'))),
  CONSTRAINT ck_no_clinical_on_thermal CHECK (purpose <> 'ClinicalDocuments' OR kind = 'Standard')
);
CREATE UNIQUE INDEX ux_printer_default ON configuration.printer_profiles (facility_id, purpose) WHERE is_default AND is_active;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['configuration.facility_settings', 'configuration.branding_assets', 'configuration.printer_profiles'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON %s TO serk_app_role', t);
  END LOOP;
END $$;

-- Documents: A3 for clinical documents; thermal is never used for clinical documents.
ALTER TABLE documents.document_versions DROP CONSTRAINT document_versions_paper_size_check;
ALTER TABLE documents.document_versions ADD CONSTRAINT document_versions_paper_size_check CHECK (paper_size IN ('A3', 'A4', 'A5'));
