# Hosted synthetic demo on Railway

**Purpose:** internet-reachable test deployment for the owner's acceptance testing. **Synthetic data only.**
It is not production, not a pilot, and must never receive real patient data (no MFA, no backups, demo accounts).

## Shape
- Railway project `serk-clinic-os-demo` (separate from other SERK projects): PostgreSQL 16 service + `web` service built
  from this repository's `Dockerfile` (SPA + API in one ASP.NET Core host), a volume at `/var/lib/serk/objects` for PDFs
  and branding images, and a Railway-generated HTTPS domain. Health check: `/health/ready`.
- The container starts as root only to take ownership of the mounted volume, then runs as the unprivileged `app` user.

## Environment `Demo`
`ASPNETCORE_ENVIRONMENT=Demo` is the only non-development environment allowed to migrate with app-login provisioning and to
seed synthetic users (`SerkHost.IsSyntheticEnvironment`). It refuses the demo password published in the repository and any
password under 16 characters. Exception details are not logged; OpenAPI is not exposed.

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Demo` |
| `ConnectionStrings__Migrations` | owner connection built from the Postgres service's `PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD` references |
| `ConnectionStrings__App` | same host/database, user `serk_app_demo`, random password |
| `Database__MigrateOnStartup` | `true` |
| `Database__EnsureAppLogin__Username` / `__Password` | `serk_app_demo` / same random password (NOSUPERUSER, NOBYPASSRLS) |
| `Seed__Demo` / `Seed__DemoPassword` | `true` / random ≥ 16 chars (shared with the owner out of band) |
| `Documents__PublicBaseUrl` | the Railway HTTPS domain (QR verification links) |
| `Proxy__TrustForwardedHeaders` | `true` (exactly one platform proxy; per-client rate limits) |

Secrets live only in Railway variables, never in git.

## Current deployment (2026-11-09)
- URL: https://web-demo-cd4b.up.railway.app · Railway project `serk-clinic-os-demo`, environment `demo`, region iad.
- Deploys automatically on push to `claude/modest-pascal-x3qzg9`.
- Verified: E2E 9/9 against the live site (see TEST_EVIDENCE_INDEX).
- Data protection keys are ephemeral (in-memory); acceptable here because sessions use the app's own revocable tokens.

## Super administrator (demo)
`super.admin` (synthetic, same demo password) is the seeded system super administrator. It sees the **System admin** console
only — no patient data. Create a second super administrator from the console so one remains if the first is locked out.

## Verify after deploy
1. `GET /health/ready` → 200.  2. Run the E2E suite against the deployment (re-runnable on an existing database; wait 1 minute between runs because of the
   login rate limit — the suite signs in ~15 times, so run the spec files in two batches a minute apart). From a proxied network add `SERK_E2E_PROXY=<proxy URL>` (and `NODE_EXTRA_CA_CERTS` if it intercepts TLS):
   `SERK_E2E_BASE_URL=https://<domain> SERK_E2E_PASSWORD=<demo password> npx playwright test --trace off` (from `src/web`).
3. Record the result in `docs/testing/TEST_EVIDENCE_INDEX.md`.

## Tear down
Delete the Railway project (removes database and volume). Costs are usage-based on the owner's Railway plan.
