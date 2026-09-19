import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    listSessionMessages: vi.fn()
  };
});

import { emptySession, useSessionStore, type HistoryEntry } from "../state/sessionStore";
import { listSessionMessages } from "./api";
import {
  beginSessionHistory,
  loadNewestHistoryPage,
  loadOlderHistoryPage,
  mergeHistoryEntries
} from "./sessionHistory";

function row(sequence: number, extra?: Partial<HistoryEntry>): HistoryEntry {
  return {
    entryId: extra?.entryId ?? `e${sequence}`,
    sequence,
    sourceEventId: `e${sequence}`,
    role: sequence % 2 === 1 ? "user" : "assistant",
    text: extra?.text ?? `Message ${sequence}`,
    responseId: sequence % 2 === 0 ? `r${sequence}` : null,
    status: extra?.status ?? "completed",
    deliveryMode: "text",
    heardTextEndExclusive: 8,
    receivedTextEndExclusive: 8,
    createdAt: "2026-01-01T00:00:00Z",
    ...extra
  };
}

function payload(entry: HistoryEntry) {
  return { ...entry };
}

describe("session history controller", () => {
  afterEach(() => {
    beginSessionHistory();
    useSessionStore.setState({ ...emptySession(), agents: [], selectedAgentId: "examiner" });
    vi.mocked(listSessionMessages).mockReset();
  });

  it("merges overlapping pages by identity and keeps sequence order", () => {
    const merged = mergeHistoryEntries(
      [row(3), row(4)],
      [row(1), row(3, { text: "stale overlap" }), row(2)]
    );
    expect(merged.map((entry) => `${entry.sequence}:${entry.text}`)).toEqual([
      "1:Message 1",
      "2:Message 2",
      "3:Message 3",
      "4:Message 4"
    ]);
  });

  it("keeps speechText on the same entry across prepend and live overlap", () => {
    const merged = mergeHistoryEntries(
      [row(3, { text: "Shown display." }), row(4)],
      [
        row(1, { text: "Earlier", speechText: "Spoken earlier" }),
        row(3, { text: "stale overlap", speechText: "Hidden speech" })
      ]
    );
    expect(merged.map((entry) => entry.entryId)).toEqual(["e1", "e3", "e4"]);
    expect(merged.find((entry) => entry.sequence === 3)?.text).toBe("Shown display.");
    expect(merged.find((entry) => entry.sequence === 3)?.speechText).toBe("Hidden speech");
    expect(merged.find((entry) => entry.sequence === 1)?.speechText).toBe("Spoken earlier");
  });

  it("opens a 500-entry transcript with one newest-page request", async () => {
    const newest = Array.from({ length: 50 }, (_, index) => payload(row(index + 451)));
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: newest,
      nextAfter: 500,
      hasMore: false,
      hasOlder: true,
      nextBefore: 451
    });
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "s-500",
      entries: Array.from({ length: 20 }, (_, index) => row(index + 481))
    });

    await loadNewestHistoryPage("s-500");

    expect(listSessionMessages).toHaveBeenCalledTimes(1);
    expect(listSessionMessages).toHaveBeenCalledWith(
      "s-500",
      expect.objectContaining({ limit: 50 })
    );
    expect(vi.mocked(listSessionMessages).mock.calls[0]?.[1]).not.toHaveProperty("after");
    expect(vi.mocked(listSessionMessages).mock.calls[0]?.[1]).not.toHaveProperty("before");
    expect(useSessionStore.getState().entries).toHaveLength(50);
    expect(useSessionStore.getState().entries[0]?.sequence).toBe(451);
    expect(useSessionStore.getState().entries[49]?.sequence).toBe(500);
    expect(useSessionStore.getState().historyHasOlder).toBe(true);
  });

  it("keeps a live append that arrives while an older page is in flight", async () => {
    let resolveOlder: ((value: {
      items: HistoryEntry[];
      nextAfter: number;
      hasMore: boolean;
      hasOlder: boolean;
      nextBefore: number;
    }) => void) | undefined;
    const olderGate = new Promise<{
      items: HistoryEntry[];
      nextAfter: number;
      hasMore: boolean;
      hasOlder: boolean;
      nextBefore: number;
    }>((resolve) => {
      resolveOlder = resolve;
    });
    vi.mocked(listSessionMessages).mockReturnValue(olderGate as never);
    beginSessionHistory();
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "s-live",
      entries: [row(51), row(52)],
      historyHasOlder: true
    });

    const pending = loadOlderHistoryPage();
    useSessionStore.setState({
      entries: [...useSessionStore.getState().entries, row(53)]
    });
    resolveOlder?.({
      items: [payload(row(1)), payload(row(2))],
      nextAfter: 2,
      hasMore: false,
      hasOlder: false,
      nextBefore: 1
    });
    await pending;

    expect(useSessionStore.getState().entries.map((entry) => entry.sequence)).toEqual([1, 2, 51, 52, 53]);
  });

  it("ignores a stale older page after the session changes", async () => {
    let resolveOlder: ((value: {
      items: HistoryEntry[];
      nextAfter: number;
      hasMore: boolean;
      hasOlder: boolean;
      nextBefore: number | null;
    }) => void) | undefined;
    const olderGate = new Promise<{
      items: HistoryEntry[];
      nextAfter: number;
      hasMore: boolean;
      hasOlder: boolean;
      nextBefore: number | null;
    }>((resolve) => {
      resolveOlder = resolve;
    });
    vi.mocked(listSessionMessages).mockReturnValue(olderGate as never);
    beginSessionHistory();
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "s-one",
      entries: [row(51)],
      historyHasOlder: true
    });

    const pending = loadOlderHistoryPage();
    beginSessionHistory();
    useSessionStore.setState({
      ...emptySession(),
      sessionId: "s-two",
      entries: [row(99, { text: "other session" })]
    });
    resolveOlder?.({
      items: [payload(row(1, { text: "stale" }))],
      nextAfter: 1,
      hasMore: false,
      hasOlder: false,
      nextBefore: null
    });
    await pending;

    expect(useSessionStore.getState().sessionId).toBe("s-two");
    expect(useSessionStore.getState().entries.map((entry) => entry.text)).toEqual(["other session"]);
  });

  it("uses hasOlder and nextBefore from the API rather than page length", async () => {
    vi.mocked(listSessionMessages).mockResolvedValue({
      items: Array.from({ length: 50 }, (_, index) => payload(row(index + 1))),
      nextAfter: 50,
      hasMore: false,
      hasOlder: false,
      nextBefore: null
    });
    useSessionStore.setState({ ...emptySession(), sessionId: "s-exact" });
    await loadNewestHistoryPage("s-exact");
    expect(useSessionStore.getState().historyHasOlder).toBe(false);

    vi.mocked(listSessionMessages).mockResolvedValue({
      items: [payload(row(20))],
      nextAfter: 20,
      hasMore: false,
      hasOlder: true,
      nextBefore: 20
    });
    useSessionStore.setState({ ...emptySession(), sessionId: "s-short" });
    await loadNewestHistoryPage("s-short");
    expect(useSessionStore.getState().historyHasOlder).toBe(true);
  });
});
