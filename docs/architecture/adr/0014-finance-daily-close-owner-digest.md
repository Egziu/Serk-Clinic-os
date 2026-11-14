# ADR-0014: Finance, daily close and owner digest (Care+ Finance pack v2.0, first slice)

Date: 2026-11-14 · Status: Accepted (engineering); accountant, clinical and legal review pending

## Context
The owner supplied `docs/reference/expansion-v2/` (SRS v2.0, addendum, checklist). Its addendum asks for the finance
vertical slice first: service → invoice → verified payment → receipt → ledger → reconciliation → daily close → owner digest.

## Decisions
1. **One `Finance` module and one `finance` schema** instead of the pack's separate Billing/Treasury/Ledger/Close/Owner
   services. This follows the modular-monolith standard; other modules see only `Serk.Clinic.Finance.Contracts`.
   Splitting later is a refactor inside one module boundary.
2. **Double-entry ledger enforced by the database.** Deferred constraint triggers refuse any commit where an entry's
   debits and credits differ; journal entries, lines, issued invoices and payments are immutable (triggers, no DELETE grant).
   Corrections are new entries. Money is `numeric(18,2)` / `decimal`; no floating point.
3. **Exactly-once posting** through a unique key `(tenant, source_type, source_id, purpose)`; HTTP commands are idempotent
   through the existing idempotency store; provider callbacks are deduplicated by provider reference.
4. **Posting rules are data, versioned per tenant.** A default chart (1000 Cash, 1010 Bank, 1020 Provider clearing,
   1100 Patient receivables, 4000 Service revenue, 5900 Cash over/short, 5910 Provider fees) is seeded on first use.
   It is **illustrative** and has not been approved by an accountant.
5. **Maker-checker** for discounts above 10 %, refunds, cash variances and the close itself: service checks plus
   database CHECK constraints (approver ≠ requester).
6. **Business date** = the facility's local calendar date, skipping days already submitted or locked. A configurable
   cutoff hour and cross-midnight shifts are not implemented.
7. **Daily close** stores a canonical JSON snapshot and its SHA-256. One person submits, another approves; figures are
   recomputed on approval and a stale submission is returned. A locked close cannot change.
8. **Owner digest** is sent from the transactional outbox (`finance.daily-close-locked.v1`) to verified recipients only.
   It contains totals, never patient data. **Only a sandbox channel exists**: nothing leaves the system. Telegram/email
   connectors need owner approval, credentials and a privacy decision first.
9. **Electronic payments use a sandbox provider** with HMAC-SHA256-signed callbacks. A manual "simulate confirmation"
   endpoint exists only in Development/Testing/Demo and only when `Finance:Providers:Sandbox:AllowManualConfirm` is true.
10. **No tax.** The pack forbids guessed rates; invoices carry no tax lines until an accountant supplies the rules.

## Consequences
- Finance figures are reproducible from the ledger and are tested end to end, but must not be used for real books
  until an accountant approves the chart, posting rules, receipt format and tax handling.
- Not built from the pack: Care+ patient journeys and follow-up, service cases/complaints, close PDF/export,
  supplemental close versions, period close, payables, inventory, insurance, real provider and messaging connectors,
  recipient MFA. See `docs/progress/FINANCE_PACK_STATUS.md`.
