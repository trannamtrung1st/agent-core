import type { ChatAgentInstance } from "../../services/api";

export const MANAGED_CHAT_IDENTITY_PREFIX = "managed:";
export function managedChatIdentityKey(instanceId: string): string { return `${MANAGED_CHAT_IDENTITY_PREFIX}${instanceId}`; }
export function parseChatIdentityKey(key: string): { kind: "managed"; instanceId: string } | null {
  const instanceId = key.startsWith(MANAGED_CHAT_IDENTITY_PREFIX) ? key.slice(MANAGED_CHAT_IDENTITY_PREFIX.length) : "";
  return instanceId ? { kind: "managed", instanceId } : null;
}
export function defaultChatIdentityKey(instances: ChatAgentInstance[]): string {
  return instances[0] ? managedChatIdentityKey(instances[0].instanceId) : "";
}
export function hasChatIdentityOptions(instances: ChatAgentInstance[]): boolean { return instances.length > 0; }
export function isNewChatIdentityReady(input: {
  chatAgentInstancesLoading: boolean; newChatIdentityKey: string; chatAgentInstances: ChatAgentInstance[];
}): boolean {
  const parsed = parseChatIdentityKey(input.newChatIdentityKey);
  return !input.chatAgentInstancesLoading && parsed !== null && input.chatAgentInstances.some(item => item.instanceId === parsed.instanceId);
}
export function shortInstanceId(instanceId: string): string { return instanceId.replace(/-/g, "").toLowerCase().slice(-8); }
export function managedInstancePickerLabel(item: ChatAgentInstance): string {
  return `${item.name} — ${item.role} · v${item.activeVersion} · ${shortInstanceId(item.instanceId)}`;
}
export type NewChatIdentityPresentation = { displayName: string; voiceAvailable: boolean; language: string };
export function resolveNewChatIdentityPresentation(identityKey: string, instances: ChatAgentInstance[]): NewChatIdentityPresentation | null {
  const parsed = parseChatIdentityKey(identityKey);
  const instance = parsed ? instances.find(item => item.instanceId === parsed.instanceId) : null;
  return instance ? { displayName: instance.name, voiceAvailable: instance.voiceAvailable, language: instance.language } : null;
}
