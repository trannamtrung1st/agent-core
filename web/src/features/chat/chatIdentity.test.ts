import { describe, expect, it } from "vitest";
import {
  defaultChatIdentityKey,
  legacyChatIdentityKey,
  managedChatIdentityKey,
  managedInstancePickerLabel,
  parseChatIdentityKey,
  isNewChatIdentityReady,
  resolveNewChatIdentityPresentation
} from "./chatIdentity";

describe("isNewChatIdentityReady", () => {
  it("blocks send until managed inventory finishes loading", () => {
    expect(
      isNewChatIdentityReady({
        chatAgentInstancesLoading: true,
        newChatIdentityKey: "",
        chatAgentInstances: [],
        agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true, language: "en" }]
      })
    ).toBe(false);
  });
});

describe("chatIdentity", () => {
  it("prefers managed instances for the default new-chat identity", () => {
    const key = defaultChatIdentityKey(
      [
        {
          instanceId: "019944af-00d1-7000-8000-000000000099",
          definitionId: "examiner",
          activeVersion: 2,
          name: "Pinned",
          role: "Coach",
          voiceAvailable: true,
          language: "en"
        }
      ],
      [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true, language: "en" }]
    );
    expect(key).toBe(managedChatIdentityKey("019944af-00d1-7000-8000-000000000099"));
    expect(parseChatIdentityKey(key)).toEqual({
      kind: "managed",
      instanceId: "019944af-00d1-7000-8000-000000000099"
    });
  });

  it("falls back to legacy agents when no managed instances exist", () => {
    const key = defaultChatIdentityKey([], [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true, language: "en" }]);
    expect(key).toBe(legacyChatIdentityKey("examiner"));
  });

  it("disambiguates duplicate managed rows with a short instance id", () => {
    const label = managedInstancePickerLabel({
      instanceId: "019944af-00d1-7000-8000-000000000099",
      definitionId: "examiner",
      activeVersion: 1,
      name: "Alex",
      role: "Examiner",
      voiceAvailable: true,
      language: "en"
    });
    expect(label).toContain("00000099");
  });

  it("uses managed persona and voice metadata for new-chat presentation", () => {
    const presentation = resolveNewChatIdentityPresentation(
      managedChatIdentityKey("019944af-00d1-7000-8000-000000000099"),
      [
        {
          instanceId: "019944af-00d1-7000-8000-000000000099",
          definitionId: "examiner",
          activeVersion: 2,
          name: "Pinned",
          role: "Coach",
          voiceAvailable: false,
          language: "en"
        }
      ],
      [{ id: "examiner", version: 1, name: "Sam", role: "Support", description: "", voiceAvailable: true, language: "en" }]
    );
    expect(presentation).toEqual({ displayName: "Pinned", voiceAvailable: false, language: "en" });
  });
});
