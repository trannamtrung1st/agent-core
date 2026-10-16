import { draftPages, type DraftPage } from '../admin/adminNavigation';
export type AccessPage = 'access' | 'members' | 'roles' | 'audit' | 'definitions' | 'credentials';
export type AccessRoute = { area: 'access' } & (
  | { view: 'sign-in' }
  | { view: 'account'; page: 'organizations' | 'profile' }
  | { view: 'tenant'; tenantId: string; page: AccessPage; definitionId?: string; draftId?: string; draftPage?: DraftPage }
  | { view: 'invitation'; invitationId: string }
  | { view: 'not-found' });
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function parseAccessRoute(path: string): AccessRoute | null {
  if (path === '/sign-in') return { area: 'access', view: 'sign-in' };
  if (path === '/account' || path === '/account/organizations') return { area: 'access', view: 'account', page: 'organizations' };
  if (path === '/account/profile') return { area: 'access', view: 'account', page: 'profile' };
  const credentials = new RegExp(`^/t/(${uuid})/integrations/credentials/?$`, 'i').exec(path);
  if (credentials) return { area: 'access', view: 'tenant', tenantId: credentials[1].toLowerCase(), page: 'credentials' };
  const definition = new RegExp(`^/t/(${uuid})/admin/definitions(?:/(${uuid})(?:/drafts/(${uuid})(?:/([a-z-]+))?)?)?/?$`, 'i').exec(path);
  if (definition) {
    const section = definition[4] ? draftPages.find(p => p.path === definition[4])?.id : undefined;
    if (definition[4] && !section) return { area: 'access', view: 'not-found' };
    return { area: 'access', view: 'tenant', page: 'definitions', tenantId: definition[1].toLowerCase(), definitionId: definition[2]?.toLowerCase(), draftId: definition[3]?.toLowerCase(), draftPage: section ?? 'overview' };
  }
  const tenant = new RegExp(`^/t/(${uuid})/admin/(access|members|roles|audit)/?$`, 'i').exec(path);
  if (tenant) return { area: 'access', view: 'tenant', tenantId: tenant[1].toLowerCase(), page: tenant[2].toLowerCase() as AccessPage };
  const invitation = new RegExp(`^/invitations/(${uuid})$`, 'i').exec(path);
  if (invitation) return { area: 'access', view: 'invitation', invitationId: invitation[1].toLowerCase() };
  return /^\/(account|t|invitations)(\/|$)/i.test(path) ? { area: 'access', view: 'not-found' } : null;
}
export const tenantAccessPath = (tenantId: string, page: AccessPage = 'access') => `/t/${encodeURIComponent(tenantId)}/${page === 'credentials' ? 'integrations/credentials' : `admin/${page}`}`;
export const tenantCredentialPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'credentials') + (id ? `?credentialId=${encodeURIComponent(id)}` : '');
export const tenantDefinitionPath = (tenant: string, id: string) => `${tenantAccessPath(tenant, 'definitions')}/${encodeURIComponent(id)}`;
export const tenantDraftPath = (tenant: string, id: string, draft: string, page: DraftPage = 'overview') => `${tenantDefinitionPath(tenant, id)}/drafts/${encodeURIComponent(draft)}/${draftPages.find(p => p.id === page)!.path}`;
