import { identityRequest, tenantApi, type TenantContext } from './identity';
import type { EmailMonitoring, SystemCredential } from './adminApi';

export type TenantCredential = { tenantId: string; credential: SystemCredential; lastRotatedAt?: string | null };
export type CredentialAccess = { permissions: string[]; revision: number };
export type CredentialPurpose = 'Gmail' | 'Drive' | 'Custom' | 'Registry' | 'ModelInference' | 'General';
export type OAuthSetup = { configured: boolean; synthetic: boolean; hostEnabled: boolean };
export const credentialApi = (tenant: string, id?: string, suffix = '') => tenantApi(tenant, '/credentials' + (id ? `/${encodeURIComponent(id)}` : '') + suffix);
export const freshCredentialAccess = (tenant: string, id: string, signal?: AbortSignal) => identityRequest<CredentialAccess>(credentialApi(tenant, id, '/access'), { signal });
export const freshTenantRevision = async (tenant: string, signal?: AbortSignal) => (await identityRequest<TenantContext>(tenantApi(tenant, '/access/me'), { signal })).revision;
export const credentialPurpose = (item: SystemCredential): CredentialPurpose => item.purpose === 'ModelInference' ? 'ModelInference' : item.driveAccount ? 'Drive' : item.containerRegistry ? 'Registry' : item.emailAccount?.preset ?? 'General';
export const purposeLabel = (purpose: CredentialPurpose) => ({ Gmail: 'Gmail account', Drive: 'Google Drive account', Custom: 'Custom email account', Registry: 'Container registry', ModelInference: 'Model inference API key', General: 'General credential' })[purpose];
export const credentialKindLabel = (kind: string) => ({ Password: 'Password', ApiKey: 'API key', Token: 'Token', Certificate: 'Certificate', PrivateKey: 'Private key', Generic: 'Generic value' })[kind] ?? 'Credential';
export function checkedAuthorizationUrl(value: string): string {
  const url = new URL(value, window.location.origin);
  if (!url.username && !url.password && !url.hash && (url.origin === window.location.origin && url.pathname === '/api/v2/integrations/tenant-google/oauth/synthetic'
    || url.origin === 'https://accounts.google.com' && url.pathname === '/o/oauth2/v2/auth')) return url.href;
  throw new Error('Unexpected authorization destination. Reload the saved credential before connecting.');
}

export type TenantEmailMonitoring = Omit<EmailMonitoring, 'subscribedInstances'> & {
  securityRevision: number; credentialRevision: number; canManage: boolean; canEnable: boolean; canCheck: boolean;
};
export const tenantEmailMonitoring = (tenant: string, id: string) => credentialApi(tenant, id, '/monitoring');
