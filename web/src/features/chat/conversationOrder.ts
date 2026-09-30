import type { HistoryEntry } from "../../state/sessionStore";

export function orderConversationEntries(entries: HistoryEntry[]): HistoryEntry[] {
  const grouped = new Map<string, HistoryEntry[]>();
  for (const entry of entries) {
    if (!entry.responseId || (entry.role !== "applicationMessage" && entry.role !== "assistant")) {
      continue;
    }

    const group = grouped.get(entry.responseId) ?? [];
    group.push(entry);
    grouped.set(entry.responseId, group);
  }

  const emitted = new Set<string>();
  const result: HistoryEntry[] = [];
  for (const entry of entries) {
    if (emitted.has(entry.entryId)) {
      continue;
    }

    const group = entry.responseId ? grouped.get(entry.responseId) : undefined;
    const applications = group?.filter((item) => item.role === "applicationMessage") ?? [];
    if (group && applications.length > 0 && (entry.role === "applicationMessage" || entry.role === "assistant")) {
      const assistants = group.filter((item) => item.role === "assistant");
      for (const item of [...applications, ...assistants]) {
        if (emitted.has(item.entryId)) {
          continue;
        }

        emitted.add(item.entryId);
        result.push(item);
      }
      continue;
    }

    emitted.add(entry.entryId);
    result.push(entry);
  }

  return result;
}
