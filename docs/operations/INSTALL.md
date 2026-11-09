# Install — local development (synthetic data only)

Prerequisites: .NET SDK 10.0.1xx (`global.json`), Node 22 (`src/web/.nvmrc`), PostgreSQL 16 (or Docker).

```bash
docker compose -f docker-compose.dev.yml up -d
```

```bash
cd src/web && npm ci && npm run build && cd ../..
```

```bash
cd src/backend/Serk.Clinic.Host && ASPNETCORE_ENVIRONMENT=Development dotnet run
```

Development mode migrates the `serk_dev` database (create it first: `createdb -h localhost -U postgres serk_dev`), provisions
the `serk_app` runtime login, seeds the **synthetic** demo tenants and serves the UI at http://localhost:5080.
Demo accounts (password `Synthetic-Demo-2026!`, development only): `reception.a`, `dr.abebe`, `dr.hana` (two facilities),
`nurse.a`, `admin.a`, `dentist.a`, `dr.expired`, tenant B `reception.b`, `dr.b`, platform `support.serk` (no access).

Configuration (environment variables override `appsettings.json`; values are never committed for non-dev):
`ConnectionStrings__App` (runtime non-owner role), `ConnectionStrings__Migrations` (schema owner, deploy-time),
`ObjectStore__RootPath` (private directory, mode 0700), `Documents__PublicBaseUrl` (https, required outside Development).

Tests: `dotnet test` (needs Docker or `SERK_TEST_PG_ADMIN`), `npm test` in `src/web`, E2E: `tools/e2e.sh`.

**There is no production deployment profile yet.** Do not expose this build to the internet or use real patient data.
