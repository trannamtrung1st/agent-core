import { describe, expect, it } from 'vitest';
import { canonicalAppUrl, parseAppRoute } from '../../app/appRoute';
import { parseAccessRoute, tenantDefinitionPath, tenantDraftPath } from './accessRoute';
const tenant = '11111111-1111-4111-8111-111111111111';
describe('canonical access navigation', () => {
  it('keeps valid identity and tenant access routes out of Chat', () => {
    expect(parseAppRoute('/sign-in')).toEqual({ area: 'access', view: 'sign-in' });
    expect(parseAppRoute(`/t/${tenant}/admin/members`)).toEqual({ area: 'access', view: 'tenant', tenantId: tenant, page: 'members' });
    expect(canonicalAppUrl(`/t/${tenant}/admin/roles`)).toBe(`/t/${tenant}/admin/roles`);
  });
  it('fails closed for malformed tenant routes and unsupported future screens', () => {
    for (const route of ['/t/invalid/admin/members', `/t/${tenant}/admin/approvals`, '/account/unknown']) expect(parseAccessRoute(route)?.view).toBe('not-found');
    expect(parseAccessRoute('/c/session')).toBeNull();
    expect(parseAppRoute('/admin/instances').area).toBe('admin');
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
