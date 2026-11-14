import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Icon } from "./icons";

export interface Command { id: string; label: string; group: string; icon: string; keywords?: string; run: () => void }

/** Ranks commands: whole-word prefix matches first, then substring matches; all words of the query must match. */
export function rankCommands(commands: Command[], query: string): Command[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return commands;
  const scored = commands.map((c) => {
    const hay = `${c.label} ${c.group} ${c.keywords ?? ""}`.toLowerCase();
    if (!words.every((w) => hay.includes(w))) return null;
    const prefix = words.filter((w) => hay.split(/\s+/).some((t) => t.startsWith(w))).length;
    return { c, score: prefix * 10 - hay.indexOf(words[0]!) / 100 };
  }).filter((x): x is { c: Command; score: number } => x !== null);
  return scored.sort((a, b) => b.score - a.score).map((x) => x.c);
}

/** Keyboard-first "jump to" palette (Ctrl/⌘+K). Lists only what the signed-in person may open. */
export function CommandPalette({ commands, onClose }: { commands: Command[]; onClose: () => void }) {
  const [q, setQ] = useState("");
  const [active, setActive] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const results = useMemo(() => rankCommands(commands, q), [commands, q]);

  // Mounted only while open, so state starts fresh each time.
  useEffect(() => { input.current?.focus(); }, []);

  const choose = (c: Command | undefined) => { if (!c) return; onClose(); c.run(); };
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") { e.preventDefault(); onClose(); }
    else if (e.key === "ArrowDown") { e.preventDefault(); setActive((a) => Math.min(a + 1, results.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); choose(results[active]); }
  };

  return (
    <div className="palette-backdrop" onMouseDown={onClose}>
      <div className="palette" role="dialog" aria-modal="true" aria-label="Jump to" onMouseDown={(e) => e.stopPropagation()} onKeyDown={onKey}>
        <div className="palette-search">
          <Icon name="search" />
          <input ref={input} role="combobox" aria-expanded="true" aria-controls="palette-list" aria-autocomplete="list"
            aria-activedescendant={results[active] ? `cmd-${results[active].id}` : undefined}
            aria-label="Type a page or action" placeholder="Jump to a page or action…" value={q} onChange={(e) => { setQ(e.target.value); setActive(0); }} />
          <kbd>Esc</kbd>
        </div>
        <ul id="palette-list" role="listbox" aria-label="Results">
          {results.length === 0 && <li className="muted palette-empty">No match.</li>}
          {results.map((c, i) => (
            <li key={c.id} id={`cmd-${c.id}`} role="option" aria-selected={i === active} className={i === active ? "active" : ""}
              onMouseEnter={() => setActive(i)} onClick={() => choose(c)}>
              <Icon name={c.icon} /> <span>{c.label}</span> <span className="muted">{c.group}</span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
