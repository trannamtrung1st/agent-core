import { IdentityCompletionError, identityRequest, tenantApi } from './identity';

export interface TenantWebhook {
  resourceId: string; displayName: string; eventKey: string; status: 'active' | 'revoked'; revision: number;
  createdAtUtc: string; updatedAtUtc: string; canManage: boolean; securityRevision: number; ingressPath: string;
}
export interface TenantWebhookPage { items: TenantWebhook[]; total: number; page: number; pageSize: number; securityRevision: number; canCreate: boolean }
export interface TenantWebhookToken { source: TenantWebhook; token: string }
export interface TenantWebhookReceipt { eventId: string; sourceEventId: string; sourceRevision: number; occurredAtUtc: string; admittedAtUtc: string; data: Record<string, unknown> }
export interface TenantWebhookReceipts { source: TenantWebhook; items: TenantWebhookReceipt[]; total: number; page: number; pageSize: number }
export const tenantWebhookApi = (tenant: string, suffix = '') => tenantApi(tenant, `/webhooks${suffix}`);
export function requireWebhook(value: TenantWebhook, expectedId?: string): TenantWebhook {
  if (!value || !value.resourceId || expectedId && value.resourceId !== expectedId || !Number.isSafeInteger(value.revision) || value.revision < 1
    || !Number.isSafeInteger(value.securityRevision) || !value.displayName || !value.eventKey || !['active', 'revoked'].includes(value.status) || !value.ingressPath)
    throw new IdentityCompletionError();
  return value;
}
export function requireWebhookToken(value: TenantWebhookToken, expectedId?: string): TenantWebhookToken {
  requireWebhook(value?.source, expectedId); if (!/^[A-Za-z0-9_-]{43}$/.test(value.token)) throw new IdentityCompletionError(); return value;
}
export async function createTenantWebhook(tenant: string, displayName: string, eventKey: string, revision: number, signal: AbortSignal) {
  return requireWebhookToken(await identityRequest<TenantWebhookToken>(tenantWebhookApi(tenant), { method: 'POST', signal, body: { displayName, eventKey, expectedRevision: revision } }));
}
