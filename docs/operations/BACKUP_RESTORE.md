# Backup and restore — PARTIAL (two layers implemented, production backup NOT IN PLACE)

Two independent layers. Neither replaces the operator's continuous database backups (still required, see "Not done").

## Layer 1 — Organisation backup (in the app, owner-controlled)
*Who:* people with `backup.manage` (Owner by default). *Where:* **Data & backups → Backups**.
- **Create:** the owner enters a backup passphrase (≥ 12 varied characters, not their password), confirms with their own
  password (step-up; failures count towards account lockout) and receives one `.serkbak` file.
- **Contents:** every row of the organisation in the tables listed in `Platform/Backup/BackupCatalog.cs` (patients,
  appointments, encounters, signed notes, dental findings, documents, configuration and clinic profile, the whole finance
  ledger, audit trail, export/backup logs, staff and access **without password hashes**) plus stored files (logos, stamps,
  signatures, issued PDFs), and `manifest.json` with each file's SHA-256, row counts, applied migrations and ledger totals.
- **Tables deliberately excluded** (listed with reasons in `BackupCatalog.Excluded`): API replay cache, outbox queue,
  migration table, platform security telemetry, sessions (secrets), platform super administrators. Integration test
  BACKUP-001 fails if a new organisation table is in neither list.
- **Encryption:** ZIP → AES-256-GCM. Key = PBKDF2-HMAC-SHA256(passphrase, random 16-byte salt, 600 000 iterations);
  random 12-byte nonce; the header (magic, iterations, salt, nonce) is authenticated. Wrong passphrase or any changed byte
  ⇒ cannot be opened. **The server keeps neither the file nor the passphrase**; a lost passphrase means a lost backup.
- **Verify:** upload the file with its passphrase. The server decrypts in memory, checks every fingerprint and row count,
  that the backup belongs to this organisation and that the ledger balances overall and per entry. Nothing is imported.
- **Logged:** `platform.backup_log` (append-only) + audit event. Insights warn when no backup exists, when the newest is
  older than 7 days, and when it has not been verified.
- **Limit:** 200 MB uncompressed per backup (larger organisations need operator backups).

## Layer 2 — Whole-database rehearsal (operator)
`tools/backup-rehearsal.sh [db]` → `pg_dump -Fc`, restore into a **new** scratch database, then proves: identical row
counts for every table, identical migrations and checksums, ledger balanced per entry, the same number of protection
triggers and row-level-security tables, and that the audit trail still rejects changes. The scratch database is dropped.
Evidence: `docs/evidence/backups/rehearsal-*.log`.

## Restoring
1. Never restore over a live database. Create a fresh environment (database + object store).
2. Operator dump: `pg_restore -d <new_db> <file.dump>`, then run the application (migrations are checksum-verified).
3. Organisation backup: **in-app import is NOT IMPLEMENTED.** The archive is plain JSON lines per table after decryption
   and can be loaded by an operator script into a fresh database in catalogue order (identity → patients → … → finance →
   audit). Writing and rehearsing that import is open work (OPS-003b).
4. After any restore: run `tools/backup-rehearsal.sh` checks against the restored database and verify document hashes.

## Not done (blocks real-patient use)
- Continuous production backups (base backups + WAL archiving / point-in-time recovery) and off-site copies.
- Scheduled automatic organisation backups; key escrow; quarterly restore drill with sign-off.
- In-app/automated import of an organisation backup into a fresh environment (OPS-003b).
- Railway demo: the volume and database are not backed up by this project.
