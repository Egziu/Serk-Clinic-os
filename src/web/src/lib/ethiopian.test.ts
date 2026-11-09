import { describe, expect, it } from "vitest";
import { isoDateToEthiopian, toEthiopian } from "./ethiopian";

// Same vectors as the C# EthiopianDateTests (server and UI must agree).
describe("Ethiopian calendar", () => {
  it.each([
    ["2007-09-12", "Meskerem 1, 2000 E.C."],
    ["2023-09-12", "Meskerem 1, 2016 E.C."],
    ["2024-09-11", "Meskerem 1, 2017 E.C."],
    ["2026-09-11", "Meskerem 1, 2019 E.C."],
    ["2019-01-07", "Tahsas 29, 2011 E.C."],
    ["2026-11-09", "Tikimt 30, 2019 E.C."],
    ["2023-09-11", "Pagume 6, 2015 E.C."],
    ["1990-03-14", "Megabit 5, 1982 E.C."],
  ])("%s -> %s", (iso, expected) => {
    expect(isoDateToEthiopian(iso)).toBe(expected);
  });

  it("never skips or repeats days across 30 years", () => {
    let prev = toEthiopian(1999, 1, 1);
    const d = new Date(Date.UTC(1999, 0, 2));
    while (d.getUTCFullYear() < 2030) {
      const cur = toEthiopian(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate());
      const monthLen = prev.month === 13 ? (prev.year % 4 === 3 ? 6 : 5) : 30;
      const expected = prev.day < monthLen ? { ...prev, day: prev.day + 1 } : prev.month < 13 ? { year: prev.year, month: prev.month + 1, day: 1 } : { year: prev.year + 1, month: 1, day: 1 };
      expect(cur).toEqual(expected);
      prev = cur;
      d.setUTCDate(d.getUTCDate() + 1);
    }
  });
});
