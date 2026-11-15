-- Automation settings per facility, dental recalls, and the staff notice board.

-- ---------- automation settings (owner-adjustable; defaults apply when no row exists) ----------
CREATE TABLE care.automation_settings (
  facility_id                 uuid PRIMARY KEY REFERENCES identity.facilities (id),
  tenant_id                   uuid NOT NULL REFERENCES identity.tenants (id),
  confirmations_enabled       boolean NOT NULL DEFAULT true,
  reminder_enabled            boolean NOT NULL DEFAULT true,
  reminder_lead_hours         int NOT NULL DEFAULT 24 CHECK (reminder_lead_hours BETWEEN 2 AND 72),
  same_day_reminder_enabled   boolean NOT NULL DEFAULT false,
  same_day_lead_hours         int NOT NULL DEFAULT 2 CHECK (same_day_lead_hours BETWEEN 1 AND 6),
  follow_up_enabled           boolean NOT NULL DEFAULT true,
  follow_up_delay_hours       int NOT NULL DEFAULT 24 CHECK (follow_up_delay_hours BETWEEN 2 AND 96),
  rebook_enabled              boolean NOT NULL DEFAULT true,
  quiet_start                 time NOT NULL DEFAULT '20:00',
  quiet_end                   time NOT NULL DEFAULT '08:00',
  recall_invite_lead_days     int NOT NULL DEFAULT 14 CHECK (recall_invite_lead_days BETWEEN 1 AND 60),
  recall_overdue_days         int NOT NULL DEFAULT 14 CHECK (recall_overdue_days BETWEEN 1 AND 90),
  auto_checkup_recall_enabled boolean NOT NULL DEFAULT true,
  auto_checkup_months         int NOT NULL DEFAULT 6 CHECK (auto_checkup_months BETWEEN 3 AND 24),
  staff_notices_enabled       boolean NOT NULL DEFAULT true,
  updated_by                  uuid NOT NULL,
  updated_at                  timestamptz NOT NULL,
  version                     int NOT NULL DEFAULT 1,
  CONSTRAINT ck_quiet_window CHECK (quiet_start <> quiet_end)
);

ALTER TABLE care.messages DROP CONSTRAINT messages_kind_check;
ALTER TABLE care.messages ADD CONSTRAINT messages_kind_check CHECK (kind IN ('Confirmation', 'Reminder', 'SameDayReminder', 'FollowUp', 'Rebook', 'Recall', 'Tip'));
ALTER TABLE care.message_templates DROP CONSTRAINT message_templates_kind_check;
ALTER TABLE care.message_templates ADD CONSTRAINT message_templates_kind_check CHECK (kind IN ('Confirmation', 'Reminder', 'SameDayReminder', 'FollowUp', 'Rebook', 'Recall'));
ALTER TABLE care.tasks DROP CONSTRAINT tasks_source_check;
ALTER TABLE care.tasks ADD CONSTRAINT tasks_source_check CHECK (source IN ('Reply', 'NoShow', 'Manual', 'Recall'));

-- ---------- recalls: "see this patient again by <date>" ----------
CREATE TABLE care.recalls (
  id                      uuid PRIMARY KEY,
  tenant_id               uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id             uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id              uuid NOT NULL REFERENCES patients.patients (id),
  kind                    text NOT NULL CHECK (kind IN ('CheckUp', 'Review', 'Cleaning', 'Treatment', 'Other')),
  note                    text NOT NULL DEFAULT '' CHECK (length(note) <= 200),   -- staff-only; never sent to the patient
  due_on                  date NOT NULL,
  status                  text NOT NULL CHECK (status IN ('Due', 'Booked', 'Done', 'Cancelled')),
  source                  text NOT NULL CHECK (source IN ('Clinician', 'Reception', 'Automatic')),
  source_appointment_id   uuid NULL,
  booked_appointment_id   uuid NULL,
  invited_at              timestamptz NULL,
  escalated_at            timestamptz NULL,
  created_by              uuid NOT NULL,
  created_at              timestamptz NOT NULL,
  closed_by               uuid NULL,
  closed_at               timestamptz NULL,
  close_reason            text NULL CHECK (close_reason IS NULL OR length(close_reason) BETWEEN 3 AND 200),
  version                 int NOT NULL DEFAULT 1
);
CREATE INDEX ix_recalls_due ON care.recalls (tenant_id, facility_id, status, due_on);
CREATE UNIQUE INDEX ux_recall_auto_source ON care.recalls (tenant_id, source_appointment_id) WHERE source = 'Automatic';

-- ---------- notice board (staff communication; never patient information) ----------
CREATE SCHEMA IF NOT EXISTS comms;
REVOKE ALL ON SCHEMA comms FROM PUBLIC;
GRANT USAGE ON SCHEMA comms TO serk_app_role;

CREATE TABLE comms.notices (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id     uuid NULL REFERENCES identity.facilities (id),   -- NULL = every facility of the organisation
  title           text NOT NULL CHECK (length(title) BETWEEN 3 AND 120),
  body            text NOT NULL CHECK (length(body) BETWEEN 1 AND 4000),
  priority        text NOT NULL CHECK (priority IN ('Normal', 'Important', 'Urgent')),
  audience_roles  text[] NOT NULL DEFAULT '{}',                    -- empty = everyone
  pinned          boolean NOT NULL DEFAULT false,
  requires_ack    boolean NOT NULL DEFAULT false,
  expires_at      timestamptz NULL,
  source          text NOT NULL CHECK (source IN ('Staff', 'System')),
  system_key      text NULL CHECK (system_key IS NULL OR length(system_key) <= 120),
  author_id       uuid NULL,
  status          text NOT NULL CHECK (status IN ('Active', 'Archived')),
  created_at      timestamptz NOT NULL,
  updated_at      timestamptz NOT NULL,
  archived_by     uuid NULL,
  archived_at     timestamptz NULL,
  version         int NOT NULL DEFAULT 1,
  CONSTRAINT ck_notice_author CHECK ((source = 'Staff') = (author_id IS NOT NULL)),
  CONSTRAINT ck_notice_system_key CHECK ((source = 'System') = (system_key IS NOT NULL))
);
CREATE UNIQUE INDEX ux_notice_system_key ON comms.notices (tenant_id, system_key) WHERE system_key IS NOT NULL;
CREATE INDEX ix_notices_active ON comms.notices (tenant_id, status, created_at DESC);

CREATE TABLE comms.notice_reads (
  notice_id        uuid NOT NULL REFERENCES comms.notices (id),
  user_id          uuid NOT NULL,
  tenant_id        uuid NOT NULL REFERENCES identity.tenants (id),
  read_at          timestamptz NOT NULL,
  acknowledged_at  timestamptz NULL,
  PRIMARY KEY (notice_id, user_id)
);

-- Roles of each assignment in the current organisation (for notice audiences); tenant-filtered.
-- Recall invitations ("time for your check-up") need their own consent, separate from appointment reminders.
ALTER TABLE care.contact_preferences ADD COLUMN consent_recalls boolean NOT NULL DEFAULT false;
ALTER TABLE care.consent_events ADD COLUMN consent_recalls boolean NOT NULL DEFAULT false;

CREATE VIEW identity.v_assignment_roles AS
  SELECT a.id AS affiliation_id, a.tenant_id, a.user_id, a.facility_id, a.roles, a.is_active
  FROM identity.affiliations a WHERE a.tenant_id = platform.current_tenant();

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['care.automation_settings', 'care.recalls', 'comms.notices', 'comms.notice_reads'] LOOP
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON %s TO serk_app_role', t);
  END LOOP;
END $$;
GRANT SELECT ON identity.v_assignment_roles TO serk_app_role;
