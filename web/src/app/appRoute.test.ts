import { describe, expect, it } from 'vitest';
import { adminDefinitionPath, adminDraftPath, adminHomePath, adminInstancePath, canonicalAppUrl, lastChatUrl, parseAppRoute, rememberChatUrl } from './appRoute';
import { draftPages, instancePages } from '../features/admin/adminNavigation';
const id = '019944af-00d1-7000-8000-000000000001';
const base = `/admin/instances/${id}`;
describe('Admin canonical routes', () => {
  it('keeps malformed Admin URLs in Admin and defaults inventories and owners deterministically', () => {
    expect(parseAppRoute('/')).toEqual({ area: 'chat' });
    expect(canonicalAppUrl('/admin')).toBe('/admin/instances');
    expect(canonicalAppUrl('/admin/connections')).toBe('/admin/connections/credentials');
    expect(canonicalAppUrl('/admin/definitions/examiner')).toBe('/admin/definitions/examiner/versions');
    expect(canonicalAppUrl(base)).toBe(`${base}/overview`);
    for (const url of ['/admin/nope', '/admin/instances/invalid', '/admin/definitions/%E0%A4%A', `${base}/automation/nope`, `${base}/continuity/thoughts`]) expect(parseAppRoute(url)).toEqual({ area: 'admin', view: 'not-found' });
  });
  it.each([
    ['identity', 'profile'], ['identity/profile', 'profile'], ['identity/settings', 'instructions'], ['identity/workspace', 'workspace'],
    ['skills/skills', 'skills'], ['skills/resources', 'resources'], ['continuity', 'continuity/memory'],
    ['automation', 'automations'], ['automation/automations', 'automations'], ['automation/controls', 'unattended#execution-defaults'],
    ['automation/harness', 'self-management'], ['activity', 'activity/sessions'], ['runs', 'activity/runs'], ['access', 'capabilities'],
  ])('migrates %s once to %s with meaningful query and no secret forwarding', (old, next) => {
    const expected = `${base}/${next.split('#')[0]}?run=${id}&automation=${id}${next.includes('#') ? `#${next.split('#')[1]}` : '#focus'}`;
    expect(canonicalAppUrl(`${base}/${old}?run=${id}&automation=${id}&token=secret#focus`)).toBe(expected);
    expect(canonicalAppUrl(expected)).toBe(expected);
  });
  it('round-trips every canonical Instance and exact draft page', () => {
    for (const p of instancePages) expect(parseAppRoute(adminInstancePath(id, p.id))).toEqual({ area: 'admin', view: 'instance', instanceId: id, page: p.id });
    for (const p of draftPages) expect(parseAppRoute(adminDraftPath('field guide', id, p.id))).toEqual({ area: 'admin', view: 'draft', definitionId: 'field guide', draftId: id, page: p.id });
    expect(adminDefinitionPath('examiner')).toBe('/admin/definitions/examiner/versions');
    expect(adminHomePath('credentials')).toBe('/admin/connections/credentials');
    expect(adminHomePath('browser-privacy')).toBe('/admin/platform/browser-privacy');
  });
  it('relocates legacy combined-page anchors to their focused owner', () => {
    expect(canonicalAppUrl(`${base}/behavior#voice`)).toBe(`${base}/voice#voice`);
    expect(canonicalAppUrl(`${base}/models#providerPreferences`)).toBe(`${base}/providers#providerPreferences`);
    expect(canonicalAppUrl(`${base}/access#credentials`)).toBe(`${base}/credentials#credentials`);
    expect(canonicalAppUrl(`/admin/definitions/examiner/drafts/${id}/policies#selfManagement`)).toBe(`/admin/definitions/examiner/drafts/${id}/self-management#selfManagement`);
  });
  it('remembers the exact saved conversation and rejects Admin as chat memory', () => {
    sessionStorage.clear(); rememberChatUrl('/admin'); expect(lastChatUrl()).toBe('/');
    rememberChatUrl(`/c/${id}?returnTo=activity#source`); expect(lastChatUrl()).toBe(`/c/${id}?returnTo=activity#source`);
  });
  it('retains an exact Session source during migration and resolves legacy builder groups', () => {
    expect(canonicalAppUrl(`${base}/activity?session=${id}&q=source&password=secret`)).toBe(`${base}/activity/sessions?session=${id}&q=source`);
    expect(adminInstancePath(id, 'skills', 'resources')).toBe(`${base}/resources`);
    expect(adminInstancePath(id, 'automation', 'controls')).toBe(`${base}/automations`);
  });
});

it('redirects saved Execution sandbox anchors to the dedicated Instance Sandbox owner', () => {
  const id='11111111-1111-1111-1111-111111111111';
  expect(canonicalAppUrl(`/admin/instances/${id}/execution#sandboxPolicy`)).toBe(`/admin/instances/${id}/sandbox#sandboxPolicy`);
});

it('keeps canonical tenant Chat routes ahead of the administration router and separates remembered conversations', () => {
  const second = '22222222-2222-2222-2222-222222222222';
  sessionStorage.clear();
  expect(parseAppRoute(`/t/${id}`)).toEqual({ area: 'chat', tenantId: id });
  expect(parseAppRoute(`/t/${id}/c/${second}`)).toEqual({ area: 'chat', tenantId: id });
  rememberChatUrl(`/t/${id}/c/${second}`); rememberChatUrl(`/t/${second}/c/${id}`);
  window.history.replaceState(null, '', `/t/${id}/admin/access`);
  expect(lastChatUrl()).toBe(`/t/${id}/c/${second}`);
  window.history.replaceState(null, '', `/t/${second}`);
  expect(lastChatUrl()).toBe(`/t/${second}/c/${id}`);
  window.history.replaceState(null, '', '/'); expect(lastChatUrl()).toBe('/');
});

 it('relocates Definition Sandbox anchors and shares navigation placement with Instances', () => {
  const draft = `/admin/definitions/examiner/drafts/${id}`;
  expect(canonicalAppUrl(`${draft}/capabilities#sandboxPolicy`)).toBe(`${draft}/sandbox#sandboxPolicy`);
  for (const pages of [draftPages, instancePages]) {
    const execution = pages.findIndex(page => page.id === 'execution');
    expect(pages.slice(execution, execution + 3).map(page => page.id)).toEqual(['execution', 'state-isolation', 'sandbox']);
  }
});
