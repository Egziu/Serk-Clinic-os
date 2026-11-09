-- Module: Scheduling (owner: front desk). Appointments with database-enforced no-double-booking.
CREATE SCHEMA IF NOT EXISTS scheduling;
REVOKE ALL ON SCHEMA scheduling FROM PUBLIC;
GRANT USAGE ON SCHEMA scheduling TO serk_app_role;

CREATE TABLE scheduling.appointments (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL,
  facility_id       uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id        uuid NOT NULL REFERENCES patients.patients (id),
  practitioner_id   uuid NOT NULL REFERENCES identity.practitioners (id),
  starts_at         timestamptz NOT NULL,
  ends_at           timestamptz NOT NULL,
  status            text NOT NULL CHECK (status IN ('Booked', 'CheckedIn', 'Cancelled', 'NoShow', 'Completed')),
  reason            text NOT NULL DEFAULT '' CHECK (length(reason) <= 300),
  encounter_id      uuid NULL,
  created_at        timestamptz NOT NULL,
  created_by        uuid NOT NULL,
  checked_in_at     timestamptz NULL,
  version           int NOT NULL DEFAULT 1,
  CONSTRAINT ck_appointment_interval CHECK (ends_at > starts_at),
  CONSTRAINT ex_no_practitioner_double_booking EXCLUDE USING gist (
    practitioner_id WITH =,
    tstzrange(starts_at, ends_at, '[)') WITH &&
  ) WHERE (status IN ('Booked', 'CheckedIn'))
);
CREATE INDEX ix_appointments_day ON scheduling.appointments (tenant_id, facility_id, starts_at);
ALTER TABLE scheduling.appointments ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.appointments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON scheduling.appointments
  USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant());
GRANT SELECT, INSERT, UPDATE ON scheduling.appointments TO serk_app_role;
