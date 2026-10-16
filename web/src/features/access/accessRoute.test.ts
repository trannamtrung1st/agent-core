import { describe, expect, it } from 'vitest';
import { canonicalAppUrl, parseAppRoute } from '../../app/appRoute';
import { parseAccessRoute } from './accessRoute';
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
});
