-- Branding images up to 20 MB (owner request 2026-11-15; dimensions still capped at 4096 px).
ALTER TABLE configuration.branding_assets DROP CONSTRAINT IF EXISTS branding_assets_size_bytes_check;
ALTER TABLE configuration.branding_assets ADD CONSTRAINT branding_assets_size_bytes_check CHECK (size_bytes BETWEEN 1 AND 20971520);

-- ============================== Care+ (patient communication and follow-up) ==============================
-- Synthetic data only. Messages leave the system only through a configured provider (sandbox by default); every patient
-- message needs that patient's recorded consent for its purpose; texts carry no diagnosis or clinical detail.
CREATE SCHEMA IF NOT EXISTS care;
REVOKE ALL ON SCHEMA care FROM PUBLIC;
GRANT USAGE ON SCHEMA care TO serk_app_role;

CREATE TABLE care.contact_preferences (
  patient_id          uuid PRIMARY KEY REFERENCES patients.patients (id),
  tenant_id           uuid NOT NULL REFERENCES identity.tenants (id),
  sms_phone           text NOT NULL DEFAULT '' CHECK (sms_phone = '' OR sms_phone ~ '^\+?[0-9]{9,15}$'),
  telegram_chat_id    text NULL CHECK (telegram_chat_id IS NULL OR telegram_chat_id ~ '^-?[0-9]{1,20}$'),
  preferred_channel   text NOT NULL DEFAULT 'None' CHECK (preferred_channel IN ('None', 'Sms', 'Telegram')),
  language            text NOT NULL DEFAULT 'en' CHECK (language IN ('en', 'am', 'ti')),
  consent_reminders   boolean NOT NULL DEFAULT false,
  consent_tips        boolean NOT NULL DEFAULT false,
  consent_method      text NULL CHECK (consent_method IS NULL OR consent_method IN ('Verbal', 'Written', 'Telegram')),
  updated_by          uuid NOT NULL,
  updated_at          timestamptz NOT NULL,
  version             int NOT NULL DEFAULT 1
);

-- Evidence of every consent change (append-only).
CREATE TABLE care.consent_events (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES identity.tenants (id),
  patient_id        uuid NOT NULL REFERENCES patients.patients (id),
  consent_reminders boolean NOT NULL,
  consent_tips      boolean NOT NULL,
  method            text NOT NULL CHECK (method IN ('Verbal', 'Written', 'Telegram', 'OptOutReply', 'StaffWithdrawal')),
  recorded_by       uuid NOT NULL,
  recorded_at       timestamptz NOT NULL
);

CREATE TABLE care.message_templates (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES identity.tenants (id),
  kind        text NOT NULL CHECK (kind IN ('Confirmation', 'Reminder', 'FollowUp', 'Rebook')),
  language    text NOT NULL CHECK (language IN ('en', 'am', 'ti')),
  body        text NOT NULL CHECK (length(body) BETWEEN 10 AND 600),
  is_enabled  boolean NOT NULL DEFAULT true,
  updated_by  uuid NOT NULL,
  updated_at  timestamptz NOT NULL,
  version     int NOT NULL DEFAULT 1,
  UNIQUE (tenant_id, kind, language)
);

CREATE TABLE care.messages (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id         uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id          uuid NOT NULL REFERENCES patients.patients (id),
  kind                text NOT NULL CHECK (kind IN ('Confirmation', 'Reminder', 'FollowUp', 'Rebook', 'Tip')),
  source_id           uuid NOT NULL,         -- appointment id or campaign id
  channel             text NOT NULL CHECK (channel IN ('Sms', 'Telegram')),
  address             text NOT NULL,
  body                text NOT NULL CHECK (length(body) BETWEEN 1 AND 1000),
  due_at              timestamptz NOT NULL,
  status              text NOT NULL CHECK (status IN ('Scheduled', 'Sent', 'Failed', 'Skipped', 'Cancelled')),
  attempts            int NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 10),
  provider_message_id text NULL,
  last_error          text NULL CHECK (last_error IS NULL OR length(last_error) <= 300),
  sent_at             timestamptz NULL,
  created_at          timestamptz NOT NULL,
  UNIQUE (tenant_id, kind, source_id, patient_id)   -- exactly one message per purpose, appointment/campaign and patient
);
CREATE INDEX ix_care_messages_due ON care.messages (tenant_id, status, due_at);

CREATE TABLE care.replies (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES identity.tenants (id),
  patient_id        uuid NOT NULL REFERENCES patients.patients (id),
  message_id        uuid NULL REFERENCES care.messages (id),
  channel           text NOT NULL CHECK (channel IN ('Sms', 'Telegram', 'Phone')),
  text              text NOT NULL CHECK (length(text) BETWEEN 1 AND 1000),
  rating            int NULL CHECK (rating BETWEEN 1 AND 5),
  needs_attention   boolean NOT NULL,
  opt_out           boolean NOT NULL DEFAULT false,
  received_at       timestamptz NOT NULL,
  external_id       text NULL,
  UNIQUE (tenant_id, channel, external_id)
);

CREATE TABLE care.tasks (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id   uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id    uuid NOT NULL REFERENCES patients.patients (id),
  source        text NOT NULL CHECK (source IN ('Reply', 'NoShow', 'Manual')),
  reply_id      uuid NULL REFERENCES care.replies (id),
  priority      text NOT NULL CHECK (priority IN ('High', 'Normal')),
  summary       text NOT NULL CHECK (length(summary) BETWEEN 3 AND 300),
  status        text NOT NULL CHECK (status IN ('Open', 'Done')),
  due_at        timestamptz NOT NULL,
  created_at    timestamptz NOT NULL,
  done_by       uuid NULL,
  done_at       timestamptz NULL,
  outcome       text NULL CHECK (outcome IS NULL OR length(outcome) BETWEEN 3 AND 500),
  version       int NOT NULL DEFAULT 1,
  CONSTRAINT ck_task_done CHECK ((status = 'Done') = (done_by IS NOT NULL AND done_at IS NOT NULL AND outcome IS NOT NULL))
);
CREATE UNIQUE INDEX ux_care_task_reply ON care.tasks (reply_id) WHERE reply_id IS NOT NULL;

CREATE TABLE care.health_tips (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES identity.tenants (id),
  title         text NOT NULL CHECK (length(title) BETWEEN 3 AND 120),
  body          text NOT NULL CHECK (length(body) BETWEEN 20 AND 600),
  topic         text NOT NULL CHECK (length(topic) BETWEEN 2 AND 60),
  language      text NOT NULL CHECK (language IN ('en', 'am', 'ti')),
  status        text NOT NULL CHECK (status IN ('Draft', 'Approved', 'Retired')),
  author_id     uuid NOT NULL,
  last_editor_id uuid NOT NULL,
  approved_by   uuid NULL,
  approved_at   timestamptz NULL,
  created_at    timestamptz NOT NULL,
  version       int NOT NULL DEFAULT 1,
  -- Four eyes: the approver is neither the author nor whoever last changed the text.
  CONSTRAINT ck_tip_four_eyes CHECK (approved_by IS NULL OR (approved_by <> author_id AND approved_by <> last_editor_id)),
  CONSTRAINT ck_tip_approved CHECK ((status = 'Draft') = (approved_by IS NULL))
);

-- Approved text cannot change (a new tip must be written and approved instead).
CREATE OR REPLACE FUNCTION care.guard_tip() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.author_id <> OLD.author_id THEN
    RAISE EXCEPTION 'serk_immutable: the author of a health tip cannot change' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status <> 'Draft' AND (NEW.title <> OLD.title OR NEW.body <> OLD.body OR NEW.language <> OLD.language OR NEW.topic <> OLD.topic
      OR NEW.approved_by IS DISTINCT FROM OLD.approved_by OR NEW.last_editor_id <> OLD.last_editor_id) THEN
    RAISE EXCEPTION 'serk_immutable: approved health tips cannot be edited' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status = 'Retired' AND NEW.status <> 'Retired' THEN
    RAISE EXCEPTION 'serk_immutable: a retired health tip cannot be reused' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_tip_guard BEFORE UPDATE ON care.health_tips FOR EACH ROW EXECUTE FUNCTION care.guard_tip();

CREATE TABLE care.campaigns (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id    uuid NOT NULL REFERENCES identity.facilities (id),
  tip_id         uuid NOT NULL REFERENCES care.health_tips (id),
  audience       text NOT NULL CHECK (audience IN ('AllConsented', 'VisitedRecently')),
  months         int NULL CHECK (months IS NULL OR months BETWEEN 1 AND 60),
  scheduled_at   timestamptz NOT NULL,
  status         text NOT NULL CHECK (status IN ('Scheduled', 'Queued', 'Cancelled')),
  recipients     int NOT NULL DEFAULT 0,
  created_by     uuid NOT NULL,
  created_at     timestamptz NOT NULL,
  queued_at      timestamptz NULL,
  version        int NOT NULL DEFAULT 1
);

-- Telegram linking: a short-lived code shown at reception; the patient sends it to the clinic bot. Only reachable through the
-- SECURITY DEFINER functions below (the application role has no direct access), so one tenant cannot enumerate another.
CREATE TABLE care.telegram_links (
  code_sha256  text PRIMARY KEY CHECK (length(code_sha256) = 64),
  tenant_id    uuid NOT NULL REFERENCES identity.tenants (id),
  patient_id   uuid NOT NULL REFERENCES patients.patients (id),
  expires_at   timestamptz NOT NULL,
  used_at      timestamptz NULL
);
CREATE TABLE care.telegram_chats (
  chat_id      text PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES identity.tenants (id),
  patient_id   uuid NOT NULL REFERENCES patients.patients (id),
  linked_at    timestamptz NOT NULL
);

CREATE OR REPLACE FUNCTION care.create_telegram_link(p_code_sha256 text, p_patient uuid, p_expires timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, care, pg_temp AS $$
DECLARE t uuid := platform.current_tenant();
BEGIN
  IF t IS NULL OR NOT EXISTS (SELECT 1 FROM patients.patients WHERE id = p_patient AND tenant_id = t) THEN
    RAISE EXCEPTION 'serk_forbidden: patient not in tenant' USING ERRCODE = 'P0001';
  END IF;
  DELETE FROM care.telegram_links WHERE patient_id = p_patient OR expires_at < now();
  INSERT INTO care.telegram_links (code_sha256, tenant_id, patient_id, expires_at) VALUES (p_code_sha256, t, p_patient, p_expires);
END $$;

-- Used by the bot webhook (no tenant context yet): consumes the code once and binds the chat.
CREATE OR REPLACE FUNCTION care.claim_telegram_link(p_code_sha256 text, p_chat_id text) RETURNS TABLE (tenant_id uuid, patient_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, care, pg_temp AS $$
DECLARE l care.telegram_links;
BEGIN
  UPDATE care.telegram_links x SET used_at = now()
   WHERE x.code_sha256 = p_code_sha256 AND x.used_at IS NULL AND x.expires_at > now()
   RETURNING * INTO l;
  IF l.code_sha256 IS NULL THEN
    RETURN;
  END IF;
  INSERT INTO care.telegram_chats (chat_id, tenant_id, patient_id, linked_at) VALUES (p_chat_id, l.tenant_id, l.patient_id, now())
  ON CONFLICT (chat_id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, patient_id = EXCLUDED.patient_id, linked_at = now();
  RETURN QUERY SELECT l.tenant_id, l.patient_id;
END $$;

CREATE OR REPLACE FUNCTION care.resolve_telegram_chat(p_chat_id text) RETURNS TABLE (tenant_id uuid, patient_id uuid)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, care, pg_temp AS $$
  SELECT c.tenant_id, c.patient_id FROM care.telegram_chats c WHERE c.chat_id = p_chat_id
$$;

CREATE OR REPLACE FUNCTION care.unlink_telegram_chat(p_patient uuid) RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, care, pg_temp AS $$
  DELETE FROM care.telegram_chats WHERE patient_id = p_patient AND tenant_id = platform.current_tenant();
  DELETE FROM care.telegram_links WHERE patient_id = p_patient AND tenant_id = platform.current_tenant();
$$;

REVOKE ALL ON care.telegram_links, care.telegram_chats FROM PUBLIC;
REVOKE ALL ON FUNCTION care.create_telegram_link(text, uuid, timestamptz), care.claim_telegram_link(text, text),
  care.resolve_telegram_chat(text), care.unlink_telegram_chat(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION care.create_telegram_link(text, uuid, timestamptz), care.claim_telegram_link(text, text),
  care.resolve_telegram_chat(text), care.unlink_telegram_chat(uuid) TO serk_app_role;

-- ============================== Social media planner and tracker ==============================
CREATE TABLE care.social_posts (
  id                     uuid PRIMARY KEY,
  tenant_id              uuid NOT NULL REFERENCES identity.tenants (id),
  platform               text NOT NULL CHECK (platform IN ('Facebook', 'Instagram', 'TikTok', 'Telegram', 'X', 'LinkedIn', 'YouTube', 'WhatsApp')),
  planned_at             timestamptz NOT NULL,
  title                  text NOT NULL CHECK (length(title) BETWEEN 3 AND 120),
  caption                text NOT NULL DEFAULT '' CHECK (length(caption) <= 2200),
  status                 text NOT NULL CHECK (status IN ('Idea', 'Draft', 'Scheduled', 'Published', 'Cancelled')),
  campaign               text NOT NULL DEFAULT '' CHECK (length(campaign) <= 80),
  owner_user_id          uuid NULL,
  shows_patient          boolean NOT NULL DEFAULT false,
  patient_consent_ref    text NULL CHECK (patient_consent_ref IS NULL OR length(patient_consent_ref) BETWEEN 3 AND 120),
  published_url          text NULL CHECK (published_url IS NULL OR published_url ~ '^https://'),
  published_at           timestamptz NULL,
  created_by             uuid NOT NULL,
  created_at             timestamptz NOT NULL,
  version                int NOT NULL DEFAULT 1,
  -- A post showing a patient (photo, story, testimonial) needs a recorded written-consent reference.
  CONSTRAINT ck_post_patient_consent CHECK (NOT shows_patient OR patient_consent_ref IS NOT NULL),
  CONSTRAINT ck_post_published CHECK ((status = 'Published') = (published_at IS NOT NULL))
);
CREATE INDEX ix_social_posts_planned ON care.social_posts (tenant_id, planned_at);

CREATE TABLE care.social_metrics (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES identity.tenants (id),
  platform      text NOT NULL CHECK (platform IN ('Facebook', 'Instagram', 'TikTok', 'Telegram', 'X', 'LinkedIn', 'YouTube', 'WhatsApp')),
  measured_on   date NOT NULL,
  followers     int NOT NULL CHECK (followers >= 0),
  reach         int NOT NULL DEFAULT 0 CHECK (reach >= 0),
  engagements   int NOT NULL DEFAULT 0 CHECK (engagements >= 0),
  inquiries     int NOT NULL DEFAULT 0 CHECK (inquiries >= 0),
  bookings      int NOT NULL DEFAULT 0 CHECK (bookings >= 0),
  entered_by    uuid NOT NULL,
  entered_at    timestamptz NOT NULL,
  UNIQUE (tenant_id, platform, measured_on)
);

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['contact_preferences', 'consent_events', 'message_templates', 'messages', 'replies', 'tasks',
                           'health_tips', 'campaigns', 'social_posts', 'social_metrics'] LOOP
    EXECUTE format('ALTER TABLE care.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE care.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON care.%I USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
    EXECUTE format('GRANT SELECT, INSERT ON care.%I TO serk_app_role', t);
  END LOOP;
END $$;
GRANT UPDATE ON care.contact_preferences, care.message_templates, care.messages, care.tasks, care.health_tips, care.campaigns,
                care.social_posts, care.social_metrics TO serk_app_role;
GRANT DELETE ON care.social_metrics TO serk_app_role;

CREATE OR REPLACE FUNCTION care.guard_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'serk_immutable: % rows cannot be changed or deleted', TG_TABLE_NAME USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_consent_events_append_only BEFORE UPDATE OR DELETE ON care.consent_events FOR EACH ROW EXECUTE FUNCTION care.guard_append_only();
CREATE TRIGGER trg_replies_append_only BEFORE UPDATE OR DELETE ON care.replies FOR EACH ROW EXECUTE FUNCTION care.guard_append_only();

-- Published views used by Care+ (read-only, tenant-scoped).
CREATE VIEW scheduling.v_appointment_care WITH (security_invoker = true) AS
  SELECT id, tenant_id, facility_id, patient_id, practitioner_id, starts_at, status, checked_in_at, created_at
  FROM scheduling.appointments;
CREATE VIEW configuration.v_clinic_identity AS
  SELECT f.tenant_id, f.id AS facility_id, coalesce(nullif(p.display_name, ''), f.name) AS clinic_name,
         coalesce(nullif(p.phone, ''), f.phone) AS clinic_phone, f.time_zone
  FROM identity.facilities f LEFT JOIN configuration.facility_profiles p ON p.facility_id = f.id
  WHERE f.tenant_id = platform.current_tenant();
GRANT SELECT ON scheduling.v_appointment_care, configuration.v_clinic_identity TO serk_app_role;
CREATE VIEW patients.v_patient_contact WITH (security_invoker = true) AS
  SELECT tenant_id, id AS patient_id, mrn, trim(concat_ws(' ', given_name, father_name, grandfather_name)) AS display_name, given_name, phone,
         created_at, registered_at_facility_id
  FROM patients.patients;
GRANT SELECT ON patients.v_patient_contact TO serk_app_role;
