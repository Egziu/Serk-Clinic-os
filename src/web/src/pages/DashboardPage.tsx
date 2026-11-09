import { useEffect, useState, type ReactNode } from "react";
import { api } from "../api/client";
import { BarList, formatNum, Heatmap, LineChart } from "../components/charts";
import { ErrorAlert } from "../components/common";
import { Icon } from "../components/icons";
import { localIsoDate } from "../lib/format";

/** Demographic count; null + suppressed when 1-4 (small-cell suppression on the server). */
interface Bucket { label: string; count: number | null; suppressed: boolean }
const bucketText = (b: Bucket) => (b.suppressed ? "fewer than 5" : String(b.count ?? 0));
const bucketItem = (b: Bucket) => ({ label: b.label, value: b.count ?? 0, display: b.suppressed ? "<5" : undefined, note: b.suppressed ? "Suppressed: fewer than 5 (privacy)" : undefined });

interface Rate { numerator: number; denominator: number; rate: number | null; low95: number | null; high95: number | null }
interface Overview {
  from: string; to: string; timeZone: string; appointments: number; previousPeriodAppointments: number; appointmentsChangePercent: number | null;
  attendanceRate: Rate; noShowRate: Rate; cancelled: number; medianBookingLeadDays: number | null;
  timeToSignedNote: { n: number; medianMinutes: number | null; p90Minutes: number | null; meanMinutes: number | null };
  newPatients: number; newPatientsBySex: Bucket[]; newPatientsByAgeBand: Bucket[];
  documentsFinalized: number; documentsAutoPrepared: number;
  appointmentTrend: { slopePerDay: number; rSquared: number; n: number } | null;
  daily: { date: string; ethiopianDate: string; appointments: number; attended: number; noShow: number; cancelled: number; movingAverage7: number }[];
  arrivalHeatmap: { isoWeekday: number; hour: number; count: number }[];
  clinicians: { practitionerId: string; name: string; encounters: number; signed: number; medianMinutesToSign: number | null }[];
  computeMilliseconds: number;
}

const pct = (v: number | null) => (v === null ? "—" : `${(v * 100).toFixed(1)}%`);

function Kpi({ label, value, sub }: { label: string; value: string; sub?: ReactNode }) {
  return (
    <div className="kpi">
      <div className="kpi-label">{label}</div>
      <div className="kpi-value">{value}</div>
      {sub && <div className="kpi-sub">{sub}</div>}
    </div>
  );
}

function ChartPanel({ title, description, legend, chart, table }: { title: string; description?: string; legend?: ReactNode; chart: ReactNode; table: ReactNode }) {
  const [showTable, setShowTable] = useState(false);
  const id = title.toLowerCase().replace(/[^a-z]+/g, "-");
  return (
    <section className="panel" aria-labelledby={id}>
      <div className="panel-header">
        <h2 id={id}>{title}</h2>
        <button className="btn toggle-table" aria-pressed={showTable} onClick={() => setShowTable((s) => !s)}>Table view</button>
      </div>
      {description && <p className="status-line">{description}</p>}
      {!showTable && legend}
      {showTable ? table : chart}
    </section>
  );
}

export function DashboardPage() {
  const today = localIsoDate(new Date());
  const [range, setRange] = useState({ from: shift(today, -29), to: today });
  const [data, setData] = useState<Overview | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [clientMs, setClientMs] = useState<number | null>(null);

  useEffect(() => {
    const t0 = performance.now();
    api.get<Overview>(`/api/v1/analytics/overview?from=${range.from}&to=${range.to}`)
      .then((r) => { setData(r.data); setClientMs(performance.now() - t0); setError(null); })
      .catch(setError);
  }, [range]);

  const preset = (days: number) => setRange({ from: shift(today, -(days - 1)), to: today });
  const t = data?.timeToSignedNote;

  return (
    <>
      <div className="page-header">
        <div>
          <h1>Clinic performance</h1>
          <p>Aggregated, de-identified operational statistics for this facility. No patient-level data is shown.</p>
        </div>
        <div className="btn-row" role="group" aria-label="Date range">
          {[7, 30, 90, 365].map((d) => <button key={d} className="btn" aria-pressed={range.to === today && range.from === shift(today, -(d - 1))} onClick={() => preset(d)}>{d === 365 ? "12 months" : `${d} days`}</button>)}
          <label className="inline-label" htmlFor="dash-from">From (Gregorian)</label>
          <input id="dash-from" type="date" value={range.from} max={range.to} onChange={(e) => e.target.value && setRange((r) => ({ ...r, from: e.target.value }))} />
          <label className="inline-label" htmlFor="dash-to">To (Gregorian)</label>
          <input id="dash-to" type="date" value={range.to} min={range.from} onChange={(e) => e.target.value && setRange((r) => ({ ...r, to: e.target.value }))} />
          <a className="btn" href={`/api/v1/analytics/daily.csv?from=${range.from}&to=${range.to}`}><Icon name="download" /> CSV</a>
        </div>
      </div>
      <ErrorAlert error={error} />
      {data && (
        <>
          <p className="status-line">Period {range.from} to {range.to} (Gregorian){data.daily.length > 0 && <> · {data.daily[0]!.ethiopianDate} to {data.daily[data.daily.length - 1]!.ethiopianDate}</>}</p>
          <div className="kpi-grid" role="group" aria-label="Key indicators">
            <Kpi label="Appointments" value={formatNum(data.appointments)}
              sub={data.appointmentsChangePercent === null ? "No previous-period baseline" : `${data.appointmentsChangePercent >= 0 ? "Up" : "Down"} ${Math.abs(data.appointmentsChangePercent).toFixed(1)}% vs previous ${data.daily.length} days`} />
            <Kpi label="Attendance rate" value={pct(data.attendanceRate.rate)} sub={data.attendanceRate.low95 === null ? "n = 0" : `95% CI ${pct(data.attendanceRate.low95)}–${pct(data.attendanceRate.high95)} · n = ${data.attendanceRate.denominator}`} />
            <Kpi label="No-show rate" value={pct(data.noShowRate.rate)} sub={data.noShowRate.low95 === null ? "n = 0" : `95% CI ${pct(data.noShowRate.low95)}–${pct(data.noShowRate.high95)} (Wilson)`} />
            <Kpi label="Time to signed note" value={t?.medianMinutes == null ? "—" : `${formatNum(t.medianMinutes)} min`} sub={t?.p90Minutes == null ? "median · no signed notes" : `median · P90 ${formatNum(t.p90Minutes)} min · n = ${t.n}`} />
            <Kpi label="New patients" value={formatNum(data.newPatients)} sub={`Booking lead time median ${data.medianBookingLeadDays === null ? "—" : formatNum(data.medianBookingLeadDays) + " days"}`} />
            <Kpi label="Documents finalized" value={formatNum(data.documentsFinalized)} sub={`${data.documentsAutoPrepared} drafts prepared automatically`} />
          </div>

          <ChartPanel
            title="Daily appointments"
            description={data.appointmentTrend ? `Linear trend ${data.appointmentTrend.slopePerDay >= 0 ? "+" : ""}${data.appointmentTrend.slopePerDay.toFixed(2)} appointments/day (R² ${data.appointmentTrend.rSquared.toFixed(2)}, n = ${data.appointmentTrend.n} days). Cancelled: ${data.cancelled}.` : undefined}
            legend={<div className="legend"><span><span className="swatch s1" />Appointments</span><span><span className="swatch s2" />7-day moving average</span></div>}
            chart={<LineChart title="Daily appointments and 7-day moving average" seriesNames={["Appointments", "7-day average"]}
              points={data.daily.map((d) => ({ label: d.date, sub: d.ethiopianDate, values: [d.appointments, d.movingAverage7] }))} />}
            table={
              <table><thead><tr><th>Date</th><th>E.C.</th><th>Appointments</th><th>Attended</th><th>No-show</th><th>Cancelled</th><th>7-day avg</th></tr></thead>
                <tbody>{data.daily.map((d) => <tr key={d.date}><td>{d.date}</td><td>{d.ethiopianDate}</td><td>{d.appointments}</td><td>{d.attended}</td><td>{d.noShow}</td><td>{d.cancelled}</td><td>{d.movingAverage7}</td></tr>)}</tbody></table>}
          />

          <div className="grid-2">
            <ChartPanel title="Arrival heatmap" description={`Check-ins by weekday and hour (${data.timeZone}). Stronger colour = more arrivals (see legend).`}
              chart={<Heatmap title="Arrivals by weekday and hour" cells={data.arrivalHeatmap} />}
              table={<table><thead><tr><th>Weekday</th><th>Hour</th><th>Arrivals</th></tr></thead><tbody>{data.arrivalHeatmap.map((c) => <tr key={`${c.isoWeekday}-${c.hour}`}><td>{["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][c.isoWeekday - 1]}</td><td>{c.hour}:00</td><td>{c.count}</td></tr>)}</tbody></table>} />
            <ChartPanel title="Clinician workload" description="Encounters started in the period; median minutes from encounter start to signature."
              chart={<BarList title="Encounters per clinician" items={data.clinicians.map((c) => ({ label: c.name, value: c.encounters, note: `Signed ${c.signed} · median ${c.medianMinutesToSign ?? "—"} min to sign` }))} />}
              table={<table><thead><tr><th>Clinician</th><th>Encounters</th><th>Signed</th><th>Median min to sign</th></tr></thead><tbody>{data.clinicians.map((c) => <tr key={c.practitionerId}><td>{c.name}</td><td>{c.encounters}</td><td>{c.signed}</td><td>{c.medianMinutesToSign ?? "—"}</td></tr>)}</tbody></table>} />
          </div>
          <div className="grid-2">
            <ChartPanel title="New patients by age band" description="Counts of 1–4 are shown as “<5” to protect privacy." chart={<BarList title="New patients by age band" items={data.newPatientsByAgeBand.map(bucketItem)} />}
              table={<table><thead><tr><th>Age band</th><th>Patients</th></tr></thead><tbody>{data.newPatientsByAgeBand.map((b) => <tr key={b.label}><td>{b.label}</td><td>{bucketText(b)}</td></tr>)}</tbody></table>} />
            <ChartPanel title="New patients by sex" description="Counts of 1–4 are shown as “<5” to protect privacy." chart={<BarList title="New patients by sex" items={data.newPatientsBySex.map(bucketItem)} />}
              table={<table><thead><tr><th>Sex</th><th>Patients</th></tr></thead><tbody>{data.newPatientsBySex.map((b) => <tr key={b.label}><td>{b.label}</td><td>{bucketText(b)}</td></tr>)}</tbody></table>} />
          </div>
          <p className="status-line" role="note">
            Computed in {data.computeMilliseconds.toFixed(1)} ms on the server{clientMs !== null && `, ${clientMs.toFixed(0)} ms end-to-end`}. Rates use Wilson 95% confidence intervals; small numbers give wide intervals —
            interpret with care. Statistics are operational indicators, not clinical quality measures.
          </p>
        </>
      )}
    </>
  );
}

function shift(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
