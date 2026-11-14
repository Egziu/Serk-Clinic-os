import { describe, expect, it } from "vitest";
import { rankCommands, type Command } from "./CommandPalette";

const cmd = (id: string, label: string, group: string, keywords?: string): Command => ({ id, label, group, icon: "x", keywords, run: () => undefined });
const all = [cmd("data", "Data & backups", "Administration", "export csv backup"), cmd("fin", "Finance", "Money", "ledger close"), cmd("setup", "Clinic setup", "Administration", "logo motto address")];

describe("rankCommands", () => {
  it("returns everything for an empty query", () => expect(rankCommands(all, " ")).toHaveLength(3));
  it("matches keywords and requires every word", () => {
    expect(rankCommands(all, "logo").map((c) => c.id)).toEqual(["setup"]);
    expect(rankCommands(all, "backup ledger")).toEqual([]);
  });
  it("prefers word-prefix matches", () => expect(rankCommands(all, "admin")[0]!.group).toBe("Administration"));
});
