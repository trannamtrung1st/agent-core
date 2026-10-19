import { draftPages, type DraftPage } from '../admin/adminNavigation';
export type AccessPage = 'governance' | 'access' | 'members' | 'roles' | 'audit' | 'definitions' | 'credentials' | 'sandboxes' | 'approvals' | 'agents';
export type TenantInstancePage = 'profile' | 'version' | 'skills' | 'resources' | 'settings' | 'execution-budgets' | 'self-management' | 'unattended-model' | 'sandbox' | 'workspace' | 'access' | 'connections' | 'lifecycle';
export type AccessRoute = { area: 'access' } & (
  | { view: 'sign-in' }
  | { view: 'system'; page: 'browser-privacy' }
  | { view: 'account'; page: 'organizations' | 'profile' }
  | { view: 'tenant'; tenantId: string; page: AccessPage; definitionId?: string; draftId?: string; draftPage?: DraftPage; requestId?: string; instanceId?: string; instancePage?: TenantInstancePage }
  | { view: 'invitation'; invitationId: string }
  | { view: 'not-found' });
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function parseAccessRoute(path: string): AccessRoute | null {
  if (/^\/system\/browser-privacy\/?$/i.test(path)) return { area: 'access', view: 'system', page: 'browser-privacy' };
  if (path === '/sign-in') return { area: 'access', view: 'sign-in' };
  if (path === '/account' || path === '/account/organizations') return { area: 'access', view: 'account', page: 'organizations' };
  if (path === '/account/profile') return { area: 'access', view: 'account', page: 'profile' };
  const credentials = new RegExp(`^/t/(${uuid})/integrations/credentials/?$`, 'i').exec(path);
  if (credentials) return { area: 'access', view: 'tenant', tenantId: credentials[1].toLowerCase(), page: 'credentials' };
  const sandboxes = new RegExp(`^/t/(${uuid})/integrations/sandboxes/?$`, 'i').exec(path);
  if (sandboxes) return { area: 'access', view: 'tenant', tenantId: sandboxes[1].toLowerCase(), page: 'sandboxes' };
  const agent = new RegExp(`^/t/(${uuid})/admin/agents(?:/(${uuid})/(profile|version|skills|resources|settings|sandbox|workspace|execution-budgets|self-management|unattended-model|access|connections|lifecycle))?/?$`, 'i').exec(path);
  if (agent) return { area: 'access', view: 'tenant', tenantId: agent[1].toLowerCase(), page: 'agents', instanceId: agent[2]?.toLowerCase(), instancePage: agent[3]?.toLowerCase() as TenantInstancePage | undefined };
  const approval = new RegExp(`^/t/(${uuid})/admin/approvals(?:/(${uuid}))?/?$`, 'i').exec(path);
  if (approval) return { area: 'access', view: 'tenant', tenantId: approval[1].toLowerCase(), page: 'approvals', requestId: approval[2]?.toLowerCase() };
  const definition = new RegExp(`^/t/(${uuid})/admin/definitions(?:/(${uuid})(?:/drafts/(${uuid})(?:/([a-z-]+))?)?)?/?$`, 'i').exec(path);
  if (definition) {
    const section = definition[4] ? draftPages.find(p => p.path === definition[4])?.id : undefined;
    if (definition[4] && !section) return { area: 'access', view: 'not-found' };
    return { area: 'access', view: 'tenant', page: 'definitions', tenantId: definition[1].toLowerCase(), definitionId: definition[2]?.toLowerCase(), draftId: definition[3]?.toLowerCase(), draftPage: section ?? 'overview' };
  }
  const tenant = new RegExp(`^/t/(${uuid})/admin/(access|members|roles|audit|governance)/?$`, 'i').exec(path);
  if (tenant) return { area: 'access', view: 'tenant', tenantId: tenant[1].toLowerCase(), page: tenant[2].toLowerCase() as AccessPage };
  const invitation = new RegExp(`^/invitations/(${uuid})$`, 'i').exec(path);
  if (invitation) return { area: 'access', view: 'invitation', invitationId: invitation[1].toLowerCase() };
  return /^\/(account|system|t|invitations)(\/|$)/i.test(path) ? { area: 'access', view: 'not-found' } : null;
}
export const tenantAccessPath = (tenantId: string, page: AccessPage = 'access') => `/t/${encodeURIComponent(tenantId)}/${page === 'credentials' || page === 'sandboxes' ? `integrations/${page}` : `admin/${page}`}`;
export const tenantCredentialPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'credentials') + (id ? `?credentialId=${encodeURIComponent(id)}` : '');
export const tenantApprovalPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'approvals') + (id ? `/${encodeURIComponent(id)}` : '');
export const tenantAgentPath = (tenant: string, id: string, page: TenantInstancePage = 'access') => `${tenantAccessPath(tenant, 'agents')}/${encodeURIComponent(id)}/${page}`;
export const tenantDefinitionPath = (tenant: string, id: string) => `${tenantAccessPath(tenant, 'definitions')}/${encodeURIComponent(id)}`;
export const tenantDraftPath = (tenant: string, id: string, draft: string, page: DraftPage = 'overview') => `${tenantDefinitionPath(tenant, id)}/drafts/${encodeURIComponent(draft)}/${draftPages.find(p => p.id === page)!.path}`;

export const tenantSandboxPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'sandboxes') + (id ? `?profileId=${encodeURIComponent(id)}` : '');
