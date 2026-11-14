#!/usr/bin/env bash
# Restore rehearsal for the whole database (operator-level backup, complements the in-app encrypted organisation backup).
# 1. pg_dump the source database (custom format)  2. restore into a NEW scratch database  3. prove the copy is complete:
#    identical row counts for every table, same applied migrations, ledger balanced per entry, immutability triggers and
#    row-level security still enabled  4. drop the scratch database. Never restores over an existing database.
# Usage: tools/backup-rehearsal.sh [source_db]   (defaults to serk_e2e in the local docker container "serk-pg")
set -euo pipefail
SRC="${1:-serk_e2e}"
CONTAINER="${SERK_PG_CONTAINER:-serk-pg}"
SCRATCH="serk_restore_rehearsal_$(date -u +%Y%m%d%H%M%S)"
OUT_DIR="${SERK_BACKUP_DIR:-artifacts/backups}"
mkdir -p "$OUT_DIR"
DUMP="$OUT_DIR/$SRC-$(date -u +%Y%m%dT%H%M%SZ).dump"
psql_in() { docker exec -i "$CONTAINER" psql -U postgres -v ON_ERROR_STOP=1 -At "$@"; }

echo "rehearsal: dumping $SRC"
docker exec "$CONTAINER" pg_dump -U postgres -Fc "$SRC" > "$DUMP"
SHA=$(sha256sum "$DUMP" | cut -d' ' -f1)
echo "rehearsal: dump $(stat -c %s "$DUMP") bytes sha256=$SHA"

cleanup() { psql_in -d postgres -c "DROP DATABASE IF EXISTS $SCRATCH" >/dev/null 2>&1 || true; }
trap cleanup EXIT
psql_in -d postgres -c "CREATE DATABASE $SCRATCH" >/dev/null
docker exec -i "$CONTAINER" pg_restore -U postgres --exit-on-error -d "$SCRATCH" < "$DUMP"
echo "rehearsal: restored into $SCRATCH"

COUNTS_SQL="SELECT string_agg(format('%s.%s=%s', schemaname, relname,
  (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', schemaname, relname), false, true, '')))[1]::text), ',' ORDER BY schemaname, relname)
  FROM pg_stat_user_tables"
SRC_COUNTS=$(psql_in -d "$SRC" -c "$COUNTS_SQL")
DST_COUNTS=$(psql_in -d "$SCRATCH" -c "$COUNTS_SQL")
fail=0
if [ "$SRC_COUNTS" = "$DST_COUNTS" ]; then echo "PASS row counts identical for $(echo "$SRC_COUNTS" | tr ',' '\n' | wc -l) tables"; else echo "FAIL row counts differ"; fail=1; fi

MIG_SQL="SELECT string_agg(script_name || ':' || checksum_sha256, ',' ORDER BY script_name) FROM platform.schema_migrations"
if [ "$(psql_in -d "$SRC" -c "$MIG_SQL")" = "$(psql_in -d "$SCRATCH" -c "$MIG_SQL")" ]; then echo "PASS applied migrations and checksums identical"; else echo "FAIL migrations differ"; fail=1; fi

UNBALANCED=$(psql_in -d "$SCRATCH" -c "SELECT count(*) FROM (SELECT entry_id FROM finance.journal_lines GROUP BY entry_id HAVING sum(debit) <> sum(credit)) x")
TOTALS=$(psql_in -d "$SCRATCH" -c "SELECT coalesce(sum(debit),0) || ' / ' || coalesce(sum(credit),0) FROM finance.journal_lines")
if [ "$UNBALANCED" = "0" ]; then echo "PASS ledger balanced per entry (debits / credits: $TOTALS)"; else echo "FAIL $UNBALANCED unbalanced journal entries"; fail=1; fi

TRIGGERS=$(psql_in -d "$SCRATCH" -c "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal")
SRC_TRIGGERS=$(psql_in -d "$SRC" -c "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal")
RLS=$(psql_in -d "$SCRATCH" -c "SELECT count(*) FROM pg_class WHERE relrowsecurity AND relforcerowsecurity")
SRC_RLS=$(psql_in -d "$SRC" -c "SELECT count(*) FROM pg_class WHERE relrowsecurity AND relforcerowsecurity")
if [ "$TRIGGERS" = "$SRC_TRIGGERS" ] && [ "$RLS" = "$SRC_RLS" ]; then echo "PASS $TRIGGERS protection triggers and $RLS row-level-security tables preserved"; else echo "FAIL protections differ"; fail=1; fi

IMMUTABLE=$(psql_in -d "$SCRATCH" -c "DO \$\$ BEGIN UPDATE finance.journal_lines SET debit = debit WHERE false; UPDATE audit.audit_events SET action = action WHERE id = (SELECT id FROM audit.audit_events LIMIT 1); RAISE EXCEPTION 'not_blocked'; EXCEPTION WHEN OTHERS THEN RAISE NOTICE '%', SQLERRM; END \$\$" 2>&1 | grep -o "not_blocked\|immutable\|append-only\|cannot" | head -1 || true)
if [ "$IMMUTABLE" != "not_blocked" ] && [ -n "$IMMUTABLE" ]; then echo "PASS audit trail still rejects changes in the restored copy"; else echo "FAIL audit immutability not enforced after restore"; fail=1; fi

if [ "$fail" = "0" ]; then echo "rehearsal: ALL CHECKS PASSED (dump kept at $DUMP)"; else echo "rehearsal: FAILED"; exit 1; fi
