import { describe, expect, it } from "vitest";
import { defaultChatIdentityKey, managedChatIdentityKey, managedInstancePickerLabel, parseChatIdentityKey, isNewChatIdentityReady, resolveNewChatIdentityPresentation } from "./chatIdentity";

const instance = {
  instanceId: "019944af-00d1-7000-8000-000000000099", definitionId: "examiner",
  activeVersion: 2, name: "Pinned", role: "Coach", voiceAvailable: false, language: "en"
};

describe("instance chat identity", () => {
  it("selects an instance and resolves its pinned presentation", () => {
    const key = defaultChatIdentityKey([instance]);
    expect(key).toBe(managedChatIdentityKey(instance.instanceId));
    expect(parseChatIdentityKey(key)).toEqual({ kind: "managed", instanceId: instance.instanceId });
    expect(resolveNewChatIdentityPresentation(key, [instance])).toEqual({ displayName: "Pinned", voiceAvailable: false, language: "en" });
    expect(managedInstancePickerLabel(instance)).toContain("00000099");
  });
  it("keeps empty inventory empty and rejects definition keys", () => {
    expect(defaultChatIdentityKey([])).toBe("");
    expect(parseChatIdentityKey("legacy:examiner")).toBeNull();
    expect(resolveNewChatIdentityPresentation("legacy:examiner", [instance])).toBeNull();
  });
  it("requires resolved inventory containing the selected instance", () => {
    const input = { chatAgentInstancesLoading: false, newChatIdentityKey: managedChatIdentityKey(instance.instanceId), chatAgentInstances: [instance] };
    expect(isNewChatIdentityReady(input)).toBe(true);
    expect(isNewChatIdentityReady({ ...input, chatAgentInstancesLoading: true })).toBe(false);
    expect(isNewChatIdentityReady({ ...input, chatAgentInstances: [] })).toBe(false);
  });
});
