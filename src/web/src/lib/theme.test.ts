import { describe, expect, it } from "vitest";
import { contrastRatio, deriveAccent } from "./theme";

const darkSurfaces = ["#121614", "#1a1f1c", "#222824"];

describe("deriveAccent", () => {
  // Accents that pass the server rule (>= 4.5:1 against white), including very dark ones that used to fail in dark mode.
  const accents = ["#1e4d3a", "#000000", "#00008b", "#7a1f1f", "#5b2a86", "#0b5c8c", "#595959", "#8a4b00"];

  it.each(accents)("keeps accent text >= 4.5:1 in light and dark themes for %s", (hex) => {
    expect(contrastRatio(hex, "#ffffff")).toBeGreaterThanOrEqual(4.5);
    const t = deriveAccent(hex);
    expect(contrastRatio(t.brand, t.brandSoft)).toBeGreaterThanOrEqual(4.5);
    for (const bg of [...darkSurfaces, t.brandSoftDark, "#0e1311"]) {
      expect(contrastRatio(t.brandDark, bg)).toBeGreaterThanOrEqual(4.5);
    }
  });

});
