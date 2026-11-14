-- Clinic profile (owner setup), data export log and backup log.
-- Profile: one row per facility, versioned (optimistic concurrency) and audited; shown on letterheads, receipts and slips.
CREATE TABLE configuration.facility_profiles (
  facility_id        uuid PRIMARY KEY REFERENCES identity.facilities (id),
  tenant_id          uuid NOT NULL REFERENCES identity.tenants (id),
  display_name       text NOT NULL DEFAULT '' CHECK (length(display_name) <= 120),
  legal_name         text NOT NULL DEFAULT '' CHECK (length(legal_name) <= 200),
  motto              text NOT NULL DEFAULT '' CHECK (length(motto) <= 140),
  address_line1      text NOT NULL DEFAULT '' CHECK (length(address_line1) <= 160),
  address_line2      text NOT NULL DEFAULT '' CHECK (length(address_line2) <= 160),
  city               text NOT NULL DEFAULT '' CHECK (length(city) <= 80),
  region             text NOT NULL DEFAULT '' CHECK (length(region) <= 80),
  country            text NOT NULL DEFAULT 'Ethiopia' CHECK (length(country) <= 80),
  postal_code        text NOT NULL DEFAULT '' CHECK (length(postal_code) <= 20),
  phone              text NOT NULL DEFAULT '' CHECK (length(phone) <= 40),
  alt_phone          text NOT NULL DEFAULT '' CHECK (length(alt_phone) <= 40),
  email              text NOT NULL DEFAULT '' CHECK (length(email) <= 160),
  website            text NOT NULL DEFAULT '' CHECK (length(website) <= 160),
  tax_id             text NOT NULL DEFAULT '' CHECK (length(tax_id) <= 40),
  registration_no    text NOT NULL DEFAULT '' CHECK (length(registration_no) <= 60),
  licence_no         text NOT NULL DEFAULT '' CHECK (length(licence_no) <= 60),
  opening_hours      text NOT NULL DEFAULT '' CHECK (length(opening_hours) <= 200),
  receipt_footer     text NOT NULL DEFAULT '' CHECK (length(receipt_footer) <= 240),
  document_footer    text NOT NULL DEFAULT '' CHECK (length(document_footer) <= 240),
  default_language   text NOT NULL DEFAULT 'en' CHECK (default_language IN ('en', 'am', 'ti')),
  calendar_display   text NOT NULL DEFAULT 'Both' CHECK (calendar_display IN ('Gregorian', 'Ethiopian', 'Both')),
  currency           text NOT NULL DEFAULT 'ETB' CHECK (currency ~ '^[A-Z]{3}$'),
  updated_by         uuid NULL,
  updated_at         timestamptz NOT NULL,
  version            int NOT NULL DEFAULT 1
);

-- Every export: who, what, range, purpose (required for patient data), rows and SHA-256 of the bytes handed out.
CREATE TABLE platform.export_log (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id     uuid NOT NULL,
  user_id         uuid NOT NULL,
  dataset         text NOT NULL CHECK (dataset ~ '^[a-z0-9.-]{3,60}$'),
  format          text NOT NULL CHECK (format IN ('csv', 'json')),
  range_from      date NULL,
  range_to        date NULL,
  contains_patient_data boolean NOT NULL,
  purpose         text NULL CHECK (purpose IS NULL OR length(purpose) BETWEEN 10 AND 300),
  row_count       int NOT NULL CHECK (row_count >= 0),
  sha256          text NOT NULL CHECK (length(sha256) = 64),
  created_at      timestamptz NOT NULL,
  correlation_id  text NOT NULL,
  CONSTRAINT ck_patient_export_has_purpose CHECK (NOT contains_patient_data OR purpose IS NOT NULL)
);
CREATE INDEX ix_export_log_tenant_time ON platform.export_log (tenant_id, created_at DESC);

-- Backups are never stored on the server; this records that one was produced or verified, and its fingerprint.
CREATE TABLE platform.backup_log (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES identity.tenants (id),
  user_id         uuid NOT NULL,
  action          text NOT NULL CHECK (action IN ('Created', 'Verified', 'VerifyFailed')),
  archive_sha256  text NULL CHECK (archive_sha256 IS NULL OR length(archive_sha256) = 64),
  size_bytes      bigint NOT NULL CHECK (size_bytes >= 0),
  table_count     int NOT NULL CHECK (table_count >= 0),
  row_count       bigint NOT NULL CHECK (row_count >= 0),
  ledger_balanced boolean NULL,
  detail          text NOT NULL DEFAULT '' CHECK (length(detail) <= 500),
  created_at      timestamptz NOT NULL,
  correlation_id  text NOT NULL
);
CREATE INDEX ix_backup_log_tenant_time ON platform.backup_log (tenant_id, created_at DESC);

CREATE OR REPLACE FUNCTION platform.guard_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'serk_immutable: % rows cannot be changed or deleted', TG_TABLE_NAME USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_export_log_append_only BEFORE UPDATE OR DELETE ON platform.export_log FOR EACH ROW EXECUTE FUNCTION platform.guard_append_only();
CREATE TRIGGER trg_backup_log_append_only BEFORE UPDATE OR DELETE ON platform.backup_log FOR EACH ROW EXECUTE FUNCTION platform.guard_append_only();

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['configuration.facility_profiles', 'platform.export_log', 'platform.backup_log'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
  END LOOP;
END $$;
GRANT SELECT, INSERT, UPDATE ON configuration.facility_profiles TO serk_app_role;
GRANT SELECT, INSERT ON platform.export_log, platform.backup_log TO serk_app_role;

-- Published reference views for exports and insights (modules read these, never each other's tables).
-- security_invoker: the caller's row-level security applies (tenant isolation preserved).
CREATE VIEW patients.v_patient_reference WITH (security_invoker = true) AS
  SELECT tenant_id, id AS patient_id, mrn, trim(concat_ws(' ', given_name, father_name, grandfather_name)) AS display_name
  FROM patients.patients;

-- identity tables have no RLS; the view filters to the session tenant explicitly.
CREATE VIEW identity.v_staff_reference AS
  SELECT DISTINCT a.tenant_id, u.id AS user_id, u.display_name, p.id AS practitioner_id
  FROM identity.affiliations a
  JOIN identity.staff_users u ON u.id = a.user_id
  LEFT JOIN identity.practitioners p ON p.user_id = u.id AND p.tenant_id = a.tenant_id
  WHERE a.tenant_id = platform.current_tenant();

GRANT SELECT ON patients.v_patient_reference, identity.v_staff_reference TO serk_app_role;

-- Applied migration names (recorded in backup manifests); checksums stay owner-only.
CREATE VIEW platform.v_applied_migrations AS SELECT script_name AS script FROM platform.schema_migrations;
GRANT SELECT ON platform.v_applied_migrations TO serk_app_role;

-- ---------- PHI-free fact views for the setup checklist and explainable insights ----------
CREATE VIEW configuration.v_setup_facts AS
  SELECT f.tenant_id, f.id AS facility_id,
         coalesce(p.display_name, '') <> '' AS has_name, coalesce(p.motto, '') <> '' AS has_motto,
         coalesce(p.address_line1, '') <> '' AS has_address, coalesce(p.phone, '') <> '' AS has_phone,
         coalesce(p.email, '') <> '' AS has_email, coalesce(p.licence_no, '') <> '' AS has_licence, coalesce(p.tax_id, '') <> '' AS has_tax_id,
         coalesce(p.opening_hours, '') <> '' AS has_hours, coalesce(p.currency, 'ETB') AS currency,
         EXISTS (SELECT 1 FROM configuration.branding_assets b WHERE b.facility_id = f.id AND b.kind = 'Logo' AND b.status = 'Active') AS has_logo,
         EXISTS (SELECT 1 FROM configuration.branding_assets b WHERE b.facility_id = f.id AND b.kind = 'Stamp' AND b.status = 'Active') AS has_stamp,
         EXISTS (SELECT 1 FROM configuration.printer_profiles r WHERE r.facility_id = f.id AND r.is_active AND r.purpose = 'ClinicalDocuments') AS has_document_printer,
         EXISTS (SELECT 1 FROM configuration.printer_profiles r WHERE r.facility_id = f.id AND r.is_active AND r.purpose = 'AppointmentSlips') AS has_slip_printer
  FROM identity.facilities f
  LEFT JOIN configuration.facility_profiles p ON p.facility_id = f.id
  WHERE f.tenant_id = platform.current_tenant();

CREATE VIEW finance.v_setup_facts WITH (security_invoker = true) AS
  SELECT t.id AS tenant_id,
         (SELECT count(*)::int FROM finance.services s WHERE s.is_current) AS service_count,
         (SELECT count(*)::int FROM finance.owner_subscriptions o WHERE o.status = 'Verified') AS active_recipients
  FROM identity.tenants t WHERE t.id = platform.current_tenant();

CREATE VIEW finance.v_payment_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, business_date, tender, status, amount, refunded_amount FROM finance.payments;

CREATE VIEW finance.v_invoice_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, business_date, status, total_amount, paid_amount, issued_at FROM finance.invoices;

CREATE VIEW finance.v_session_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, business_date, status, cashier_user_id, variance, opened_at, counted_at FROM finance.cashier_sessions;

CREATE VIEW finance.v_close_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, business_date, status, close_version FROM finance.daily_closes;

GRANT SELECT ON configuration.v_setup_facts, finance.v_setup_facts, finance.v_payment_facts, finance.v_invoice_facts,
                finance.v_session_facts, finance.v_close_facts TO serk_app_role;

-- Defence in depth: a profile row's tenant must be its facility's tenant.
CREATE UNIQUE INDEX IF NOT EXISTS ux_facilities_id_tenant ON identity.facilities (id, tenant_id);
ALTER TABLE configuration.facility_profiles
  ADD CONSTRAINT fk_profile_facility_tenant FOREIGN KEY (facility_id, tenant_id) REFERENCES identity.facilities (id, tenant_id);
