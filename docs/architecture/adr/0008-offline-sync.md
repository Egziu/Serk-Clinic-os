# ADR-0008 Offline and synchronisation
- **Status:** Proposed · **Date:** 2026-11-09
- **Decision:** First deployable profile is clinic-local (single server, LAN). Hybrid sync, if approved later, uses domain events with stable IDs (outbox already carries them), idempotent consumers and operator-resolved conflicts. Last-write-wins is prohibited for signed notes, medications, identity, billing and stock. Not implemented.
