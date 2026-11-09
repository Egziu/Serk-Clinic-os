-- Review round 1 hardening (see docs/reviews/2026-11-09-review-round-1.md).

-- 1. At most one current Final per encounter + template (prevents concurrent finalization producing two "current" documents).
CREATE UNIQUE INDEX ux_documents_one_current_final
  ON documents.document_versions (tenant_id, encounter_id, template_code) WHERE status = 'Final';

-- 2. Public verification must not depend on a superuser owner. A dedicated NOLOGIN role owns the SECURITY DEFINER function
--    and may read only finalized/superseded rows and the minimal columns needed.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'serk_document_verifier') THEN
    CREATE ROLE serk_document_verifier NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END $$;
GRANT serk_document_verifier TO CURRENT_USER;
GRANT USAGE ON SCHEMA documents, identity TO serk_document_verifier;
GRANT SELECT (facility_id, template_code, template_version, status, finalized_at, pdf_sha256, verification_ref)
  ON documents.document_versions TO serk_document_verifier;
GRANT SELECT (id, name) ON identity.facilities TO serk_document_verifier;
CREATE POLICY verifier_reads_issued ON documents.document_versions
  FOR SELECT TO serk_document_verifier USING (status IN ('Final', 'Superseded'));
ALTER FUNCTION documents.verify_reference(text) OWNER TO serk_document_verifier;

-- 3. Encounter identity is fixed at creation: patient, facility, tenant, attending and appointment can never be re-pointed
--    (prevents silently re-attributing a signed note to another patient).
CREATE OR REPLACE FUNCTION clinical.guard_encounter_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: encounters cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.patient_id IS DISTINCT FROM OLD.patient_id OR NEW.facility_id IS DISTINCT FROM OLD.facility_id
     OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.attending_practitioner_id IS DISTINCT FROM OLD.attending_practitioner_id
     OR NEW.appointment_id IS DISTINCT FROM OLD.appointment_id OR NEW.started_at IS DISTINCT FROM OLD.started_at THEN
    RAISE EXCEPTION 'serk_immutable: encounter identity fields cannot change' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Signed' AND (NEW.status IS DISTINCT FROM OLD.status OR NEW.signed_at IS DISTINCT FROM OLD.signed_at) THEN
    RAISE EXCEPTION 'serk_immutable: a signed encounter cannot be reopened' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER encounters_identity_guard BEFORE UPDATE OR DELETE ON clinical.encounters
  FOR EACH ROW EXECUTE FUNCTION clinical.guard_encounter_identity();

-- 4. Documents: patient/facility/encounter/finalization attribution are immutable once set.
CREATE OR REPLACE FUNCTION documents.guard_document_version() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: documents cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.snapshot IS DISTINCT FROM OLD.snapshot OR NEW.snapshot_sha256 IS DISTINCT FROM OLD.snapshot_sha256
     OR NEW.template_code IS DISTINCT FROM OLD.template_code OR NEW.template_version IS DISTINCT FROM OLD.template_version
     OR NEW.note_version_id IS DISTINCT FROM OLD.note_version_id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
     OR NEW.verification_ref IS DISTINCT FROM OLD.verification_ref OR NEW.patient_id IS DISTINCT FROM OLD.patient_id
     OR NEW.facility_id IS DISTINCT FROM OLD.facility_id OR NEW.encounter_id IS DISTINCT FROM OLD.encounter_id
     OR NEW.paper_size IS DISTINCT FROM OLD.paper_size OR NEW.created_by IS DISTINCT FROM OLD.created_by
     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
    RAISE EXCEPTION 'serk_immutable: document binding cannot change' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Final' AND NOT (NEW.status = 'Superseded'
       AND NEW.pdf_object_key IS NOT DISTINCT FROM OLD.pdf_object_key
       AND NEW.pdf_sha256 IS NOT DISTINCT FROM OLD.pdf_sha256
       AND NEW.pdf_size_bytes IS NOT DISTINCT FROM OLD.pdf_size_bytes
       AND NEW.finalized_at IS NOT DISTINCT FROM OLD.finalized_at
       AND NEW.finalized_by IS NOT DISTINCT FROM OLD.finalized_by
       AND NEW.supersedes_id IS NOT DISTINCT FROM OLD.supersedes_id) THEN
    RAISE EXCEPTION 'serk_immutable: a final document can only be superseded' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Superseded' THEN
    RAISE EXCEPTION 'serk_immutable: superseded documents are frozen' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;

-- 5. Security events: also block TRUNCATE.
CREATE TRIGGER security_events_no_truncate BEFORE TRUNCATE ON audit.security_events
  FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();

-- 6. Allergy corrections need a reason column (status changes are audited; no deletes).
ALTER TABLE patients.allergies ADD COLUMN status_reason text NULL CHECK (status_reason IS NULL OR length(status_reason) <= 300);
ALTER TABLE patients.allergies ADD COLUMN status_changed_at timestamptz NULL;
ALTER TABLE patients.allergies ADD COLUMN status_changed_by uuid NULL;
