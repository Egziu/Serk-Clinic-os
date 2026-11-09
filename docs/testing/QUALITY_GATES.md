# SERK Clinic OS — quality / release gates

| Gate | Evidence required | Failure consequence |
|---|---|---|
| Source/tool trust | Audited plugin, MCP permissions, no exposed patient data | Block external tool use |
| Build/lint | Real command logs and exit code | Block candidate |
| Core domain tests | Recorded pass/fail/skip and risk coverage | Block affected modules |
| Authorization | Two-tenant BOLA, RLS, role matrix, support denial | Block any clinical release |
| Clinical integrity | Note immutability, amendment, orders, patient identity | Block clinical release |
| Finance and stock | Idempotency, concurrent payments, transaction safety | Block finance/pharmacy release |
| Documentation | Preview/final snapshot match and guarded downloads | Block document release |
| Database upgrade | Migration rehearsal and verification of preserved records | Block upgrade |
| Recovery | Isolated backup/object/key restoration and measured times | Block pilot |
| Security | ASVS matrix, scanning and externally scoped pentest | Block broad rollout |
| Performance | Reproducible workload and measured p95s | Do not claim target achieved |
| Clinical validation | Named licensed reviewer signs each relevant specialty workflow | Block real patient clinical workflow |
| Privacy/legal | Jurisdiction-specific owner/counsel approvals | Block real patient deployment |
| Pilot UAT | Facility users and safety lead approve controlled workflows | Block production rollout |

A passing unit test is **not** equivalent to clinical or legal approval. Missing tests must show NOT RUN, not PASS.
