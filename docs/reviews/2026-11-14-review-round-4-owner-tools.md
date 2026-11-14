# Review round 4 — owner setup, exports, backups, insights (2026-11-14)

Independent AI security review (read-only, static reading; NOT a substitute for a human security review). Status after fixes:

| ID | Severity | Finding | Status | Evidence |
|---|---|---|---|---|
| H1 | High | A backup gives the non-clinical owner every clinical record and signature image, with no purpose | **Accepted with controls**: capability re-labelled *Clinical*; purpose (10–300 chars) now required and recorded in backup log + audit; role text says backups contain clinical records; revocable per person; decision recorded in ADR-0015. **Owner/legal decision still advised.** | BACKUP-002, AccessPolicyTests |
| M1 | Medium | Verify vulnerable to zip bombs / duplicate references / numeric overflow (500) | Fixed: streamed decompression budget (64 MB), duplicate references rejected, entry-count cap, overflow handled | BackupArchiveTests (bomb, duplicate) |
| M2 | Medium | Global 1 MB body limit made verification fail for real backups | Fixed: single documented exemption (70 MB) for `POST /api/v1/backups/verify`; every other route still 1 MB | BACKUP-002 (3 MB verify = 200, 3 MB logo = 413) |
| M3 | Medium | Stored files not counted against the backup size cap | Fixed: cap checked after every file | code |
| M4 | Medium | "data-heavy" rate limit was effectively per IP (limiter runs before authentication) | Fixed: partition by hashed session cookie, IP only when no session | code (not separately tested) |
| M5 | Medium | Step-up failure counter could lose updates under parallel guesses | Fixed: atomic `UPDATE … RETURNING`; lock and session revocation by set-based updates | EXPORT-003, BACKUP-002 (wrong-password paths); concurrent test NOT written |
| M6 | Medium | Patient register export needs no demographics-read capability | Accepted by design (owner as data controller; purpose + password + log); documented in ADR-0015 | EXPORT-003 |
| L1 | Low | Backup contained recipient verification challenge hashes | Fixed: column projection excludes them | BACKUP-002 |
| L2 | Low | Audit-trail export carries patient ids/reasons without patient-data controls | Fixed: dataset marked as patient data (purpose + password) | code |
| L3 | Low | Profile rows could reference another tenant's facility; some views rely on a tenant filter | Partly fixed: composite FK (facility, tenant); views keep explicit `platform.current_tenant()` filter | migration 0013 |
| L4 | Low | Facility admins could see other facilities' export history | Fixed: export history and log export filtered to the facility | code |
| L5 | Low | Bidirectional/invisible characters could spoof printed letterheads | Fixed: Unicode format characters rejected | ClinicProfileRulesTests |
| — | — | Crafted header could force 5 M PBKDF2 iterations | Bounded to 2 M | code |

Also found during verification (not by the reviewer): PDF rendering was not thread-safe — under parallel load preview and
final occasionally positioned glyphs differently (pre-existing; 3/30 failing runs on the previous commit). Fixed by
serialising rendering process-wide; 0/40 failures afterwards.

Open: human security review, MFA (IDENT-001), in-app restore (OPS-003b), continuous production backups.
