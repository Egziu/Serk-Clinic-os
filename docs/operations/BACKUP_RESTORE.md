# Backup and restore — **NOT IMPLEMENTED / NOT RUN**

Required design (ADR-0009): PostgreSQL base backups + WAL archiving (PITR), encrypted; object store backup with a manifest
linking `document_versions.pdf_object_key` + `pdf_sha256`; keys escrowed separately; quarterly restore drill into an
isolated environment verifying record counts, note hashes (`VerifyIntegrity`) and PDF hashes. OPS-003 is a pilot blocker.
