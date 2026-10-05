import { useEffect, useRef, useState } from "react";
import type { Key } from "react";
import type { AutomationSelection } from "../chat/runPresentation";

export function useAutomationSelection(selection: AutomationSelection | undefined, kind: AutomationSelection["kind"], ids: string[], setSearch: (value: string) => void, setExpanded: (keys: Key[]) => void) {
  const [tableVersion, setTableVersion] = useState(0);
  const applied = useRef<AutomationSelection | undefined>(undefined);
  useEffect(() => {
    if (!selection || selection.kind !== kind || !ids.includes(selection.registrationId) || applied.current === selection) return;
    applied.current = selection;
    setSearch("");
    setExpanded([selection.registrationId]);
    setTableVersion(value => value + 1);
  }, [selection, kind, ids, setSearch, setExpanded]);
  useEffect(() => {
    if (!selection || selection.kind !== kind) return;
    const frame = requestAnimationFrame(() => {
      const button = document.querySelector<HTMLButtonElement>(`[data-automation-id="${selection.registrationId}"]`);
      button?.scrollIntoView({ block: "nearest" });
      const table = button?.closest(".ant-table-content");
      if (table) table.scrollLeft = 0;
      button?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [tableVersion, selection, kind]);
  return tableVersion;
}
