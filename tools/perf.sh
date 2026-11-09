#!/usr/bin/env bash
# Reproducible latency measurement: fresh DB "serk_perf" + synthetic volume, Release host on :5080, node measure script.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PSQL_URL="${SERK_PSQL_URL:-postgresql://postgres:devonly-postgres@localhost:5432/postgres}"
ADMIN="${SERK_PG_ADMIN:-Host=localhost;Port=5432;Username=postgres;Password=devonly-postgres}"
DB=serk_perf
psql "$PSQL_URL" -v ON_ERROR_STOP=1 -q -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" -c "CREATE DATABASE $DB"
dotnet build "$ROOT/src/backend/Serk.Clinic.Host" -c Release --nologo -v q >&2
export Logging__LogLevel__Default=Warning ASPNETCORE_ENVIRONMENT=Development Diagnostics__LogExceptionDetails=false Outbox__Enabled=false Scheduling__NoShow__Enabled=false
export ConnectionStrings__App="Host=localhost;Port=5432;Database=$DB;Username=serk_app;Password=dev-only-app-password;Maximum Pool Size=50"
export ConnectionStrings__Migrations="$ADMIN;Database=$DB" ObjectStore__RootPath="$ROOT/.local/perf-objects" RateLimits__LoginPerMinute=1000
if curl -s -o /dev/null http://localhost:5080/health/live; then echo "port 5080 busy" >&2; exit 2; fi
mkdir -p "$ROOT/artifacts"
(cd "$ROOT/src/backend/Serk.Clinic.Host" && exec dotnet bin/Release/net10.0/Serk.Clinic.Host.dll > "$ROOT/artifacts/perf-host.log" 2>&1) &
HOST_PID=$!
trap 'kill $HOST_PID 2>/dev/null || true' EXIT
for i in $(seq 1 60); do curl -sf http://localhost:5080/health/ready >/dev/null && break; sleep 1; done
psql "${PSQL_URL%/postgres}/$DB" -v ON_ERROR_STOP=1 -q -f "$ROOT/tools/perf/seed-volume.sql"
node "$ROOT/tools/perf/measure.mjs" http://localhost:5080 "${1:-300}" "${2:-8}"
