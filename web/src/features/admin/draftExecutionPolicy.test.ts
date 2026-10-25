import { describe, expect, it } from "vitest";
import {
  applyDraftExecutionPolicyToCandidate,
  draftExecutionPolicyEquals,
  readDraftExecutionPolicy
} from "./draftExecutionPolicy";

describe("draftExecutionPolicy", () => {
  it("reads typed executionPolicy fields from a draft candidate", () => {
    const candidate = {
      executionPolicy: {
        harness: ["examiner-turn-taking"],
        toolAllowlist: ["workspace.read"],
        knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo", resourcePath: "" }],
        workspace: { templateId: "examiner-default" },
        attachments: { allowUnreadUnsupportedTypes: true }
      }
    };

    expect(readDraftExecutionPolicy(candidate)).toEqual({
      harness: ["examiner-turn-taking"],
      toolAllowlist: ["workspace.read"],
      workspaceTemplateId: "examiner-default",
      knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo", resourcePath: "" }],
      allowUnreadUnsupportedAttachmentTypes: true
    });
  });

  it("writes executionPolicy back without disturbing other candidate fields", () => {
    const base = { agentInstructions: "Body", definitionId: "examiner" };
    const updated = applyDraftExecutionPolicyToCandidate(base, {
      harness: ["label-a"],
      toolAllowlist: ["knowledge.retrieve"],
      workspaceTemplateId: "",
      knowledgeSources: [],
      allowUnreadUnsupportedAttachmentTypes: false
    });

    expect(updated.agentInstructions).toBe("Body");
    expect(updated.executionPolicy).toEqual({
      harness: ["label-a"],
      knowledgeSources: [],
      toolAllowlist: ["knowledge.retrieve"],
      workspace: {},
      attachments: { allowUnreadUnsupportedTypes: false }
    });
  });

  it("compares environments ignoring harness and tool order", () => {
    const left = readDraftExecutionPolicy({
      executionPolicy: { harness: ["b", "a"], toolAllowlist: ["workspace.write", "workspace.read"] }
    });
    const right = readDraftExecutionPolicy({
      executionPolicy: { harness: ["a", "b"], toolAllowlist: ["workspace.read", "workspace.write"] }
    });
    expect(draftExecutionPolicyEquals(left, right)).toBe(true);
  });

  it("writes an explicit knowledge path and omits a blank one", () => {
    const updated = applyDraftExecutionPolicyToCandidate({}, {
      harness: [],
      toolAllowlist: [],
      workspaceTemplateId: "",
      allowUnreadUnsupportedAttachmentTypes: false,
      knowledgeSources: [
        { identity: "policy", title: "Policy", citation: "cite", resourcePath: " knowledge/policy.md " },
        { identity: "legacy", title: "Legacy", citation: "cite", resourcePath: " " }
      ]
    });

    expect((updated.executionPolicy as { knowledgeSources: unknown }).knowledgeSources).toEqual([
      { identity: "policy", title: "Policy", citation: "cite", resourcePath: "knowledge/policy.md" },
      { identity: "legacy", title: "Legacy", citation: "cite" }
    ]);
  });
});
