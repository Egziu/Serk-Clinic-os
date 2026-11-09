# Data flow and classification

| Class | Examples | Where stored | Allowed in logs/telemetry/AI tools | Notes |
|---|---|---|---|---|
| **C3 — Clinical (highest)** | note content, allergies, signed snapshots, PDFs | `clinical.*`, `patients.allergies`, `documents.document_versions.snapshot`, object store | **Never** | RLS + app ABAC; audit on read |
| **C2 — Patient identity** | names, DOB, phone, national ID, MRN | `patients.patients`, appointment DTOs | **Never** | minimal projection in search/day lists |
| **C1 — Operational identifiers** | GUIDs, correlation IDs, status codes, hashes | audit metadata, outbox payloads, logs | Yes (no PHI) | audit metadata limited to IDs/codes/hashes |
| **C1 — Staff identity** | username, display name, roles | `identity.*` | Usernames hashed in security events | |
| **Secrets** | password hashes, session token hashes, CSRF tokens, DB passwords | `identity.*`, deployment config | **Never** | no secrets in repo except clearly-labelled dev-only local defaults |

## Flows (Milestone A)
1. Browser → `/api/v1/*` (cookie session) → Application service → DbSession (one transaction, RLS context) → PostgreSQL.
2. Sign → `clinical.note_versions` (immutable) + `audit.audit_events` + `platform.outbox_messages` in the same transaction → OutboxProcessor (IDs only).
3. Document finalize → render PDF in process (no external service) → object store (write-once, hashed) → `documents.document_versions`.
4. Anonymous → `/api/v1/verify/{ref}` → SECURITY DEFINER function (owner `serk_document_verifier`) → facility name, template, status, hash only.

## AI / tooling boundary
Development used synthetic data only. No PHI was sent to Claude, plugins, MCP servers, CI, telemetry or error reporting.
Production must keep these paths closed; any AI-assisted feature requires the controls in MASTER_BUILD_PROMPT §4 G.

## Legal
Ethiopia Personal Data Protection Proclamation No. 1321/2024 applies to real deployments. **No compliance is claimed.**
Controller/processor roles, lawful basis, retention and cross-border review require counsel (PENDING).
