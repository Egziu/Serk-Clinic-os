-- Module: Finance (owner: finance). Expansion pack v2, phases P1-P3 vertical slice:
-- service catalog -> invoice -> verified payment -> receipt -> balanced double-entry journal -> cashier session ->
-- daily close snapshot -> owner digest delivery evidence. Synthetic data only; account mappings are ILLUSTRATIVE and
-- must be approved by a qualified accountant before real use (SRS v2 §6.2). No tax is computed (FIN-014: no guessed rates).
CREATE SCHEMA IF NOT EXISTS finance;
REVOKE ALL ON SCHEMA finance FROM PUBLIC;
GRANT USAGE ON SCHEMA finance TO serk_app_role;

-- ---------- chart of accounts and versioned posting rules (configured, not hardcoded) ----------
CREATE TABLE finance.accounts (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES identity.tenants (id),
  code        text NOT NULL CHECK (code ~ '^[0-9]{4}$'),
  name        text NOT NULL CHECK (length(name) BETWEEN 2 AND 120),
  type        text NOT NULL CHECK (type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')),
  is_active   boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, code)
);

CREATE TABLE finance.posting_rules (
  id                   uuid PRIMARY KEY,
  tenant_id            uuid NOT NULL REFERENCES identity.tenants (id),
  event_type           text NOT NULL,
  version              int NOT NULL CHECK (version > 0),
  debit_account_code   text NOT NULL,
  credit_account_code  text NOT NULL,
  is_active            boolean NOT NULL DEFAULT true,
  approved_by          uuid NULL,          -- accountant approval; NULL = illustrative default, not approved
  created_at           timestamptz NOT NULL,
  UNIQUE (tenant_id, event_type, version)
);

-- ---------- service / price catalog (each price change is a new immutable version) ----------
CREATE TABLE finance.services (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES identity.tenants (id),
  code        text NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9-]{1,19}$'),
  name        text NOT NULL CHECK (length(name) BETWEEN 2 AND 160),
  unit_price  numeric(14,2) NOT NULL CHECK (unit_price >= 0),
  currency    text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  version     int NOT NULL CHECK (version > 0),
  is_current  boolean NOT NULL DEFAULT true,
  created_by  uuid NOT NULL,
  created_at  timestamptz NOT NULL,
  UNIQUE (tenant_id, code, version)
);
CREATE UNIQUE INDEX ux_services_current ON finance.services (tenant_id, code) WHERE is_current;

-- Human-visible serial numbers per facility and document kind (gapless within committed transactions).
CREATE TABLE finance.document_sequences (
  tenant_id    uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id  uuid NOT NULL REFERENCES identity.facilities (id),
  kind         text NOT NULL CHECK (kind IN ('Invoice', 'Receipt', 'Refund', 'Close')),
  next_no      bigint NOT NULL DEFAULT 1,
  PRIMARY KEY (tenant_id, facility_id, kind)
);

-- ---------- invoices ----------
CREATE TABLE finance.invoices (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id       uuid NOT NULL REFERENCES identity.facilities (id),
  patient_id        uuid NOT NULL REFERENCES patients.patients (id),
  serial            text NULL,
  status            text NOT NULL CHECK (status IN ('Draft', 'PendingApproval', 'Issued', 'PartiallyPaid', 'Paid', 'Cancelled')),
  currency          text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  gross_amount      numeric(14,2) NOT NULL CHECK (gross_amount >= 0),
  discount_amount   numeric(14,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
  total_amount      numeric(14,2) NOT NULL CHECK (total_amount >= 0),
  paid_amount       numeric(14,2) NOT NULL DEFAULT 0 CHECK (paid_amount >= 0),
  discount_reason   text NULL CHECK (discount_reason IS NULL OR length(discount_reason) <= 300),
  created_by        uuid NOT NULL,
  created_at        timestamptz NOT NULL,
  discount_approved_by uuid NULL,
  issued_by         uuid NULL,
  issued_at         timestamptz NULL,
  business_date     date NULL,
  version           int NOT NULL DEFAULT 1,
  CONSTRAINT ck_invoice_totals CHECK (total_amount = gross_amount - discount_amount AND paid_amount <= total_amount),
  CONSTRAINT ck_invoice_issued CHECK ((status IN ('Draft', 'PendingApproval', 'Cancelled')) OR (serial IS NOT NULL AND issued_at IS NOT NULL AND business_date IS NOT NULL)),
  CONSTRAINT ck_discount_maker_checker CHECK (discount_approved_by IS NULL OR discount_approved_by <> created_by),
  UNIQUE (tenant_id, facility_id, serial)
);
CREATE INDEX ix_invoices_patient ON finance.invoices (tenant_id, patient_id);
CREATE INDEX ix_invoices_day ON finance.invoices (tenant_id, facility_id, business_date);

CREATE TABLE finance.invoice_lines (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES identity.tenants (id),
  invoice_id    uuid NOT NULL REFERENCES finance.invoices (id),
  service_id    uuid NOT NULL REFERENCES finance.services (id),   -- exact price version used
  service_code  text NOT NULL,
  description   text NOT NULL,
  quantity      int NOT NULL CHECK (quantity BETWEEN 1 AND 999),
  unit_price    numeric(14,2) NOT NULL CHECK (unit_price >= 0),
  line_total    numeric(14,2) NOT NULL,
  CONSTRAINT ck_line_total CHECK (line_total = quantity * unit_price)
);
CREATE INDEX ix_invoice_lines_invoice ON finance.invoice_lines (invoice_id);
CREATE TRIGGER invoice_lines_immutable BEFORE UPDATE OR DELETE ON finance.invoice_lines
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();

-- Issued invoices: identity, amounts, lines and serial are frozen; only payment progress may change.
CREATE OR REPLACE FUNCTION finance.guard_invoice() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: invoices cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.tenant_id <> OLD.tenant_id OR NEW.facility_id <> OLD.facility_id OR NEW.patient_id <> OLD.patient_id THEN
    RAISE EXCEPTION 'serk_immutable: invoice identity cannot change' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.status IN ('Issued', 'PartiallyPaid', 'Paid')
     AND (NEW.serial IS DISTINCT FROM OLD.serial OR NEW.gross_amount <> OLD.gross_amount OR NEW.discount_amount <> OLD.discount_amount
          OR NEW.total_amount <> OLD.total_amount OR NEW.currency <> OLD.currency OR NEW.issued_at IS DISTINCT FROM OLD.issued_at
          OR NEW.business_date IS DISTINCT FROM OLD.business_date OR NEW.status NOT IN ('Issued', 'PartiallyPaid', 'Paid')) THEN
    RAISE EXCEPTION 'serk_immutable: issued invoices cannot be edited; use a refund or correction' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER invoices_guard BEFORE UPDATE OR DELETE ON finance.invoices
  FOR EACH ROW EXECUTE FUNCTION finance.guard_invoice();

-- ---------- cashier sessions ----------
CREATE TABLE finance.cashier_sessions (
  id                    uuid PRIMARY KEY,
  tenant_id             uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id           uuid NOT NULL REFERENCES identity.facilities (id),
  cashier_user_id       uuid NOT NULL REFERENCES identity.staff_users (id),
  status                text NOT NULL CHECK (status IN ('Open', 'PendingVarianceApproval', 'Closed')),
  opening_float         numeric(14,2) NOT NULL CHECK (opening_float >= 0),
  opened_at             timestamptz NOT NULL,
  business_date         date NOT NULL,
  expected_amount       numeric(14,2) NULL,
  counted_amount        numeric(14,2) NULL CHECK (counted_amount IS NULL OR counted_amount >= 0),
  variance              numeric(14,2) NULL,
  variance_explanation  text NULL CHECK (variance_explanation IS NULL OR length(variance_explanation) <= 500),
  variance_approved_by  uuid NULL,
  counted_at            timestamptz NULL,
  closed_at             timestamptz NULL,
  version               int NOT NULL DEFAULT 1,
  CONSTRAINT ck_variance CHECK (variance IS NULL OR variance = counted_amount - expected_amount),
  CONSTRAINT ck_variance_maker_checker CHECK (variance_approved_by IS NULL OR variance_approved_by <> cashier_user_id)
);
CREATE UNIQUE INDEX ux_one_open_session ON finance.cashier_sessions (tenant_id, facility_id, cashier_user_id) WHERE status = 'Open';
CREATE INDEX ix_sessions_day ON finance.cashier_sessions (tenant_id, facility_id, business_date);

-- ---------- provider settlements ----------
CREATE TABLE finance.settlements (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id  uuid NOT NULL REFERENCES identity.facilities (id),
  provider     text NOT NULL,
  reference    text NOT NULL CHECK (length(reference) BETWEEN 3 AND 80),
  gross_amount numeric(14,2) NOT NULL CHECK (gross_amount > 0),
  fee_amount   numeric(14,2) NOT NULL CHECK (fee_amount >= 0),
  currency     text NOT NULL,
  business_date date NOT NULL,
  created_by   uuid NOT NULL,
  created_at   timestamptz NOT NULL,
  CONSTRAINT ck_fee CHECK (fee_amount < gross_amount),
  UNIQUE (tenant_id, provider, reference)
);

-- ---------- payments (attempt is distinct from verified payment and from settlement) ----------
CREATE TABLE finance.payments (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id         uuid NOT NULL REFERENCES identity.facilities (id),
  invoice_id          uuid NOT NULL REFERENCES finance.invoices (id),
  tender              text NOT NULL CHECK (tender IN ('Cash', 'MobileMoney', 'BankTransfer', 'Card')),
  provider            text NULL,
  provider_reference  text NULL CHECK (provider_reference IS NULL OR length(provider_reference) BETWEEN 3 AND 80),
  amount              numeric(14,2) NOT NULL CHECK (amount > 0),
  currency            text NOT NULL,
  status              text NOT NULL CHECK (status IN ('PendingProvider', 'Verified', 'Failed')),
  receipt_serial      text NULL,
  cashier_session_id  uuid NULL REFERENCES finance.cashier_sessions (id),
  refunded_amount     numeric(14,2) NOT NULL DEFAULT 0 CHECK (refunded_amount >= 0),
  settlement_id       uuid NULL REFERENCES finance.settlements (id),
  created_by          uuid NOT NULL,
  created_at          timestamptz NOT NULL,
  verified_at         timestamptz NULL,
  business_date       date NULL,
  CONSTRAINT ck_cash_needs_session CHECK ((tender = 'Cash') = (cashier_session_id IS NOT NULL)),
  CONSTRAINT ck_electronic_reference CHECK (tender = 'Cash' OR (provider IS NOT NULL AND provider_reference IS NOT NULL)),
  CONSTRAINT ck_verified CHECK (status <> 'Verified' OR (receipt_serial IS NOT NULL AND verified_at IS NOT NULL AND business_date IS NOT NULL)),
  CONSTRAINT ck_refunded CHECK (refunded_amount <= amount)
);
-- The same provider transaction can never create two payments (replayed callbacks are no-ops).
CREATE UNIQUE INDEX ux_payment_provider_ref ON finance.payments (tenant_id, provider, provider_reference) WHERE provider_reference IS NOT NULL;
CREATE UNIQUE INDEX ux_payment_receipt ON finance.payments (tenant_id, facility_id, receipt_serial) WHERE receipt_serial IS NOT NULL;
CREATE INDEX ix_payments_invoice ON finance.payments (invoice_id);
CREATE INDEX ix_payments_day ON finance.payments (tenant_id, facility_id, business_date);

CREATE OR REPLACE FUNCTION finance.guard_payment() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'serk_immutable: payments cannot be deleted' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.amount <> OLD.amount OR NEW.invoice_id <> OLD.invoice_id OR NEW.tender <> OLD.tender OR NEW.currency <> OLD.currency
     OR NEW.tenant_id <> OLD.tenant_id OR NEW.provider_reference IS DISTINCT FROM OLD.provider_reference
     OR (OLD.status = 'Verified' AND (NEW.status <> 'Verified' OR NEW.receipt_serial IS DISTINCT FROM OLD.receipt_serial
                                       OR NEW.business_date IS DISTINCT FROM OLD.business_date)) THEN
    RAISE EXCEPTION 'serk_immutable: payment evidence cannot be edited' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER payments_guard BEFORE UPDATE OR DELETE ON finance.payments
  FOR EACH ROW EXECUTE FUNCTION finance.guard_payment();

-- ---------- refunds (maker-checker enforced in the database as well) ----------
CREATE TABLE finance.refunds (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id         uuid NOT NULL REFERENCES identity.facilities (id),
  payment_id          uuid NOT NULL REFERENCES finance.payments (id),
  serial              text NULL,
  amount              numeric(14,2) NOT NULL CHECK (amount > 0),
  reason              text NOT NULL CHECK (length(reason) BETWEEN 5 AND 500),
  status              text NOT NULL CHECK (status IN ('Requested', 'Paid', 'Rejected')),
  cashier_session_id  uuid NULL REFERENCES finance.cashier_sessions (id),
  requested_by        uuid NOT NULL,
  requested_at        timestamptz NOT NULL,
  decided_by          uuid NULL,
  decided_at          timestamptz NULL,
  business_date       date NULL,
  CONSTRAINT ck_refund_maker_checker CHECK (decided_by IS NULL OR decided_by <> requested_by),
  CONSTRAINT ck_refund_decided CHECK ((status = 'Requested') = (decided_by IS NULL))
);
CREATE INDEX ix_refunds_day ON finance.refunds (tenant_id, facility_id, business_date);

-- ---------- general ledger: immutable, balanced, exactly-once per business effect ----------
CREATE TABLE finance.journal_entries (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id     uuid NOT NULL REFERENCES identity.facilities (id),
  business_date   date NOT NULL,
  source_type     text NOT NULL,
  source_id       uuid NOT NULL,
  purpose         text NOT NULL,
  rule_version    int NOT NULL,
  currency        text NOT NULL,
  narrative       text NOT NULL,
  created_by      uuid NOT NULL,
  occurred_at     timestamptz NOT NULL,
  posted_at       timestamptz NOT NULL,
  correlation_id  text NOT NULL,
  UNIQUE (tenant_id, source_type, source_id, purpose)
);
CREATE INDEX ix_journal_day ON finance.journal_entries (tenant_id, facility_id, business_date);

CREATE TABLE finance.journal_lines (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES identity.tenants (id),
  entry_id    uuid NOT NULL REFERENCES finance.journal_entries (id),
  account_id  uuid NOT NULL REFERENCES finance.accounts (id),
  debit       numeric(14,2) NOT NULL DEFAULT 0 CHECK (debit >= 0),
  credit      numeric(14,2) NOT NULL DEFAULT 0 CHECK (credit >= 0),
  CONSTRAINT ck_one_sided CHECK ((debit = 0) <> (credit = 0))
);
CREATE INDEX ix_journal_lines_entry ON finance.journal_lines (entry_id);
CREATE INDEX ix_journal_lines_account ON finance.journal_lines (tenant_id, account_id);

CREATE TRIGGER journal_entries_immutable BEFORE UPDATE OR DELETE ON finance.journal_entries
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
CREATE TRIGGER journal_lines_immutable BEFORE UPDATE OR DELETE ON finance.journal_lines
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();

-- Debits must equal credits for every entry, checked at COMMIT (deferred), so no unbalanced or empty entry can ever exist.
CREATE OR REPLACE FUNCTION finance.assert_balanced(v_entry uuid) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE v_debit numeric; v_credit numeric; v_count int;
BEGIN
  SELECT coalesce(sum(debit), 0), coalesce(sum(credit), 0), count(*) INTO v_debit, v_credit, v_count
  FROM finance.journal_lines WHERE entry_id = v_entry;
  IF v_count < 2 OR v_debit <> v_credit OR v_debit = 0 THEN
    RAISE EXCEPTION 'serk_ledger: journal entry % is not balanced (debit %, credit %, lines %)', v_entry, v_debit, v_credit, v_count
      USING ERRCODE = 'P0001';
  END IF;
END $$;
CREATE OR REPLACE FUNCTION finance.assert_entry_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN PERFORM finance.assert_balanced(NEW.id); RETURN NULL; END $$;
CREATE OR REPLACE FUNCTION finance.assert_line_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN PERFORM finance.assert_balanced(NEW.entry_id); RETURN NULL; END $$;
CREATE CONSTRAINT TRIGGER journal_entry_balanced AFTER INSERT ON finance.journal_entries
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.assert_entry_balanced();
CREATE CONSTRAINT TRIGGER journal_line_balanced AFTER INSERT ON finance.journal_lines
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.assert_line_balanced();

-- ---------- daily close: immutable snapshot once locked ----------
CREATE TABLE finance.daily_closes (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id       uuid NOT NULL REFERENCES identity.facilities (id),
  business_date     date NOT NULL,
  close_version     int NOT NULL CHECK (close_version > 0),
  serial            text NOT NULL,
  status            text NOT NULL CHECK (status IN ('Submitted', 'Locked', 'Returned')),
  fully_reconciled  boolean NOT NULL,
  snapshot          jsonb NOT NULL,
  snapshot_sha256   text NOT NULL CHECK (length(snapshot_sha256) = 64),
  comment           text NULL CHECK (comment IS NULL OR length(comment) <= 500),
  submitted_by      uuid NOT NULL,
  submitted_at      timestamptz NOT NULL,
  decided_by        uuid NULL,
  decided_at        timestamptz NULL,
  decision_reason   text NULL,
  CONSTRAINT ck_close_maker_checker CHECK (decided_by IS NULL OR decided_by <> submitted_by),
  UNIQUE (tenant_id, facility_id, business_date, close_version)
);
CREATE UNIQUE INDEX ux_one_live_close ON finance.daily_closes (tenant_id, facility_id, business_date) WHERE status IN ('Submitted', 'Locked');

CREATE OR REPLACE FUNCTION finance.guard_daily_close() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' OR OLD.status <> 'Submitted' THEN
    RAISE EXCEPTION 'serk_immutable: a decided daily close is immutable' USING ERRCODE = 'P0001';
  END IF;
  IF (to_jsonb(NEW) - 'status' - 'decided_by' - 'decided_at' - 'decision_reason') IS DISTINCT FROM
     (to_jsonb(OLD) - 'status' - 'decided_by' - 'decided_at' - 'decision_reason') OR NEW.status NOT IN ('Locked', 'Returned') THEN
    RAISE EXCEPTION 'serk_immutable: only the approval decision may be recorded on a submitted close' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER daily_closes_guard BEFORE UPDATE OR DELETE ON finance.daily_closes
  FOR EACH ROW EXECUTE FUNCTION finance.guard_daily_close();

CREATE TABLE finance.close_checks (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES identity.tenants (id),
  close_id    uuid NOT NULL REFERENCES finance.daily_closes (id),
  control_id  text NOT NULL,
  result      text NOT NULL CHECK (result IN ('Pass', 'Fail', 'Conditional')),
  severity    text NOT NULL CHECK (severity IN ('Blocking', 'Conditional')),
  detail      text NOT NULL
);
CREATE TRIGGER close_checks_immutable BEFORE UPDATE OR DELETE ON finance.close_checks
  FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();

-- ---------- owner digest recipients and delivery evidence ----------
CREATE TABLE finance.owner_subscriptions (
  id                    uuid PRIMARY KEY,
  tenant_id             uuid NOT NULL REFERENCES identity.tenants (id),
  facility_id           uuid NOT NULL REFERENCES identity.facilities (id),
  user_id               uuid NOT NULL REFERENCES identity.staff_users (id),
  channel               text NOT NULL CHECK (channel IN ('Telegram', 'Email')),
  address               text NULL,        -- private chat id or verified e-mail; set only by a verified challenge
  status                text NOT NULL CHECK (status IN ('PendingVerification', 'Verified', 'Revoked')),
  challenge_sha256      text NULL,
  challenge_expires_at  timestamptz NULL,
  created_at            timestamptz NOT NULL,
  verified_at           timestamptz NULL,
  revoked_at            timestamptz NULL,
  CONSTRAINT ck_verified_has_address CHECK (status <> 'Verified' OR address IS NOT NULL)
);
CREATE INDEX ix_owner_subscriptions ON finance.owner_subscriptions (tenant_id, facility_id, status);

CREATE TABLE finance.delivery_attempts (
  id                   uuid PRIMARY KEY,
  tenant_id            uuid NOT NULL REFERENCES identity.tenants (id),
  subscription_id      uuid NOT NULL REFERENCES finance.owner_subscriptions (id),
  close_id             uuid NOT NULL REFERENCES finance.daily_closes (id),
  channel              text NOT NULL,
  payload              text NOT NULL,       -- sanitized aggregate digest (no PHI by construction)
  payload_sha256       text NOT NULL,
  status               text NOT NULL CHECK (status IN ('Queued', 'ProviderAccepted', 'RetryPending', 'DeadLetter')),
  attempts             int NOT NULL DEFAULT 0,
  provider_message_id  text NULL,
  last_error           text NULL,
  created_at           timestamptz NOT NULL,
  updated_at           timestamptz NOT NULL,
  UNIQUE (subscription_id, close_id)   -- one digest per close version and recipient; retries reuse the row
);

-- ---------- row-level security (tenant isolation, fail closed) and runtime grants (no DELETE anywhere) ----------
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['accounts', 'posting_rules', 'services', 'document_sequences', 'invoices', 'invoice_lines', 'cashier_sessions',
                           'settlements', 'payments', 'refunds', 'journal_entries', 'journal_lines', 'daily_closes', 'close_checks',
                           'owner_subscriptions', 'delivery_attempts']
  LOOP
    EXECUTE format('ALTER TABLE finance.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE finance.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON finance.%I USING (tenant_id = platform.current_tenant()) WITH CHECK (tenant_id = platform.current_tenant())', t);
    EXECUTE format('GRANT SELECT, INSERT ON finance.%I TO serk_app_role', t);
  END LOOP;
END $$;
GRANT UPDATE ON finance.services, finance.document_sequences, finance.invoices, finance.cashier_sessions, finance.payments,
               finance.refunds, finance.daily_closes, finance.owner_subscriptions, finance.delivery_attempts, finance.posting_rules TO serk_app_role;

-- ---------- bright blue default theme (owner request 2026-11-14); customised colours are left untouched ----------
ALTER TABLE configuration.facility_settings ALTER COLUMN accent_color SET DEFAULT '#1257c9';
UPDATE configuration.facility_settings SET accent_color = '#1257c9', version = version + 1 WHERE accent_color = '#1e4d3a';
