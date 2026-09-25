import { afterEach, describe, expect, it, vi } from "vitest";
import { legacyChatIdentityKey } from "../features/chat/chatIdentity";
import { emptySession, useSessionStore } from "../state/sessionStore";
import { composerSendEnabled, startConversation } from "./realtime";

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    createSession: vi.fn(),
    createSessionForInstance: vi.fn()
  };
});

import { createSession } from "./api";

const agents = [
  {
    id: "customer-support",
    version: 1,
    name: "Sam",
    role: "Support",
    description: "",
    voiceAvailable: false,
    language: "en"
  }
];

describe("managed inventory gate", () => {
  afterEach(() => {
    useSessionStore.setState({
      ...emptySession(),
      agents: [],
      chatAgentInstances: [],
      chatAgentInstancesError: null,
      chatAgentInstancesLoading: false,
      newChatIdentityKey: "",
      selectedAgentId: "examiner"
    });
    vi.clearAllMocks();
  });

  it("does not allow send or legacy session create while managed inventory is loading", async () => {
    useSessionStore.setState({
      ...emptySession(),
      agents,
      chatAgentInstances: [],
      chatAgentInstancesError: null,
      chatAgentInstancesLoading: true,
      newChatIdentityKey: "",
      selectedAgentId: "customer-support",
      draft: "Hello"
    });

    expect(composerSendEnabled()).toBe(false);

    const started = await startConversation();
    expect(started).toBe(false);
    expect(createSession).not.toHaveBeenCalled();
  });

  it("enables send only after inventory resolves to a legacy identity", () => {
    useSessionStore.setState({
      ...emptySession(),
      agents,
      chatAgentInstances: [],
      chatAgentInstancesError: null,
      chatAgentInstancesLoading: false,
      newChatIdentityKey: legacyChatIdentityKey("customer-support"),
      selectedAgentId: "customer-support",
      draft: "Hello"
    });

    expect(composerSendEnabled()).toBe(true);
  });
});
