import { describe, it, expect } from "vitest";
import {
  hasTask,
  messageText,
  normalizeParts,
  wireParts,
} from "./messageParts";
describe("canonical composer input", () => {
  it("coalesces text, deduplicates IDs, and keeps same-name distinct IDs", () => {
    const parts = normalizeParts([
      { kind: "text", text: "one " },
      { kind: "text", text: "two " },
      {
        kind: "invocation",
        invocationKind: "skill",
        skillKey: "definition:review",
        label: "Review",
      },
      {
        kind: "invocation",
        invocationKind: "skill",
        skillKey: "definition:review",
      },
      {
        kind: "invocation",
        invocationKind: "skill",
        skillKey: "instance:review",
        label: "Review",
      },
    ]);
    expect(parts).toHaveLength(3);
    expect(messageText(parts)).toBe(
      "one two /definition:review/instance:review",
    );
    expect(wireParts(parts).every((p) => !("label" in p))).toBe(true);
  });
  it("accepts nullable server metadata but rejects foreign locator fields", () => {
    expect(
      normalizeParts([
        {
          kind: "reference",
          text: null,
          skillKey: null,
          invocationKind: null,
          label: "chat",
          reference: {
            kind: "session",
            sessionId: "11111111-1111-1111-1111-111111111111",
            itemId: null,
            artifactId: null,
            agentRunId: null,
            agentInstanceId: null,
            skillKey: null,
            selectedRevision: null,
          },
        },
      ])[0],
    ).toEqual({
      kind: "reference",
      label: "chat",
      reference: {
        kind: "session",
        sessionId: "11111111-1111-1111-1111-111111111111",
      },
    });
    expect(() =>
      normalizeParts([
        {
          kind: "reference",
          reference: {
            kind: "session",
            sessionId: "11111111-1111-1111-1111-111111111111",
            path: "/etc/passwd",
          },
        },
      ]),
    ).toThrow();
  });
  it("keeps Unicode bounds and task eligibility", () => {
    expect(
      normalizeParts([{ kind: "text", text: "語".repeat(8000) }]),
    ).toHaveLength(1);
    expect(() =>
      normalizeParts([{ kind: "text", text: "a".repeat(8001) }]),
    ).toThrow();
    expect(
      hasTask(
        [
          {
            kind: "invocation",
            invocationKind: "skill",
            skillKey: "definition:review",
          },
        ],
        "/definition:review",
      ),
    ).toBe(false);
  });
});
