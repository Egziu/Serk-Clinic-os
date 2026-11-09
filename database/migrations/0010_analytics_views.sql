-- Published, PHI-free "fact" views for aggregated analytics (Reporting reads ONLY these views, never module tables).
-- security_invoker => the caller's privileges and row-level security apply (tenant isolation preserved).
CREATE VIEW scheduling.v_appointment_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, practitioner_id, starts_at, ends_at, status, checked_in_at, created_at
  FROM scheduling.appointments;

CREATE VIEW clinical.v_encounter_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, attending_practitioner_id AS practitioner_id, started_at, signed_at, status
  FROM clinical.encounters;

-- Only sex and age at registration (computed), never names, birth dates or identifiers.
CREATE VIEW patients.v_registration_facts WITH (security_invoker = true) AS
  SELECT tenant_id, registered_at_facility_id AS facility_id, created_at, sex,
         date_part('year', age((created_at AT TIME ZONE 'UTC')::date, birth_date))::int AS age_years_at_registration
  FROM patients.patients;

CREATE VIEW documents.v_document_facts WITH (security_invoker = true) AS
  SELECT tenant_id, facility_id, template_code, status, created_at, finalized_at, created_by
  FROM documents.document_versions;

GRANT SELECT ON scheduling.v_appointment_facts, clinical.v_encounter_facts, patients.v_registration_facts, documents.v_document_facts TO serk_app_role;

-- Supporting indexes for analytics range scans.
CREATE INDEX IF NOT EXISTS ix_encounters_facility_started ON clinical.encounters (tenant_id, facility_id, started_at);
CREATE INDEX IF NOT EXISTS ix_patients_facility_created ON patients.patients (tenant_id, registered_at_facility_id, created_at);
CREATE INDEX IF NOT EXISTS ix_documents_facility_created ON documents.document_versions (tenant_id, facility_id, created_at);
