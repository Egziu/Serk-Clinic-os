-- Module: Dental specialty pack (owner: dental). Append-only, encounter-attached tooth findings in FDI notation.
CREATE SCHEMA IF NOT EXISTS dental;
REVOKE ALL ON SCHEMA dental FROM PUBLIC;
GRANT USAGE ON SCHEMA dental TO serk_app_role;

CREATE TABLE dental.findings (
  id                          uuid PRIMARY KEY,
  tenant_id                   uuid NOT NULL,
  patient_id                  uuid NOT NULL REFERENCES patients.patients (id),
  encounter_id                uuid NOT NULL REFERENCES clinical.encounters (id),
  tooth_code                  smallint NOT NULL CHECK (
                                (tooth_code / 10 BETWEEN 1 AND 4 AND tooth_code % 10 BETWEEN 1 AND 8) OR
                                (tooth_code / 10 BETWEEN 5 AND 8 AND tooth_code % 10 BETWEEN 1 AND 5)),
  surfaces                    text[] NOT NULL DEFAULT '{}' CHECK (surfaces <@ ARRAY['Mesial','Distal','Occlusal','Incisal','Buccal','Lingual']),
  condition                   text NOT NULL CHECK (condition IN ('Sound','Caries','Restored','Crown','RootCanalTreated','Fractured','Sealant','Missing')),
  note                        text NOT NULL DEFAULT '' CHECK (length(note) <= 500),
  corrects_entry_id           uuid NULL UNIQUE REFERENCES dental.findings (id),
  correction_reason           text NULL CHECK (correction_reason IS NULL OR length(correction_reason) <= 300),
  recorded_by_user_id         uuid NOT NULL,
  recorded_by_practitioner_id uuid NOT NULL REFERENCES identity.practitioners (id),
  recorded_at                 timestamptz NOT NULL,
  CONSTRAINT ck_correction_has_reason CHECK ((corrects_entry_id IS NULL) = (correction_reason IS NULL))
);
CREATE INDEX ix_dental_findings_patient ON dental.findings (tenant_id, patient_id, tooth_code, recorded_at);
CREATE TRIGGER dental_findings_append_only BEFORE UPDATE OR DELETE ON dental.findings
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
ALTER TABLE dental.findings ENABLE ROW LEVEL SECURITY;
ALTER TABLE dental.findings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON dental.findings
  USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant());
GRANT SELECT, INSERT ON dental.findings TO serk_app_role;
