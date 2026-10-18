import { describe, expect, it } from 'vitest';
import { canonicalAppUrl, parseAppRoute } from '../../app/appRoute';
import { parseAccessRoute, tenantDefinitionPath, tenantDraftPath, tenantCredentialPath, tenantSandboxPath, tenantApprovalPath, tenantAgentPath } from './accessRoute';
const tenant = '11111111-1111-4111-8111-111111111111';
describe('canonical access navigation', () => {
  it('pins agent Access to the exact organization and rejects incomplete or extra subroutes', () => {
    expect(parseAppRoute(`/t/${tenant}/admin/agents`)).toMatchObject({ area: 'access', view: 'tenant', page: 'agents', tenantId: tenant });
    expect(parseAccessRoute(tenantAgentPath(tenant, tenant))).toMatchObject({ page: 'agents', tenantId: tenant, instanceId: tenant });
    expect(canonicalAppUrl(tenantAgentPath(tenant, tenant))).toBe(tenantAgentPath(tenant, tenant));
    expect(parseAccessRoute(tenantAgentPath(tenant, tenant, 'connections'))).toMatchObject({ page: 'agents', tenantId: tenant, instanceId: tenant, instancePage: 'connections' });
    expect(canonicalAppUrl(tenantAgentPath(tenant, tenant, 'connections'))).toBe(tenantAgentPath(tenant, tenant, 'connections'));
    expect(parseAccessRoute(tenantAgentPath(tenant, tenant, 'connections') + '/other')?.view).toBe('not-found');
    for (const instancePage of ['profile', 'version', 'skills', 'resources', 'settings', 'execution-budgets', 'self-management', 'unattended-model'] as const) {
      const path = tenantAgentPath(tenant, tenant, instancePage);
      expect(parseAccessRoute(path)).toMatchObject({ tenantId: tenant, instanceId: tenant, instancePage });
      expect(canonicalAppUrl(path)).toBe(path);
      expect(parseAccessRoute(path + '/extra')?.view).toBe('not-found');
    }
    for (const path of [`/t/${tenant}/admin/agents/${tenant}`, `/t/${tenant}/admin/agents/invalid/access`, `${tenantAgentPath(tenant, tenant)}/other`]) expect(parseAccessRoute(path)?.view).toBe('not-found');
  });
  it('opens the OAuth return destination as a tenant Credential page and preserves its exact item query', () => {
    const path = tenantCredentialPath(tenant, tenant);
    expect(parseAppRoute(path.split('?')[0])).toMatchObject({ area: 'access', view: 'tenant', tenantId: tenant, page: 'credentials' });
    expect(canonicalAppUrl(path + '&connection=connected')).toBe(path + '&connection=connected');
    expect(parseAccessRoute(`/t/${tenant}/integrations/credentials/foreign`)).toMatchObject({ view: 'not-found' });
  });
  it('keeps valid identity and tenant access routes out of Chat', () => {
    expect(parseAppRoute('/sign-in')).toEqual({ area: 'access', view: 'sign-in' });
    expect(parseAppRoute(`/t/${tenant}/admin/members`)).toEqual({ area: 'access', view: 'tenant', tenantId: tenant, page: 'members' });
    expect(canonicalAppUrl(`/t/${tenant}/admin/roles`)).toBe(`/t/${tenant}/admin/roles`);
  });
  it('fails closed for malformed tenant routes and unsupported future screens', () => {
    for (const route of ['/t/invalid/admin/members', `/t/${tenant}/admin/approvals/invalid`, '/account/unknown']) expect(parseAccessRoute(route)?.view).toBe('not-found');
    expect(parseAccessRoute('/c/session')).toBeNull();
    expect(parseAppRoute('/admin/instances').area).toBe('admin');
  });
  it('opens the exact organization Sandbox catalog and retains its item query', () => {
    const path = tenantSandboxPath(tenant, 'p-exact-profile');
    expect(parseAccessRoute(path.split('?')[0])).toMatchObject({ tenantId: tenant, page: 'sandboxes' });
    expect(canonicalAppUrl(path)).toBe(path);
    expect(parseAccessRoute(`/t/${tenant}/integrations/sandboxes/foreign`)?.view).toBe('not-found');
  });
  it('opens exact organization approval deep links', () => {
    expect(parseAccessRoute(tenantApprovalPath(tenant, tenant))).toMatchObject({ view: 'tenant', tenantId: tenant, page: 'approvals', requestId: tenant });
    expect(canonicalAppUrl(tenantApprovalPath(tenant, tenant))).toBe(tenantApprovalPath(tenant, tenant));
  });
  it('retains exact tenant, Definition and draft ownership in deep links', () => {
    const definition = '22222222-2222-4222-8222-222222222222';
    const draft = '33333333-3333-4333-8333-333333333333';
    expect(parseAccessRoute(tenantDefinitionPath(tenant, definition))).toMatchObject({ view: 'tenant', tenantId: tenant, page: 'definitions', definitionId: definition });
    expect(parseAccessRoute(tenantDraftPath(tenant, definition, draft, 'instructions'))).toMatchObject({ view: 'tenant', tenantId: tenant, definitionId: definition, draftId: draft, draftPage: 'instructions' });
    expect(parseAccessRoute(`/t/${tenant}/admin/definitions/${definition}/drafts/${draft}`)).toMatchObject({ draftPage: 'overview' });
    for (const path of [`/t/${tenant}/admin/definitions/not-a-definition`, `${tenantDraftPath(tenant, definition, draft)}/extra`, `/t/${tenant}/admin/definitions/${definition}/drafts/${draft}/unknown`]) expect(parseAccessRoute(path)?.view).toBe('not-found');
  });
});
