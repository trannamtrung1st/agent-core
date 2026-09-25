import type { AgentDescriptor } from "../../services/api";
import type { ChatAgentInstance } from "../../services/api";

export const MANAGED_CHAT_IDENTITY_PREFIX = "managed:";
export const LEGACY_CHAT_IDENTITY_PREFIX = "legacy:";

export function managedChatIdentityKey(instanceId: string): string {
  return `${MANAGED_CHAT_IDENTITY_PREFIX}${instanceId}`;
}

export function legacyChatIdentityKey(agentId: string): string {
  return `${LEGACY_CHAT_IDENTITY_PREFIX}${agentId}`;
}

export function parseChatIdentityKey(key: string): { kind: "managed"; instanceId: string } | { kind: "legacy"; agentId: string } | null {
  if (key.startsWith(MANAGED_CHAT_IDENTITY_PREFIX)) {
    const instanceId = key.slice(MANAGED_CHAT_IDENTITY_PREFIX.length);
    return instanceId ? { kind: "managed", instanceId } : null;
  }
  if (key.startsWith(LEGACY_CHAT_IDENTITY_PREFIX)) {
    const agentId = key.slice(LEGACY_CHAT_IDENTITY_PREFIX.length);
    return agentId ? { kind: "legacy", agentId } : null;
  }
  return null;
}

export function defaultChatIdentityKey(
  managedInstances: ChatAgentInstance[],
  agents: AgentDescriptor[]
): string {
  if (managedInstances.length > 0) {
    return managedChatIdentityKey(managedInstances[0].instanceId);
  }
  if (agents.length > 0) {
    return legacyChatIdentityKey(agents[0].id);
  }
  return "";
}

export function hasChatIdentityOptions(
  managedInstances: ChatAgentInstance[],
  agents: AgentDescriptor[]
): boolean {
  return managedInstances.length > 0 || agents.length > 0;
}

export function isNewChatIdentityReady(input: {
  chatAgentInstancesLoading: boolean;
  newChatIdentityKey: string;
  chatAgentInstances: ChatAgentInstance[];
  agents: AgentDescriptor[];
}): boolean {
  if (input.chatAgentInstancesLoading) {
    return false;
  }
  const parsed = parseChatIdentityKey(input.newChatIdentityKey);
  if (!parsed) {
    return false;
  }
  if (parsed.kind === "managed") {
    return input.chatAgentInstances.some((item) => item.instanceId === parsed.instanceId);
  }
  return input.agents.some((item) => item.id === parsed.agentId);
}

export function shortInstanceId(instanceId: string): string {
  const normalized = instanceId.replace(/-/g, "").toLowerCase();
  return normalized.slice(-8);
}

export function managedInstancePickerLabel(item: ChatAgentInstance): string {
  return `${item.name} — ${item.role} · v${item.activeVersion} · ${shortInstanceId(item.instanceId)}`;
}

export type NewChatIdentityPresentation = {
  displayName: string;
  voiceAvailable: boolean;
  language: string;
};

export function resolveNewChatIdentityPresentation(
  identityKey: string,
  managedInstances: ChatAgentInstance[],
  agents: AgentDescriptor[]
): NewChatIdentityPresentation | null {
  const parsed = parseChatIdentityKey(identityKey);
  if (parsed?.kind === "managed") {
    const managed = managedInstances.find((item) => item.instanceId === parsed.instanceId);
    if (!managed) {
      return null;
    }
    return {
      displayName: managed.name,
      voiceAvailable: managed.voiceAvailable,
      language: managed.language
    };
  }
  if (parsed?.kind === "legacy") {
    const agent = agents.find((item) => item.id === parsed.agentId);
    if (!agent) {
      return null;
    }
    return {
      displayName: agent.name,
      voiceAvailable: agent.voiceAvailable,
      language: agent.language ?? "en"
    };
  }
  return null;
}
