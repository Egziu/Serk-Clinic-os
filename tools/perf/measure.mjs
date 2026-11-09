// Latency measurement against a running host. Reports client round-trip and server Server-Timing (app;dur) percentiles.
// Usage: node measure.mjs <baseUrl> <iterations> <concurrency>
const [base = "http://localhost:5080", iterArg = "300", concArg = "8"] = process.argv.slice(2);
const iterations = Number(iterArg);
const concurrency = Number(concArg);
const password = "Synthetic-Demo-2026!";

async function login(username) {
  const r = await fetch(`${base}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username, password }) });
  if (!r.ok) throw new Error(`login ${username} ${r.status}`);
  const cookie = r.headers.get("set-cookie").split(";")[0];
  return { cookie };
}

function pct(values, p) {
  const s = [...values].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))];
}

async function timed(session, path) {
  const t0 = performance.now();
  const r = await fetch(base + path, { headers: { Cookie: session.cookie } });
  await r.arrayBuffer();
  const client = performance.now() - t0;
  if (!r.ok) throw new Error(`${path} -> ${r.status}`);
  const st = (r.headers.get("server-timing") || "").match(/app;dur=([\d.]+)/);
  return { client, server: st ? Number(st[1]) : NaN };
}

async function run(name, session, path) {
  for (let i = 0; i < 20; i++) await timed(session, path); // warm-up (JIT, pools, caches)
  const seq = [];
  for (let i = 0; i < iterations; i++) seq.push(await timed(session, path));
  const conc = [];
  const t0 = performance.now();
  await Promise.all(Array.from({ length: concurrency }, async () => {
    for (let i = 0; i < Math.ceil(iterations / concurrency); i++) conc.push(await timed(session, path));
  }));
  const wall = performance.now() - t0;
  const f = (v) => Number(v.toFixed(3));
  return {
    name, path, n: seq.length,
    serverMs: { p50: f(pct(seq.map((x) => x.server), 50)), p95: f(pct(seq.map((x) => x.server), 95)), p99: f(pct(seq.map((x) => x.server), 99)) },
    clientMs: { p50: f(pct(seq.map((x) => x.client), 50)), p95: f(pct(seq.map((x) => x.client), 95)), p99: f(pct(seq.map((x) => x.client), 99)) },
    concurrent: { clients: concurrency, n: conc.length, p95ClientMs: f(pct(conc.map((x) => x.client), 95)), throughputRps: f(conc.length / (wall / 1000)) },
  };
}

const reception = await login("reception.a");
const doctor = await login("dr.abebe");
const owner = await login("owner.a");
const enc = (await (await fetch(`${base}/api/v1/encounters?day=${new Date(Date.now() - 86400000 * 30).toISOString().slice(0, 10)}`, { headers: { Cookie: doctor.cookie } })).json())[0];
const search = await (await fetch(`${base}/api/v1/patients?query=Hiwot`, { headers: { Cookie: reception.cookie } })).json();
const results = [];
results.push(await run("Health (liveness)", reception, "/health/live"));
results.push(await run("Patient search (name prefix, 20k patients)", reception, "/api/v1/patients?query=Hiwot%20vol12"));
results.push(await run("Patient search (MRN exact)", reception, "/api/v1/patients?query=MRN-V012345"));
results.push(await run("Patient record (demographics + audit write)", reception, `/api/v1/patients/${search[0].id}`));
results.push(await run("Day list (appointments)", reception, "/api/v1/appointments"));
if (enc) results.push(await run("Encounter open (banner + audit write)", doctor, `/api/v1/encounters/${enc.id}`));
results.push(await run("Analytics overview 30 days", owner, "/api/v1/analytics/overview"));
const year = new Date(Date.now() - 365 * 86400000).toISOString().slice(0, 10);
results.push(await run("Analytics overview 365 days (35k appointments)", owner, `/api/v1/analytics/overview?from=${year}`));
console.log(JSON.stringify({ measuredAtUtc: new Date().toISOString(), base, iterations, concurrency, results }, null, 2));
