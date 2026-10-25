import { draftPages, type DraftPage } from '../admin/adminNavigation';
export type ModelAdminPage = 'connections' | 'models' | 'defaults';
export type AccessPage = 'my-profile' | 'governance' | 'access' | 'members' | 'roles' | 'audit' | 'definitions' | 'credentials' | 'events' | 'sandboxes' | 'approvals' | 'agents' | 'spaces' | 'secrets' | 'ai-models';
export type TenantInstancePage = 'memory' | 'profile' | 'version' | 'skills' | 'resources' | 'state' | 'state-isolation' | 'settings' | 'execution-budgets' | 'self-management' | 'unattended-model' | 'sandbox' | 'access' | 'connections' | 'lifecycle' | 'activity-sessions' | 'activity-runs' | 'automations';
export type TenantSpacePage = 'overview' | 'members' | 'workspace' | 'environments' | 'access';
export type AccessRoute = { area: 'access' } & (
  | { view: 'sign-in' }
  | { view: 'system'; page: 'browser-privacy' | 'audit' | 'organizations' | 'accounts' | 'operators' | 'model-presets' }
  | { view: 'account'; page: 'organizations' | 'profile' }
  | { view: 'tenant'; tenantId: string; page: AccessPage; modelPage?: ModelAdminPage; definitionId?: string; draftId?: string; draftPage?: DraftPage; requestId?: string; instanceId?: string; instancePage?: TenantInstancePage; spaceId?: string; spacePage?: TenantSpacePage }
  | { view: 'invitation'; invitationId: string }
  | { view: 'not-found' });
const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
export function parseAccessRoute(path: string): AccessRoute | null {
  const system = /^\/system\/(browser-privacy|audit|organizations|accounts|operators|model-presets)\/?$/i.exec(path);
  if (system) return { area: 'access', view: 'system', page: system[1].toLowerCase() as 'browser-privacy' | 'audit' | 'organizations' | 'accounts' | 'operators' | 'model-presets' };
  if (path === '/sign-in') return { area: 'access', view: 'sign-in' };
  if (path === '/account' || path === '/account/organizations') return { area: 'access', view: 'account', page: 'organizations' };
  if (path === '/account/profile') return { area: 'access', view: 'account', page: 'profile' };
  const ownProfile = new RegExp(`^/t/(${uuid})/profile/?$`, 'i').exec(path);
  if (ownProfile) return { area: 'access', view: 'tenant', tenantId: ownProfile[1].toLowerCase(), page: 'my-profile' };
  const models = new RegExp(`^/t/(${uuid})/integrations/ai-models(?:/(connections|models|defaults))?/?$`, 'i').exec(path);
  if (models) return { area: 'access', view: 'tenant', tenantId: models[1].toLowerCase(), page: 'ai-models', modelPage: (models[2]?.toLowerCase() ?? 'connections') as ModelAdminPage };
  const spaces = new RegExp(`^/t/(${uuid})/admin/spaces(?:/(${uuid})(?:/(overview|members|workspace|environments|access))?)?/?$`, 'i').exec(path);
  if (spaces) return { area: 'access', view: 'tenant', tenantId: spaces[1].toLowerCase(), page: 'spaces', spaceId: spaces[2]?.toLowerCase(), spacePage: spaces[3]?.toLowerCase() as TenantSpacePage | undefined };
  const secrets = new RegExp(`^/t/(${uuid})/integrations/secrets/?$`, 'i').exec(path);
  if (secrets) return { area: 'access', view: 'tenant', tenantId: secrets[1].toLowerCase(), page: 'secrets' };
  const credentials = new RegExp(`^/t/(${uuid})/integrations/credentials/?$`, 'i').exec(path);
  if (credentials) return { area: 'access', view: 'tenant', tenantId: credentials[1].toLowerCase(), page: 'credentials' };
  const events = new RegExp(`^/t/(${uuid})/integrations/events/?$`, 'i').exec(path);
  if (events) return { area: 'access', view: 'tenant', tenantId: events[1].toLowerCase(), page: 'events' };
  const sandboxes = new RegExp(`^/t/(${uuid})/integrations/sandboxes/?$`, 'i').exec(path);
  if (sandboxes) return { area: 'access', view: 'tenant', tenantId: sandboxes[1].toLowerCase(), page: 'sandboxes' };
  const activity = new RegExp(`^/t/(${uuid})/admin/agents/(${uuid})/activity(?:/(sessions|runs))?/?$`, 'i').exec(path);
  if (activity) return { area: 'access', view: 'tenant', tenantId: activity[1].toLowerCase(), page: 'agents', instanceId: activity[2].toLowerCase(), instancePage: activity[3]?.toLowerCase() === 'runs' ? 'activity-runs' : 'activity-sessions' };
  const agent = new RegExp(`^/t/(${uuid})/admin/agents(?:/(${uuid})/(memory|profile|version|skills|resources|state|state-isolation|settings|sandbox|execution-budgets|self-management|unattended-model|access|connections|lifecycle|automations))?/?$`, 'i').exec(path);
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
export const tenantAccessPath = (tenantId: string, page: AccessPage = 'access') => `/t/${encodeURIComponent(tenantId)}/${page === 'my-profile' ? 'profile' : page === 'ai-models' ? 'integrations/ai-models/connections' : page === 'secrets' || page === 'credentials' || page === 'sandboxes' || page === 'events' ? `integrations/${page}` : `admin/${page}`}`;
export const tenantCredentialPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'credentials') + (id ? `?credentialId=${encodeURIComponent(id)}` : '');
export const tenantApprovalPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'approvals') + (id ? `/${encodeURIComponent(id)}` : '');
export const tenantAgentPath = (tenant: string, id: string, page: TenantInstancePage = 'access') => `${tenantAccessPath(tenant, 'agents')}/${encodeURIComponent(id)}/${page.startsWith('activity-') ? `activity/${page.slice(9)}` : page}`;
export const tenantDefinitionPath = (tenant: string, id: string) => `${tenantAccessPath(tenant, 'definitions')}/${encodeURIComponent(id)}`;
export const tenantDraftPath = (tenant: string, id: string, draft: string, page: DraftPage = 'overview') => `${tenantDefinitionPath(tenant, id)}/drafts/${encodeURIComponent(draft)}/${draftPages.find(p => p.id === page)!.path}`;

export const tenantSandboxPath = (tenant: string, id?: string) => tenantAccessPath(tenant, 'sandboxes') + (id ? `?profileId=${encodeURIComponent(id)}` : '');

export const tenantModelsPath = (tenant: string, page: ModelAdminPage = 'connections') => `/t/${encodeURIComponent(tenant)}/integrations/ai-models/${page}`;
export const tenantSpacePath = (tenant: string, id?: string, page: TenantSpacePage = 'overview') => tenantAccessPath(tenant, 'spaces') + (id ? `/${encodeURIComponent(id)}/${page}` : '');
