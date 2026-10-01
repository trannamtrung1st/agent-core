import { sameSessionId } from "../app/sessionRoute";
import {
  historyFromPayload,
  isInFlightOutput,
  useSessionStore,
  type HistoryEntry
} from "../state/sessionStore";
import { listSessionMessages, type HistoryPage } from "./api";

export const HISTORY_PAGE_SIZE = 50;

let historyEpoch = 0;
let historyAbort: AbortController | null = null;
let olderCursor: number | null = null;

export function mergeHistoryEntries(existing: HistoryEntry[], incoming: HistoryEntry[]): HistoryEntry[] {
  const byId = new Map<string, HistoryEntry>();
  for (const entry of existing) {
    byId.set(entry.entryId, entry);
  }

  for (const entry of incoming) {
    const current = byId.get(entry.entryId);
    if (!current) {
      byId.set(entry.entryId, entry);
      continue;
    }

    const incomingTerminal =
      entry.status === "completed" || entry.status === "failed" || entry.status === "interrupted";
    const currentTerminal =
      current.status === "completed" || current.status === "failed" || current.status === "interrupted";
    if (incomingTerminal && !currentTerminal) {
      byId.set(entry.entryId, {
        ...entry,
        speechText: entry.speechText || current.speechText,
        memoryReceipts: entry.memoryReceipts?.length ? entry.memoryReceipts : current.memoryReceipts
      });
      continue;
    }

    if (currentTerminal && !incomingTerminal) {
      continue;
    }

    if (currentTerminal && incomingTerminal) {
      byId.set(entry.entryId, {
        ...current,
        speechText: current.speechText || entry.speechText,
        memoryReceipts: current.memoryReceipts?.length ? current.memoryReceipts : entry.memoryReceipts
      });
      continue;
    }

    if (entry.text.length > current.text.length
        || entry.receivedTextEndExclusive > current.receivedTextEndExclusive) {
      byId.set(entry.entryId, {
        ...entry,
        speechText: entry.speechText || current.speechText,
        memoryReceipts: entry.memoryReceipts?.length ? entry.memoryReceipts : current.memoryReceipts
      });
      continue;
    }

    byId.set(entry.entryId, {
      ...current,
      speechText: current.speechText || entry.speechText,
      memoryReceipts: current.memoryReceipts?.length ? current.memoryReceipts : entry.memoryReceipts
    });
  }

  return [...byId.values()].sort((left, right) => left.sequence - right.sequence);
}

export function beginSessionHistory(): number {
  historyAbort?.abort();
  historyAbort = new AbortController();
  olderCursor = null;
  historyEpoch += 1;
  return historyEpoch;
}

export function historyRequestSignal(): AbortSignal | undefined {
  return historyAbort?.signal;
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError"
    || (error instanceof Error && error.name === "AbortError");
}

function minSequence(entries: HistoryEntry[]): number | null {
  if (entries.length === 0) {
    return null;
  }

  return entries.reduce((min, entry) => Math.min(min, entry.sequence), entries[0]!.sequence);
}

function maxSequence(entries: HistoryEntry[]): number {
  return entries.reduce((max, entry) => Math.max(max, entry.sequence), 0);
}

function cursorFromPage(page: HistoryPage, items: HistoryEntry[]): number | null {
  if (page.nextBefore != null) {
    return page.nextBefore;
  }

  if (!page.hasOlder) {
    return null;
  }

  return minSequence(items);
}

function applyOlderMeta(page: HistoryPage, items: HistoryEntry[], loaded: HistoryEntry[]): void {
  olderCursor = cursorFromPage(page, items);
  const reachedStart = loaded.some((entry) => entry.sequence <= 1);
  useSessionStore.setState({
    historyHasOlder: Boolean(page.hasOlder) && olderCursor != null && !reachedStart,
    historyOlderLoading: false
  });
}

export async function loadNewestHistoryPage(
  sessionId: string,
  options?: { replaceWindow?: boolean }
): Promise<void> {
  const replaceWindow = options?.replaceWindow !== false;
  const epoch = replaceWindow ? beginSessionHistory() : historyEpoch;
  const signal = historyRequestSignal();
  try {
    const page = await listSessionMessages(sessionId, { limit: HISTORY_PAGE_SIZE, signal });
    if (epoch !== historyEpoch) {
      return;
    }

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return;
    }

    const incoming = historyFromPayload(page.items);
    const pageMin = minSequence(incoming);
    const pageMax = maxSequence(incoming);
    const retained = replaceWindow
      ? latest.entries.filter((entry) => pageMin == null || entry.sequence >= pageMin)
      : latest.entries;
    const merged = mergeHistoryEntries(retained, incoming);
    const withLive = pageMax > 0
      ? mergeHistoryEntries(merged, latest.entries.filter((entry) => entry.sequence > pageMax))
      : merged;

    const durableTerminal = latest.liveResponseId == null
      ? null
      : incoming.find((entry) =>
          entry.role === "assistant"
          && entry.responseId === latest.liveResponseId
          && (entry.status === "completed"
            || entry.status === "failed"
            || entry.status === "interrupted"));
    const clearLiveConversation =
      durableTerminal != null
      && !isInFlightOutput(latest.outputState);
    useSessionStore.setState({
      entries: withLive,
      ...(clearLiveConversation
        ? {
            liveResponseId: null,
            conversationExecutionId: null,
            outputState: "idle",
            activeProgress: null,
            pendingApproval: null
          }
        : {})
    });
    applyOlderMeta(page, incoming, withLive);
  } catch (error) {
    if (isAbortError(error) || epoch !== historyEpoch) {
      return;
    }

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return;
    }

    useSessionStore.setState({ historyOlderLoading: false });
  }
}

export async function loadOlderHistoryPage(): Promise<void> {
  const snapshot = useSessionStore.getState();
  const sessionId = snapshot.sessionId;
  if (!sessionId || snapshot.historyOlderLoading || !snapshot.historyHasOlder) {
    return;
  }

  const before = olderCursor ?? snapshot.entries[0]?.sequence;
  if (before == null || before <= 1) {
    useSessionStore.setState({ historyHasOlder: false, historyOlderLoading: false });
    return;
  }

  const epoch = historyEpoch;
  const signal = historyRequestSignal();
  useSessionStore.setState({ historyOlderLoading: true });
  try {
    const page = await listSessionMessages(sessionId, { before, limit: HISTORY_PAGE_SIZE, signal });
    if (epoch !== historyEpoch) {
      return;
    }

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return;
    }

    const incoming = historyFromPayload(page.items);
    const merged = mergeHistoryEntries(latest.entries, incoming);
    useSessionStore.setState({ entries: merged });
    applyOlderMeta(page, incoming, merged);
  } catch (error) {
    if (isAbortError(error) || epoch !== historyEpoch) {
      return;
    }

    const latest = useSessionStore.getState();
    if (!sameSessionId(latest.sessionId, sessionId)) {
      return;
    }

    useSessionStore.setState({ historyOlderLoading: false });
  }
}
