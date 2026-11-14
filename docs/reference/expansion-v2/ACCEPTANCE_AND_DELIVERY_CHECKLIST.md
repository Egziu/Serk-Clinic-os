# SERK Clinic OS — Release Acceptance & Developer Evidence Checklist

**Status:** Template; every checkbox must be completed from actual evidence, never assumption.

## Finance and close

- [ ] Real double-entry journal posting with active mapping versions; debit=credit invariant enforced and proven.
- [ ] No duplicate payment receipts on provider callback replay; idempotency tests passed.
- [ ] Invoice-to-payment-to-reconciliation-to-close-to-report trace available from UI and API.
- [ ] Proper authorization for price overrides, discounts, refunds, cashier transfers and adjusting journals.
- [ ] Physical cash count reconciles to expected cash, stores variance and separate approval.
- [ ] Close blocks unresolved mandatory differences; conditional exceptions clearly labeled and approved.
- [ ] Locked close snapshots are immutable; late corrections issue linked supplemental versions.
- [ ] Branch local cutoffs and cross-midnight shift tests passed; Ethiopian date is display-only unless business config says otherwise.
- [ ] GL, AR, tender/provider clearing and revenue statements independently recalculated on test fixtures.
- [ ] Qualified accountant signs off mappings, statements, receipt/tax handling and reconciliation.

## Owner reports and alerts

- [ ] Owner/authorized recipient verified through portal MFA and channel challenge.
- [ ] Telegram private chat only by default; recipient changes are step-up authorized and audited.
- [ ] Default external digest has no PHI, diagnoses, phone numbers or raw bank/card secrets.
- [ ] Each report includes facility, local business day, currency, close version, cutoff and status.
- [ ] Test provider accepted/sent/retry/dead-letter states without claiming human reads.
- [ ] Repeated worker events cannot send unintended duplicate reports or post duplicate journals.
- [ ] Corrected close creates correction notice, preserving prior report archive and signatures.
- [ ] Failed dispatch leaves close intact and surfaces delivery exception.

## Patient luxury and follow-up

- [ ] Patient journey supports booking, check-in, care plan, document access and clinician-approved follow-up.
- [ ] Proxy/guardian permissions and revocation work in E2E and negative authorization tests.
- [ ] Aftercare protocol versions are reviewed by qualified clinicians.
- [ ] Missed follow-ups and concerning responses create accountable escalation, with appropriate staffing.
- [ ] Complaint resolution tracks acknowledgement, owner, time, outcome and patient follow-up.
- [ ] Medical safety incidents are routed separately from routine service tickets.
- [ ] Localization, accessibility, privacy and low-bandwidth acceptance tests are recorded.

## Quality gates

- [ ] `dotnet build`, `dotnet test` and integration tests actually executed and logs preserved.
- [ ] Database migration and rollback rehearse against anonymized/fictional fixtures.
- [ ] Cross-tenant, privilege escalation and reporting endpoint leakage tests passed.
- [ ] Backup restored in an isolated environment and balanced ledger/source snapshot verified.
- [ ] Static analysis, dependency scanning, secrets scan and independent security review completed.
- [ ] Unrun tests clearly marked NOT RUN; unresolved issues and blockers documented.
- [ ] Production go-live authorized by clinical, finance, security, operations and legal stakeholders as applicable.
