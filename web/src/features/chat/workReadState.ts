import { useSyncExternalStore } from "react";
import type { AgentRun } from "../../services/api";

export const WORK_READ_STORAGE_KEY = "agent-core.background-run-read";
const listeners = new Set<() => void>();
function subscribe(listener: () => void) {
  listeners.add(listener);
  window.addEventListener("storage", listener);
  return () => { listeners.delete(listener); window.removeEventListener("storage", listener); };
}
function snapshot() {
  try { return window.localStorage.getItem(WORK_READ_STORAGE_KEY) ?? "{}"; }
  catch { return "{}"; }
}
function parse(value: string): Record<string, string> {
  try {
    const parsed: unknown = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, string> : {};
  } catch { return {}; }
}
function version(item: AgentRun) { return `${item.revision}:${item.updatedAt}`; }
function acknowledgedRevision(value: unknown): number {
  if (typeof value !== "string") return -1;
  const revision = Number(value.split(":", 1)[0]);
  return Number.isSafeInteger(revision) && revision > 0 ? revision : -1;
}
export function useWorkReadState() {
  const read = parse(useSyncExternalStore(subscribe, snapshot));
  return {
    isUnread: (item: AgentRun) => item.status === "completed" && !!item.outcome?.attentionRequired && acknowledgedRevision(read[item.agentRunId]) <= item.revision && read[item.agentRunId] !== version(item),
    markRead: (items: AgentRun[]) => {
      const next = parse(snapshot());
      for (const item of items) {
        if (item.status === "completed" && item.outcome?.attentionRequired && acknowledgedRevision(next[item.agentRunId]) <= item.revision) {
          next[item.agentRunId] = version(item);
        }
      }
      try { window.localStorage.setItem(WORK_READ_STORAGE_KEY, JSON.stringify(next)); }
      catch { throw new Error("Unable to save read status in this browser. Try again."); }
      listeners.forEach(listener => listener());
    }
  };
}
