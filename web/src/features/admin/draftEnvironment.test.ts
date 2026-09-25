import { describe, expect, it } from "vitest";
import {
  applyDraftEnvironmentToCandidate,
  draftEnvironmentEquals,
  readDraftEnvironment
} from "./draftEnvironment";

describe("draftEnvironment", () => {
  it("reads typed environment fields from a draft candidate", () => {
    const candidate = {
      environment: {
        harness: ["examiner-turn-taking"],
        toolAllowlist: ["workspace.read"],
        knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo" }],
        workspace: { templateId: "examiner-default" },
        attachments: { allowUnreadUnsupportedTypes: true }
      }
    };

    expect(readDraftEnvironment(candidate)).toEqual({
      harness: ["examiner-turn-taking"],
      toolAllowlist: ["workspace.read"],
      workspaceTemplateId: "examiner-default",
      knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo" }],
      allowUnreadUnsupportedAttachmentTypes: true
    });
  });

  it("writes environment back without disturbing other candidate fields", () => {
    const base = { systemInstructions: "Body", definitionId: "examiner" };
    const updated = applyDraftEnvironmentToCandidate(base, {
      harness: ["label-a"],
      toolAllowlist: ["knowledge.retrieve"],
      workspaceTemplateId: "",
      knowledgeSources: [],
      allowUnreadUnsupportedAttachmentTypes: false
    });

    expect(updated.systemInstructions).toBe("Body");
    expect(updated.environment).toEqual({
      harness: ["label-a"],
      knowledgeSources: [],
      toolAllowlist: ["knowledge.retrieve"],
      workspace: {},
      attachments: { allowUnreadUnsupportedTypes: false }
    });
  });

  it("compares environments ignoring harness and tool order", () => {
    const left = readDraftEnvironment({
      environment: { harness: ["b", "a"], toolAllowlist: ["workspace.write", "workspace.read"] }
    });
    const right = readDraftEnvironment({
      environment: { harness: ["a", "b"], toolAllowlist: ["workspace.read", "workspace.write"] }
    });
    expect(draftEnvironmentEquals(left, right)).toBe(true);
  });
});
