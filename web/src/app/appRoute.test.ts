import { beforeEach, describe, expect, it } from 'vitest';
import { canonicalAppUrl, lastChatUrl, parseAppRoute, rememberChatUrl } from './appRoute';
const tenant = '019944af-00d1-7000-8000-000000000001';
const session = '22222222-2222-2222-2222-222222222222';
beforeEach(() => { sessionStorage.clear(); window.history.replaceState(null, '', '/'); });
describe('authenticated app entry routes', () => {
  it('opens Account organization selection from the root', () => {
    expect(parseAppRoute('/')).toEqual({ area: 'access', view: 'account', page: 'organizations' });
    expect(canonicalAppUrl('/')).toBe('/account/organizations');
    expect(canonicalAppUrl('/?token=obsolete&returnTo=/c/old#voice')).toBe('/account/organizations');
  });
  it.each(['admin', 'admin/instances', `admin/instances/${tenant}/identity`, 'admin/connections', 'admin/definitions/examiner', `c/${session}`, 'unknown', 't/invalid'])('keeps retired or malformed /%s outside Chat', path => {
    expect(parseAppRoute('/' + path)).toEqual({ area: 'access', view: 'not-found' });
    const exact = `/${path}?token=obsolete&returnTo=/admin#voice`;
    expect(canonicalAppUrl(exact)).toBe(exact);
  });
  it('accepts only exact canonical tenant Chat paths', () => {
    expect(parseAppRoute(`/t/${tenant}`)).toEqual({ area: 'chat', tenantId: tenant });
    expect(parseAppRoute(`/t/${tenant}/c/${session}`)).toEqual({ area: 'chat', tenantId: tenant });
    expect(parseAppRoute(`/t/${tenant}/c/${session}/other`)).toEqual({ area: 'access', view: 'not-found' });
    expect(parseAppRoute('/system/browser-privacy')).toEqual({ area: 'access', view: 'system', page: 'browser-privacy' });
  });
  it('remembers only same-origin exact tenant conversations and isolates organizations', () => {
    rememberChatUrl(`/c/${session}`); rememberChatUrl('/admin');
    expect(lastChatUrl()).toBe('/account/organizations');
    rememberChatUrl(`/t/${tenant}/c/${session}?source=activity#saved`);
    rememberChatUrl(`/t/${session}/c/${tenant}`);
    rememberChatUrl(`https://example.test/t/${tenant}/c/${tenant}`);
    window.history.replaceState(null, '', `/t/${tenant}/admin/access`);
    expect(lastChatUrl()).toBe(`/t/${tenant}/c/${session}?source=activity#saved`);
    window.history.replaceState(null, '', `/t/${session}`);
    expect(lastChatUrl()).toBe(`/t/${session}/c/${tenant}`);
  });
});
