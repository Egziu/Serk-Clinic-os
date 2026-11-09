import { isoDateToEthiopian } from "./ethiopian";

/** Facility time zone (v1: all facilities are Africa/Addis_Ababa; the server owns the canonical value). */
export const FACILITY_TIME_ZONE = "Africa/Addis_Ababa";

const timeFmt = new Intl.DateTimeFormat("en-GB", { timeZone: FACILITY_TIME_ZONE, hour: "2-digit", minute: "2-digit", hour12: false });
const dateFmt = new Intl.DateTimeFormat("en-CA", { timeZone: FACILITY_TIME_ZONE, year: "numeric", month: "2-digit", day: "2-digit" });

export function localIsoDate(instant: string | Date): string {
  return dateFmt.format(typeof instant === "string" ? new Date(instant) : instant);
}

export function formatTime(instant: string): string {
  return timeFmt.format(new Date(instant));
}

/** Calendar-labelled date: "2026-11-09 (Gregorian) · Tikimt 30, 2019 E.C." */
export function formatDualDate(isoDate: string): string {
  return `${isoDate} (Gregorian) · ${isoDateToEthiopian(isoDate)}`;
}

export function formatDualInstant(instant: string): string {
  const d = localIsoDate(instant);
  return `${d} ${formatTime(instant)} EAT · ${isoDateToEthiopian(d)}`;
}

/** Converts a facility-local "YYYY-MM-DD" + "HH:mm" (EAT, UTC+3, no DST) to an ISO UTC instant. */
export function facilityLocalToUtcIso(date: string, time: string): string {
  return new Date(`${date}T${time}:00+03:00`).toISOString();
}

export function shortHash(hash: string): string {
  return hash.slice(0, 12);
}
