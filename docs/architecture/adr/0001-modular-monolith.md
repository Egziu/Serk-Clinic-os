# ADR-0001 Modular monolith on .NET 10 / ASP.NET Core
- **Status:** Accepted (engineering) · **Date:** 2026-11-09 · **Owner:** SERK engineering · **Revisit:** if a module needs independent scaling or release cadence with evidence.
- **Context:** Small clinics, intermittent connectivity, small team; architecture kit mandates a modular monolith.
- **Decision:** One deployable host (`Serk.Clinic.Host`) composed of module projects (Identity, Patients, Scheduling, Clinical, Documents) plus `SharedKernel` and `Platform`. Each module contains `Domain`, `Application`, `Infrastructure`, `Contracts`, `Api` folders. Other modules may use only `*.Contracts`.
- **Enforcement:** `tests/Serk.Clinic.ArchitectureTests/ModuleBoundaryTests` (IL-level, NetArchTest) — mutation-checked on 2026-11-09.
- **Consequences:** Simple deployment and transactions; boundaries depend on tests not process isolation. Cross-module FKs exist in SQL for referential integrity (see ADR-0003) — module code never queries another module's tables.
