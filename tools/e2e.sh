#!/usr/bin/env bash
# Builds the SPA into the API host, provisions a FRESH disposable database "serk_e2e" with synthetic seed data, starts the
# host in Development mode on :5080 and runs the Playwright suite. Requires a local PostgreSQL reachable with
# SERK_PG_ADMIN (owner/superuser, dev only). Never point this at a database holding real data.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ADMIN="${SERK_PG_ADMIN:-Host=localhost;Port=5432;Username=postgres;Password=devonly-postgres}"
PSQL_URL="${SERK_PSQL_URL:-postgresql://postgres:devonly-postgres@localhost:5432/postgres}"
DB=serk_e2e

psql "$PSQL_URL" -v ON_ERROR_STOP=1 -q -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" -c "CREATE DATABASE $DB"
(cd "$ROOT/src/web" && npm run build --silent)
dotnet build "$ROOT/src/backend/Serk.Clinic.Host" -c Release --nologo -v q

export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__App="Host=localhost;Port=5432;Database=$DB;Username=serk_app;Password=dev-only-app-password"
export ConnectionStrings__Migrations="$ADMIN;Database=$DB"
export ObjectStore__RootPath="$ROOT/.local/e2e-objects"
export Diagnostics__LogExceptionDetails=false
# The suite signs in ~15 times in under a minute; the production default (10/min per client) is tested separately.
export RateLimits__LoginPerMinute=60
rm -rf "$ROOT/.local/e2e-objects"
mkdir -p "$ROOT/artifacts"
if curl -s -o /dev/null http://localhost:5080/health/live; then
  echo "Port 5080 is already serving something; stop it first so the tests do not hit a stale host." >&2
  exit 2
fi
(cd "$ROOT/src/backend/Serk.Clinic.Host" && exec dotnet bin/Release/net10.0/Serk.Clinic.Host.dll > "$ROOT/artifacts/e2e-host.log" 2>&1) &
HOST_PID=$!  # exec => this is the dotnet process itself, so the trap really stops it
trap 'kill $HOST_PID 2>/dev/null || true' EXIT
for i in $(seq 1 60); do curl -sf http://localhost:5080/health/ready >/dev/null && break; sleep 1; done
curl -sf http://localhost:5080/health/ready >/dev/null || { echo "host did not become ready"; tail -50 "$ROOT/artifacts/e2e-host.log"; exit 1; }
cd "$ROOT/src/web" && npx playwright test --trace off "$@"
