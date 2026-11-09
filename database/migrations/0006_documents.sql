-- Module: Documents (owner: document studio). Versioned documents bound to an exact data snapshot + template version.
CREATE SCHEMA IF NOT EXISTS documents;
REVOKE ALL ON SCHEMA documents FROM PUBLIC;
GRANT USAGE ON SCHEMA documents TO serk_app_role;

CREATE TABLE documents.document_versions (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL,
  facility_id         uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id          uuid NOT NULL REFERENCES patients.patients (id),
  encounter_id        uuid NOT NULL REFERENCES clinical.encounters (id),
  note_version_id     uuid NOT NULL REFERENCES clinical.note_versions (id),
  template_code       text NOT NULL,
  template_version    int NOT NULL,
  paper_size          text NOT NULL CHECK (paper_size IN ('A4', 'A5')),
  snapshot            jsonb NOT NULL,
  snapshot_sha256     text NOT NULL CHECK (length(snapshot_sha256) = 64),
  status              text NOT NULL CHECK (status IN ('Draft', 'Final', 'Superseded')),
  verification_ref    text NOT NULL UNIQUE,
  created_at          timestamptz NOT NULL,
  created_by          uuid NOT NULL,
  finalized_at        timestamptz NULL,
  finalized_by        uuid NULL,
  pdf_object_key      text NULL,
  pdf_sha256          text NULL,
  pdf_size_bytes      int NULL,
  supersedes_id       uuid NULL REFERENCES documents.document_versions (id),
  version             int NOT NULL DEFAULT 1,
  CONSTRAINT ck_final_has_pdf CHECK (status = 'Draft' OR (pdf_object_key IS NOT NULL AND pdf_sha256 IS NOT NULL AND finalized_at IS NOT NULL))
);
CREATE INDEX ix_documents_encounter ON documents.document_versions (tenant_id, encounter_id);

-- Draft rows may change only status/finalization columns; snapshot and template are frozen at creation.
-- Final rows may only transition to Superseded. Deletion is always prohibited.
CREATE OR REPLACE FUNCTION documents.guard_document_version() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: documents cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.snapshot IS DISTINCT FROM OLD.snapshot OR NEW.snapshot_sha256 IS DISTINCT FROM OLD.snapshot_sha256
     OR NEW.template_code IS DISTINCT FROM OLD.template_code OR NEW.template_version IS DISTINCT FROM OLD.template_version
     OR NEW.note_version_id IS DISTINCT FROM OLD.note_version_id OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
     OR NEW.verification_ref IS DISTINCT FROM OLD.verification_ref THEN
    RAISE EXCEPTION 'serk_immutable: document snapshot/template binding cannot change' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Final' AND NOT (NEW.status = 'Superseded'
       AND NEW.pdf_object_key IS NOT DISTINCT FROM OLD.pdf_object_key
       AND NEW.pdf_sha256 IS NOT DISTINCT FROM OLD.pdf_sha256
       AND NEW.finalized_at IS NOT DISTINCT FROM OLD.finalized_at) THEN
    RAISE EXCEPTION 'serk_immutable: a final document can only be superseded' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Superseded' THEN
    RAISE EXCEPTION 'serk_immutable: superseded documents are frozen' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER document_versions_guard BEFORE UPDATE OR DELETE ON documents.document_versions
  FOR EACH ROW EXECUTE FUNCTION documents.guard_document_version();

ALTER TABLE documents.document_versions ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents.document_versions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON documents.document_versions
  USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant());
GRANT SELECT, INSERT, UPDATE ON documents.document_versions TO serk_app_role;

-- Public verification by opaque reference (QR). Returns no patient data. SECURITY DEFINER bypasses tenant RLS for this
-- narrow, read-only projection only.
CREATE OR REPLACE FUNCTION documents.verify_reference(p_ref text)
RETURNS TABLE (facility_name text, template_code text, template_version int, status text, finalized_at timestamptz, pdf_sha256 text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, documents, identity AS $$
  SELECT f.name, d.template_code, d.template_version, d.status, d.finalized_at, d.pdf_sha256
  FROM documents.document_versions d JOIN identity.facilities f ON f.id = d.facility_id
  WHERE d.verification_ref = p_ref AND d.status IN ('Final', 'Superseded')
$$;
REVOKE ALL ON FUNCTION documents.verify_reference(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION documents.verify_reference(text) TO serk_app_role;
