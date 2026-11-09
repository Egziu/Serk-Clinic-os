# ADR-0010 Patient identity and duplicate handling
- **Status:** Accepted (rules PENDING CLINICIAN / medical-records review) · **Date:** 2026-11-09
- **Decision:** Registration runs deterministic duplicate scoring (`DuplicateMatcher`): name alone never matches; name+DOB or phone+given name = Probable; national ID = Definite (blocks creation). Probable matches require an audited override reason; candidate disclosure is audited even without creation. MRNs are tenant-scoped, allocated atomically. **Merge is not implemented**; a future merge must preserve both histories and be reversible/audited.
