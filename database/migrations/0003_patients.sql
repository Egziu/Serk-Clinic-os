-- Module: Patients (owner: patient registry). Tenant-scoped patient master, allergies.
CREATE SCHEMA IF NOT EXISTS patients;
REVOKE ALL ON SCHEMA patients FROM PUBLIC;
GRANT USAGE ON SCHEMA patients TO serk_app_role;

CREATE TABLE patients.mrn_counters (
  tenant_id   uuid PRIMARY KEY,
  next_value  bigint NOT NULL CHECK (next_value > 0)
);

CREATE TABLE patients.patients (
  id                           uuid PRIMARY KEY,
  tenant_id                    uuid NOT NULL REFERENCES identity.tenants (id),
  mrn                          text NOT NULL,
  given_name                   text NOT NULL CHECK (length(given_name) BETWEEN 1 AND 100),
  father_name                  text NOT NULL CHECK (length(father_name) BETWEEN 1 AND 100),
  grandfather_name             text NOT NULL DEFAULT '' CHECK (length(grandfather_name) <= 100),
  normalized_name              text NOT NULL,
  sex                          text NOT NULL CHECK (sex IN ('Female', 'Male', 'Other', 'Unknown')),
  birth_date                   date NOT NULL,
  birth_date_is_estimated      boolean NOT NULL,
  phone                        text NOT NULL DEFAULT '' CHECK (length(phone) <= 32),
  national_id                  text NULL CHECK (national_id IS NULL OR length(national_id) <= 64),
  allergy_status               text NOT NULL DEFAULT 'Unknown' CHECK (allergy_status IN ('Unknown', 'NoKnownAllergies', 'HasAllergies')),
  allergy_status_updated_at    timestamptz NULL,
  allergy_status_updated_by    uuid NULL,
  registered_at_facility_id    uuid NOT NULL REFERENCES identity.facilities (id),
  created_at                   timestamptz NOT NULL,
  created_by                   uuid NOT NULL,
  version                      int NOT NULL DEFAULT 1,
  UNIQUE (tenant_id, mrn)
);
CREATE UNIQUE INDEX ux_patients_national_id ON patients.patients (tenant_id, national_id) WHERE national_id IS NOT NULL;
CREATE INDEX ix_patients_name ON patients.patients (tenant_id, normalized_name text_pattern_ops);
CREATE INDEX ix_patients_phone ON patients.patients (tenant_id, phone) WHERE phone <> '';
CREATE INDEX ix_patients_birth ON patients.patients (tenant_id, birth_date);

CREATE TABLE patients.allergies (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL,
  patient_id   uuid NOT NULL REFERENCES patients.patients (id),
  substance    text NOT NULL CHECK (length(substance) BETWEEN 1 AND 200),
  reaction     text NOT NULL DEFAULT '' CHECK (length(reaction) <= 500),
  severity     text NOT NULL CHECK (severity IN ('Mild', 'Moderate', 'Severe', 'Unknown')),
  status       text NOT NULL CHECK (status IN ('Active', 'Inactive', 'EnteredInError')),
  recorded_at  timestamptz NOT NULL,
  recorded_by  uuid NOT NULL,
  version      int NOT NULL DEFAULT 1
);
CREATE INDEX ix_allergies_patient ON patients.allergies (tenant_id, patient_id);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['patients.mrn_counters', 'patients.patients', 'patients.allergies'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON %s TO serk_app_role', t);
  END LOOP;
END $$;
