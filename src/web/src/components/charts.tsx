import { useMemo, useState, type KeyboardEvent, type MouseEvent } from "react";

// Hand-rolled SVG charts (no runtime chart dependency): one y-axis, thin marks, recessive grid, hover/keyboard
// tooltip drawn inside the SVG (CSP-safe), and a table alternative rendered by the caller.

export interface LinePoint {
  label: string;
  sub?: string;
  values: number[];
}

function niceMax(v: number): number {
  if (v <= 4) return 4;
  const pow = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
}

function Tooltip({ x, y, lines, width }: { x: number; y: number; lines: string[]; width: number }) {
  const w = Math.max(...lines.map((l) => l.length)) * 6.6 + 16;
  const h = lines.length * 16 + 10;
  const tx = Math.min(Math.max(x + 10, 0), width - w - 2);
  const ty = Math.max(y - h - 8, 2);
  return (
    <g transform={`translate(${tx},${ty})`} pointerEvents="none">
      <rect className="tip-box" width={w} height={h} rx={6} />
      {lines.map((l, i) => (
        <text key={i} className="tip-text" x={8} y={18 + i * 16} fontWeight={i === 0 ? 700 : 400}>{l}</text>
      ))}
    </g>
  );
}

/** Multi-series line chart over a shared x axis. seriesNames.length must equal values length. */
export function LineChart({ points, seriesNames, title, height = 240 }: { points: LinePoint[]; seriesNames: string[]; title: string; height?: number }) {
  const width = 720;
  const pad = { l: 40, r: 12, t: 12, b: 28 };
  const [hover, setHover] = useState<number | null>(null);
  const max = niceMax(Math.max(1, ...points.flatMap((p) => p.values)));
  const x = (i: number) => pad.l + (points.length <= 1 ? 0 : (i * (width - pad.l - pad.r)) / (points.length - 1));
  const y = (v: number) => pad.t + (1 - v / max) * (height - pad.t - pad.b);
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((t) => Math.round(t * max));
  const step = Math.max(1, Math.ceil(points.length / 8));

  const onMove = (e: MouseEvent<SVGRectElement>) => {
    const rect = (e.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect();
    const px = ((e.clientX - rect.left) / rect.width) * width;
    const i = Math.round(((px - pad.l) / (width - pad.l - pad.r)) * (points.length - 1));
    setHover(Math.max(0, Math.min(points.length - 1, i)));
  };
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "ArrowRight") setHover((h) => Math.min(points.length - 1, (h ?? -1) + 1));
    if (e.key === "ArrowLeft") setHover((h) => Math.max(0, (h ?? points.length) - 1));
    if (e.key === "Home") setHover(0);
    if (e.key === "End") setHover(points.length - 1);
    if (e.key === "Escape") setHover(null);
  };
  const hp = hover !== null ? points[hover] : undefined;
  const readout = hp ? `${hp.label}${hp.sub ? ` (${hp.sub})` : ""}: ${seriesNames.map((n, s) => `${n} ${formatNum(hp.values[s] ?? 0)}`).join(", ")}` : "";

  return (
    <>
    <svg className="chart" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${title}. Focus and use the left and right arrow keys (Home, End) to read each day, or use the table view.`} tabIndex={0} onKeyDown={onKey} onBlur={() => setHover(null)}>
      {ticks.map((t) => (
        <g key={t}>
          <line className="grid" x1={pad.l} x2={width - pad.r} y1={y(t)} y2={y(t)} />
          <text className="axis-label" x={pad.l - 6} y={y(t) + 4} textAnchor="end">{t}</text>
        </g>
      ))}
      {points.map((p, i) => {
        const last = i === points.length - 1;
        // Regular ticks, plus the final date unless it would collide with the previous tick.
        const show = (i % step === 0 && (last || points.length - 1 - i >= step / 2)) || last;
        return show ? (
          <text key={p.label} className="axis-label" x={x(i)} y={height - 8} textAnchor={last ? "end" : i === 0 ? "start" : "middle"}>{p.label.slice(5)}</text>
        ) : null;
      })}
      {seriesNames.map((_, s) => (
        <polyline key={s} className={`series-${s + 1}`} strokeLinejoin="round" strokeLinecap="round" points={points.map((p, i) => `${x(i)},${y(p.values[s] ?? 0)}`).join(" ")} />
      ))}
      {hover !== null && points[hover] && (
        <>
          <line className="crosshair" x1={x(hover)} x2={x(hover)} y1={pad.t} y2={height - pad.b} />
          {seriesNames.map((_, s) => <circle key={s} cx={x(hover)} cy={y(points[hover]!.values[s] ?? 0)} r={4} className={`series-${s + 1}`} fill="var(--surface-raised)" />)}
          <Tooltip x={x(hover)} y={y(Math.max(...points[hover]!.values))} width={width}
            lines={[points[hover]!.label + (points[hover]!.sub ? ` · ${points[hover]!.sub}` : ""), ...seriesNames.map((n, s) => `${n}: ${formatNum(points[hover]!.values[s] ?? 0)}`)]} />
        </>
      )}
      <rect className="hit" x={pad.l} y={pad.t} width={width - pad.l - pad.r} height={height - pad.t - pad.b} onMouseMove={onMove} onMouseLeave={() => setHover(null)} />
    </svg>
    <p className="sr-only" aria-live="polite">{readout}</p>
    </>
  );
}

/** Horizontal ranked bars (single series => one colour). */
export function BarList({ items, title, unit = "", width = 440 }: { items: { label: string; value: number; note?: string; display?: string }[]; title: string; unit?: string; width?: number }) {
  const [hover, setHover] = useState<number | null>(null);
  // Sized for a half-width panel so 12px labels render at ~12px (no down-scaling).
  const rowH = 28;
  const labelW = Math.round(width * 0.36);
  const max = Math.max(1, ...items.map((i) => i.value));
  const height = Math.max(rowH, items.length * rowH) + 8;
  return (
    <svg className="chart" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${title}. Use the table view for exact values.`}>
      {items.map((it, i) => {
        const w = Math.max(it.value > 0 ? 4 : 0, (it.value / max) * (width - labelW - 70));
        return (
          <g key={it.label} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
            <title>{`${it.label}: ${it.display ?? formatNum(it.value) + unit}${it.note ? ` (${it.note})` : ""}`}</title>
            <text className="axis-label bar-label" x={labelW - 8} y={i * rowH + 18} textAnchor="end">{it.label.length > 22 ? it.label.slice(0, 21) + "…" : it.label}</text>
            <rect className="bar" x={labelW} y={i * rowH + 7} width={w} height={16} rx={4} />
            <text className="axis-label" x={labelW + w + 6} y={i * rowH + 19}>{it.display ?? `${formatNum(it.value)}${unit}`}</text>
            <rect className="hit" x={0} y={i * rowH} width={width} height={rowH} />
          </g>
        );
      })}
      {hover !== null && items[hover] && <Tooltip x={labelW + 20} y={hover * rowH + 30} width={width} lines={[items[hover]!.label, items[hover]!.display ?? `${formatNum(items[hover]!.value)}${unit}`, ...(items[hover]!.note ? [items[hover]!.note!] : [])]} />}
    </svg>
  );
}

const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

/** Weekday × hour arrival heatmap (sequential single-hue ramp, 7 classes incl. zero). */
export function Heatmap({ cells, title }: { cells: { isoWeekday: number; hour: number; count: number }[]; title: string }) {
  const [hover, setHover] = useState<{ d: number; h: number; c: number } | null>(null);
  const hours = useMemo(() => {
    const hs = cells.map((c) => c.hour);
    const lo = Math.min(7, ...hs);
    const hi = Math.max(18, ...hs);
    return Array.from({ length: hi - lo + 1 }, (_, i) => lo + i);
  }, [cells]);
  const max = Math.max(1, ...cells.map((c) => c.count));
  const cell = 26;
  const left = 40;
  const width = left + hours.length * (cell + 2) + 8;
  const height = 20 + 7 * (cell + 2) + 34;
  const legendY = 20 + 7 * (cell + 2) + 10;
  const get = (d: number, h: number) => cells.find((c) => c.isoWeekday === d && c.hour === h)?.count ?? 0;
  const cls = (n: number) => (n === 0 ? "heat-0" : `heat-${Math.min(6, Math.ceil((n / max) * 6))}`);
  return (
    <svg className="chart" viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`${title}. Use the table view for exact values.`}>
      {hours.map((h, i) => (i % 2 === 0 ? <text key={h} className="axis-label" x={left + i * (cell + 2) + cell / 2} y={12} textAnchor="middle">{String(h).padStart(2, "0")}</text> : null))}
      {DAYS.map((d, di) => (
        <g key={d}>
          <text className="axis-label" x={left - 6} y={20 + di * (cell + 2) + 17} textAnchor="end">{d}</text>
          {hours.map((h, hi) => {
            const n = get(di + 1, h);
            return <rect key={h} className={cls(n)} x={left + hi * (cell + 2)} y={20 + di * (cell + 2)} width={cell} height={cell} rx={4}
              onMouseEnter={() => setHover({ d: di, h: hi, c: n })} onMouseLeave={() => setHover(null)}><title>{`${d} ${String(h).padStart(2, "0")}:00 — ${n} arrivals`}</title></rect>;
          })}
        </g>
      ))}
      <text className="axis-label" x={left} y={legendY + 13}>0</text>
      {[0, 1, 2, 3, 4, 5, 6].map((k) => <rect key={k} className={`heat-${k}`} x={left + 14 + k * 20} y={legendY} width={18} height={18} rx={3} />)}
      <text className="axis-label" x={left + 14 + 7 * 20 + 4} y={legendY + 13}>{formatNum(max)} arrivals (more)</text>
      {hover && <Tooltip x={left + hover.h * (cell + 2)} y={20 + hover.d * (cell + 2)} width={width} lines={[`${DAYS[hover.d]} ${String(hours[hover.h]).padStart(2, "0")}:00`, `${hover.c} arrivals`]} />}
    </svg>
  );
}

export function formatNum(v: number): string {
  return Number.isInteger(v) ? v.toLocaleString("en-US") : v.toLocaleString("en-US", { maximumFractionDigits: 1 });
}
