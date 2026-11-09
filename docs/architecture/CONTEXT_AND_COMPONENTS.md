# Context and components (as implemented in Milestone A)

```mermaid
flowchart LR
  subgraph Clinic["Clinic LAN / browser"]
    R[Reception] --- UI
    C[Clinician] --- UI
    UI[React SPA (same origin)]
  end
  UI -- "HTTPS, cookie session + CSRF header" --> H[Serk.Clinic.Host<br/>ASP.NET Core minimal APIs]
  H --> ID[Identity] & PT[Patients] & SC[Scheduling] & CL[Clinical] & DO[Documents]
  ID & PT & SC & CL & DO --> PL[Platform: DbSession, Audit, Outbox, Idempotency]
  PL -- "serk_app (non-owner, RLS)" --> DB[(PostgreSQL 16)]
  DO --> OS[(Private object store)]
  H --> W[Outbox background worker]
  W --> DB
  Anon[Anyone with QR] -- "/api/v1/verify/{opaque}" --> H
```

## Sequence: sign note → final PDF

```mermaid
sequenceDiagram
  participant B as Browser (clinician)
  participant A as API
  participant DB as PostgreSQL
  participant O as Object store
  B->>A: PUT /encounters/{id}/note-draft (If-Match vN)
  A->>DB: update draft WHERE version=N (RLS ctx)
  B->>A: POST /encounters/{id}/sign (Idempotency-Key, draftVersion, allergyReviewToken)
  A->>DB: BEGIN; set_config(tenant); idempotency row; checks (attending, licence, allergy token, draft version)
  A->>DB: INSERT note_versions(snapshot, sha256); UPDATE encounter Signed; bump draft; INSERT audit; INSERT outbox; COMMIT
  B->>A: POST /documents {latest noteVersionId}
  A->>DB: freeze snapshot + template v2 (hash)
  B->>A: GET /documents/{id}/preview → watermarked PDF (same renderer)
  B->>A: POST /documents/{id}/finalize (Idempotency-Key)
  A->>A: re-check latest version; render final
  A->>O: write-once PDF
  A->>DB: status Final + pdf sha256 (unique current final); audit; COMMIT
  B->>A: GET /documents/{id}/download → verified bytes (hash check)
```

Deployment profiles, backup/restore and network sequences are **not yet implemented** (see BUILD_STATUS).
