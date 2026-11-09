#!/usr/bin/env bash
# Runs the same gates as CI locally (run before every push). Requires Docker or SERK_TEST_PG_ADMIN for integration tests.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
dotnet list package --vulnerable --include-transitive | tee /tmp/serk-vuln.txt
! grep -q "has the following vulnerable packages" /tmp/serk-vuln.txt
dotnet test --no-build -c Release
(cd src/web && npm ci --no-fund && npm audit --audit-level=moderate && npm run lint && npm run typecheck && npm test && npm run build)
echo "verify: all gates passed"
