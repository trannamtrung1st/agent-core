import { describe, expect, it } from "vitest";
import { historyFromPayload } from "../../state/sessionStore";
import { orderConversationEntries } from "./conversationOrder";
import type { HistoryEntry } from "../../state/sessionStore";

function entry(partial: Partial<HistoryEntry> & Pick<HistoryEntry, "entryId" | "role" | "sequence">): HistoryEntry {
  return {
    sourceEventId: null,
    text: partial.entryId,
    responseId: null,
    status: "completed",
    deliveryMode: "text",
    heardTextEndExclusive: 0,
    receivedTextEndExclusive: 0,
    createdAt: "2026-09-30T12:00:00.000Z",
    ...partial
  };
}

describe("orderConversationEntries", () => {
  it("places application messages before the assistant entry that shares their response", () => {
    const ordered = orderConversationEntries([
      entry({ entryId: "u", role: "user", sequence: 1, text: "please review" }),
      entry({ entryId: "a", role: "assistant", sequence: 2, responseId: "r1", text: "Done" }),
      entry({ entryId: "m1", role: "applicationMessage", sequence: 3, responseId: "r1", text: "Still checking" }),
      entry({ entryId: "m2", role: "applicationMessage", sequence: 4, responseId: "r1", text: "Second notice" })
    ]);
    expect(ordered.map((item) => item.entryId)).toEqual(["u", "m1", "m2", "a"]);
  });

  it("leaves turns without an application message in global sequence", () => {
    const ordered = orderConversationEntries([
      entry({ entryId: "u1", role: "user", sequence: 1 }),
      entry({ entryId: "a1", role: "assistant", sequence: 2, responseId: "r1" }),
      entry({ entryId: "u2", role: "user", sequence: 3 }),
      entry({ entryId: "a2", role: "assistant", sequence: 4, responseId: "r2" })
    ]);
    expect(ordered.map((item) => item.entryId)).toEqual(["u1", "a1", "u2", "a2"]);
  });

  it("reconstructs reload history without duplicating the application message", () => {
    const hydrated = historyFromPayload([
      {
        entryId: "m1",
        sequence: 3,
        role: "applicationMessage",
        text: "Still checking the billing case.",
        responseId: "r1",
        status: "completed",
        deliveryMode: "text",
        heardTextEndExclusive: 0,
        receivedTextEndExclusive: 32,
        createdAt: "2026-09-30T12:00:00.000Z"
      },
      {
        entryId: "m1",
        sequence: 3,
        role: "applicationMessage",
        text: "Still checking the billing case.",
        responseId: "r1",
        status: "completed",
        deliveryMode: "text",
        heardTextEndExclusive: 0,
        receivedTextEndExclusive: 32,
        createdAt: "2026-09-30T12:00:00.000Z"
      },
      {
        entryId: "a1",
        sequence: 2,
        role: "assistant",
        text: "Billing review is complete.",
        responseId: "r1",
        status: "completed",
        deliveryMode: "text",
        heardTextEndExclusive: 27,
        receivedTextEndExclusive: 27,
        createdAt: "2026-09-30T12:00:01.000Z"
      }
    ]);
    const unique = hydrated.filter((item, index) => hydrated.findIndex((candidate) => candidate.entryId === item.entryId) === index);
    expect(orderConversationEntries(unique).map((item) => item.role)).toEqual(["applicationMessage", "assistant"]);
  });
});