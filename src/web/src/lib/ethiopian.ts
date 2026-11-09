// Ethiopian (Ge'ez) calendar for DISPLAY ONLY. Mirrors Serk.Clinic.SharedKernel.EthiopianDate (same JDN algorithm).
const JDN_OFFSET = 1723856;

export const ETHIOPIAN_MONTHS = [
  "Meskerem", "Tikimt", "Hidar", "Tahsas", "Tir", "Yekatit", "Megabit", "Miyazya", "Ginbot", "Sene", "Hamle", "Nehase", "Pagume",
] as const;

export interface EthiopianDate {
  year: number;
  month: number;
  day: number;
}

function gregorianToJdn(y: number, m: number, d: number): number {
  const a = Math.floor((14 - m) / 12);
  const yy = y + 4800 - a;
  const mm = m + 12 * a - 3;
  return d + Math.floor((153 * mm + 2) / 5) + 365 * yy + Math.floor(yy / 4) - Math.floor(yy / 100) + Math.floor(yy / 400) - 32045;
}

export function toEthiopian(y: number, m: number, d: number): EthiopianDate {
  const jdn = gregorianToJdn(y, m, d);
  const r = (jdn - JDN_OFFSET) % 1461;
  const n = (r % 365) + 365 * Math.floor(r / 1460);
  const year = 4 * Math.floor((jdn - JDN_OFFSET) / 1461) + Math.floor(r / 365) - Math.floor(r / 1460);
  return { year, month: Math.floor(n / 30) + 1, day: (n % 30) + 1 };
}

export function formatEthiopian(e: EthiopianDate): string {
  return `${ETHIOPIAN_MONTHS[e.month - 1]} ${e.day}, ${e.year} E.C.`;
}

/** "2026-11-09" -> "Tikimt 30, 2019 E.C." */
export function isoDateToEthiopian(iso: string): string {
  const [y, m, d] = iso.split("-").map(Number);
  return formatEthiopian(toEthiopian(y!, m!, d!));
}
