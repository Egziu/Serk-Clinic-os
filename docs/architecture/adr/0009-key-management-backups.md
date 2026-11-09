# ADR-0009 Secrets, keys and backups
- **Status:** Proposed — **blocking for pilot** · **Date:** 2026-11-09
- **Decision:** No secrets in repository except clearly labelled development-only local defaults (`appsettings.Development.json`, `docker-compose.dev.yml`). Production secrets from environment/secret store. Backups: PostgreSQL base backups + WAL archiving, encrypted, with object-store backup and separately escrowed keys; restore rehearsals recorded (OPS-003 NOT RUN).
