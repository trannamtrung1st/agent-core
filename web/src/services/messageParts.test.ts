import { describe, it, expect } from "vitest";
import {
  hasTask,
  messageText,
  normalizeParts,
  snapshotParts,
  wireParts,
} from "./messageParts";
describe("canonical composer input", () => {
  it.each(["builtin:harness-authoring.v1", "builtin:configuration-assistance.v1"])(
    "preserves built-in Skill %s through invocation, snapshots and references",
    (skillKey) => {
      const invocation = {
        kind: "invocation" as const,
        invocationKind: "skill" as const,
        skillKey,
        label: "Core procedure",
      };
      const parts = normalizeParts([invocation, { ...invocation }]);
      expect(parts).toEqual([invocation]);
      expect(hasTask(parts, "")).toBe(true);
      expect(messageText(parts)).toBe(`/${skillKey}`);
      expect(snapshotParts(parts)).toEqual(parts);
      expect(wireParts(parts)).toEqual([
        { kind: "invocation", invocationKind: "skill", skillKey },
      ]);
      const reference = {
        kind: "skill" as const,
        agentInstanceId: "11111111-1111-1111-1111-111111111111",
        skillKey,
      };
      expect(normalizeParts([{ kind: "reference", reference }])).toEqual([
        { kind: "reference", reference },
      ]);
    },
  );
  it.each([
    "builtin:",
    "builtin:Harness-authoring.v1",
    "builtin:../harness-authoring.v1",
    "builtin:11111111-1111-1111-1111-111111111111",
    `builtin:${"a".repeat(65)}`,
    "unknown:harness-authoring.v1",
  ])(
    "rejects malformed Skill key %s for invocations and references",
    (skillKey) => {
      const invocation = {
        kind: "invocation" as const,
        invocationKind: "skill" as const,
        skillKey,
      };
      expect(() => normalizeParts([invocation])).toThrow("Invalid message part.");
      expect(hasTask([invocation], "")).toBe(false);
      expect(() => normalizeParts([{
        kind: "reference",
        reference: {
          kind: "skill",
          agentInstanceId: "11111111-1111-1111-1111-111111111111",
          skillKey,
        },
      }])).toThrow("Invalid reference locator.");
    },
  );
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
            path: null,
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
  it("rejects empty identities and revisions on immutable Skill references", () => {
    const id = "11111111-1111-1111-1111-111111111111";
    const empty = "00000000-0000-0000-0000-000000000000";
    for (const reference of [
      { kind: "session", sessionId: empty },
      { kind: "skill", agentInstanceId: id, skillKey: `instance:${empty}` },
      { kind: "skill", agentInstanceId: id, skillKey: "instance:review", selectedRevision: 1 },
    ]) expect(() => normalizeParts([{ kind: "reference", reference }])).toThrow();
    expect(() => normalizeParts([{ kind: "invocation", invocationKind: "skill", skillKey: `instance:${empty}` }])).toThrow();
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
    ).toBe(true);
  });
});

it('qualifies home references by actual Space and refuses retired Instance home fields', () => {
  const spaceId = '11111111-1111-4111-8111-111111111111';
  const parts = normalizeParts([{ kind: 'reference', reference: { kind: 'homeFile', spaceId, path: '/home/project.txt' } }]);
  expect(messageText(parts)).toBe(`@homeFile:${spaceId}/home/project.txt`);
  expect(() => normalizeParts([{ kind: 'reference', reference: { kind: 'homeFile', agentInstanceId: spaceId, path: '/home/project.txt' } }])).toThrow();
});
