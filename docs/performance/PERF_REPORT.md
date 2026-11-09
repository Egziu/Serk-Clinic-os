# Performance report (measured, not estimated)

**Run:** `perf-20261109T174206Z.json` · 2026-11-09 · `tools/perf.sh 300 8`
**Environment:** shared cloud container, 4 vCPU (Intel Xeon @ 2.30 GHz), 15 GB RAM; PostgreSQL 16.6 in Docker on the same host; API = Release build,
Kestrel, Development profile with logging at Warning; client = Node 22 `fetch` on the same host (loopback — **no network latency**).
**Data (synthetic):** 20,000 patients, 35,000 appointments over 365 days, 25,000 signed encounters, one facility.
**Method:** 20 warm-up requests, then 300 sequential requests per endpoint (p50/p95/p99), then 8 concurrent clients × 38 requests.
"Server" = the API's own `Server-Timing: app;dur=` (time inside the ASP.NET pipeline incl. all DB round-trips, audit writes and commits).

| Endpoint | Server p50 | Server p95 | Client p50 | Client p95 | 8 concurrent p95 | Throughput (8 clients) |
|---|---|---|---|---|---|---|
| Health (liveness, no DB) | — | — | 3.8 ms | 5.0 ms | 18.4 ms | 796 req/s |
| Patient search, name prefix | 9.8 ms | 12.9 ms | 10.5 ms | 14.0 ms | 38.7 ms | 309 req/s |
| Patient search, MRN exact | 8.8 ms | 12.2 ms | 9.5 ms | 13.2 ms | 32.0 ms | 353 req/s |
| Patient record (+ audit write) | 4.8 ms | 7.0 ms | 5.5 ms | 7.9 ms | 18.8 ms | 598 req/s |
| Day appointment list | 4.9 ms | 8.1 ms | 5.5 ms | 9.1 ms | 32.0 ms | 378 req/s |
| Encounter open (+ audit write) | 6.5 ms | 9.8 ms | 7.1 ms | 10.5 ms | 29.8 ms | 357 req/s |
| Analytics overview, 30 days | 26.0 ms | 34.6 ms | 26.8 ms | 36.0 ms | 79.4 ms | 141 req/s |
| Analytics overview, 365 days | 122.9 ms | 149.5 ms | 124.5 ms | 150.9 ms | 376.4 ms | 29 req/s |

## Against the architecture targets (hypotheses in `docs/reference/architecture/01_SYSTEM_ARCHITECTURE.md`)

| Target | Measured here | Status |
|---|---|---|
| Patient search p95 < 500 ms | 12.9–14.0 ms | **Met in this environment** (clinic LAN/WAN latency not included) |
| Encounter open p95 < 1 s | 10.5 ms (API only; browser render not included) | **Met in this environment** for the API |
| RPO ≤ 15 min / RTO ≤ 4 h | — | **NOT MEASURED** (no backup/restore implementation yet) |

## Honest notes
- "Microsecond" responses are not physically achievable for authenticated requests that read PostgreSQL and write an audit row
  in a committed transaction; single-digit **milliseconds** is the realistic floor and is what is measured. The Server-Timing
  header reports server time with microsecond resolution so real deployments can be monitored.
- Run-to-run variance on this shared container is roughly ±30% (a repeat run moved the health check from 3.8 to 5.3 ms).
- Optimisations tried and **reverted because they produced no measurable benefit** at this volume: fixed-offset epoch bucketing instead
  of `AT TIME ZONE` (query plans showed time-zone conversion was not the bottleneck), and a `pg_trgm` GIN index for name search
  (the planner correctly preferred a 4 ms sequential scan over 20,000 rows). Revisit the trigram index above ~200,000 patients.
- Kept: batched patient/practitioner lookups (removes N+1 in the day list and worklist), indexes for analytics range scans.
- The 365-day dashboard (~0.12 s) scans and sorts a year of rows for percentiles; a nightly materialised daily roll-up is the
  next step if facilities exceed ~250,000 appointments/year. Not built (no evidence of need yet).
- Not measured: browser rendering time, clinic LAN latency, low-end reception PCs, PostgreSQL on separate hardware, multi-facility load.
