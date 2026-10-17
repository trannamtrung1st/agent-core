import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantAgentSkills } from './TenantAgentSkills';
import type { TenantSkillEditor } from '../../services/tenantAgentConfiguration';

const context = { tenant: { tenantId: 'tenant', name: 'Regional organization', slug: 'regional' }, userId: 'u', principalId: 'p', permissions: [], roles: [], revision: 7 } as TenantContext;
const skill = () => ({ id: 'review', key: 'instance:review', origin: 'Instance', name: 'Local review', description: 'Review totals.', procedure: 'Saved procedure.', projection: 'OnDemand', enabled: true, requiredCapabilities: ['chat.respond'], revision: 2, enabledOverride: null, definitionVersion: null, sourceDefinitionId: null, sourceDefinitionVersion: null, sourceDefinitionSkillId: null, missingCapabilities: [] } as const);
const initial = () => ({ agent: { principalId: 'principal', systemIdentityId: 'identity', instance: { instanceId: 'agent', definitionId: 'source', persona: { name: 'Alex', role: 'Guide', description: 'Regional guide', tone: 'Calm' }, revision: 4, personaRevision: 1, activeVersion: 1, lifecycle: 'Active' } }, items: [{ skill: { ...skill(), requiredCapabilities: ['chat.respond'], missingCapabilities: [] }, canManage: true, canCustomize: false }], total: 1, page: 0, pageSize: 10, securityRevision: 7, canCreate: true, allReadable: true } as TenantSkillEditor);
const fetchMock = vi.fn<typeof fetch>(); let current: TenantSkillEditor; let writeStatus = 200, readStatus = 200; let malformed = false, detailDenied = false, deferred = false;
let resolveDetail: ((response: Response) => void) | undefined;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => {
  resetIdentityCsrf(); current = initial(); writeStatus = readStatus = 200; malformed = detailDenied = deferred = false; resolveDetail = undefined; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'POST') {
      if (writeStatus !== 200) return response({ reasonCode: 'stale_revision' }, writeStatus);
      const body = JSON.parse(String(options.body)); current = structuredClone(current); current.securityRevision++; current.agent.instance.revision++;
      if (body.input) current.items[0].skill = { ...current.items[0].skill, ...body.input, revision: 3 };
      return malformed ? new Response('{', { status: 200, headers: { 'Content-Type': 'application/json' } }) : response({ changed: true });
    }
    if (url.includes('/skills/instance%3Areview/editor')) {
      if (deferred) return new Promise(resolve => { resolveDetail = resolve; });
      return detailDenied ? response({ reasonCode: 'permission_denied' }, 403) : response({ agent: current.agent, item: current.items[0], securityRevision: current.securityRevision });
    }
    if (url.includes('/skills/editor')) return readStatus === 200 ? response(current) : response({ reasonCode: readStatus === 403 ? 'permission_denied' : 'unavailable' }, readStatus);
    throw new Error('Unexpected ' + url);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
async function open() { render(<App><TenantAgentSkills context={context} id="agent" /></App>); await screen.findByText('Local review'); }
async function edit() { fireEvent.click(screen.getByRole('button', { name: 'Edit Skill' })); await screen.findByDisplayValue('Saved procedure.'); }

it('updates the exact local Skill with independent detail witnesses and retires a saved draft', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Reviewed new procedure.' } }); fireEvent.click(screen.getByRole('button', { name: 'Save Skill' }));
  await waitFor(() => expect(writes()).toHaveLength(1)); const body = JSON.parse(String(writes()[0][1]?.body));
  expect(body).toMatchObject({ operation: 'update', key: 'instance:review', expectedRevision: 7, expectedInstanceRevision: 4, expectedSkillRevision: 2, input: { procedure: 'Reviewed new procedure.', projection: 'OnDemand', requiredCapabilities: ['chat.respond'] } });
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Save Skill' })).not.toBeInTheDocument()); expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith('/api/v2/'))).toBe(true);
});
it('retains a stale draft, blocks replay and refreshes only its exact item before explicit retry', async () => {
  writeStatus = 409; await open(); await edit(); fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Retained draft.' } }); fireEvent.click(screen.getByRole('button', { name: 'Save Skill' })); await screen.findByText(/Access has changed since/);
  expect(screen.getByRole('button', { name: 'Save Skill' })).toBeDisabled(); expect(screen.getByDisplayValue('Retained draft.')).toBeInTheDocument();
  current.securityRevision = 11; current.agent.instance.revision = 8; current.items[0].skill.revision = 5; writeStatus = 200;
  fireEvent.click(screen.getByRole('button', { name: 'Reload latest Skill' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Save Skill' })).toBeEnabled()); expect(screen.getByDisplayValue('Retained draft.')).toBeInTheDocument(); expect(writes()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Save Skill' })); await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 11, expectedInstanceRevision: 8, expectedSkillRevision: 5 });
});
it('retires acknowledged malformed writes and recovers parent read failure without replay', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Saved despite unreadable body.' } }); malformed = true; readStatus = 503;
  fireEvent.click(screen.getByRole('button', { name: 'Save Skill' })); await screen.findByText('Skill change was saved'); expect(writes()).toHaveLength(1); expect(screen.queryByRole('button', { name: 'Save Skill' })).not.toBeInTheDocument();
  readStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload Skills' })); await screen.findByText('Local review'); expect(writes()).toHaveLength(1);
});
it('preserves saved values on explicit discard and guards dirty drawer cancellation', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Procedure'), { target: { value: 'Unsaved.' } }); fireEvent.click(screen.getByRole('button', { name: 'Cancel editing' }));
  const keep = await screen.findByRole('button', { name: 'Keep editing' }); fireEvent.click(keep); await waitFor(() => expect(screen.queryByRole('button', { name: 'Keep editing' })).not.toBeInTheDocument()); expect(screen.getByDisplayValue('Unsaved.')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Discard draft' })); await screen.findByDisplayValue('Saved procedure.'); expect(screen.getByRole('button', { name: 'Save Skill' })).toBeDisabled(); expect(writes()).toHaveLength(0);
});
it('removes protected editor content on exact item read revocation', async () => {
  await open(); await edit(); detailDenied = true; readStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Reload latest Skill' })); await waitFor(() => expect(screen.queryByDisplayValue('Saved procedure.')).not.toBeInTheDocument()); expect(screen.queryByText('Local review')).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('allows read-only inspection without create/manage or IAM-inspector calls', async () => {
  current.canCreate = false; current.allReadable = false; current.items[0].canManage = false; await open(); expect(screen.getByRole('button', { name: 'New Instance Skill' })).toBeDisabled(); expect(screen.getByRole('switch')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Inspect Skill' })); await screen.findByText('Saved procedure.'); expect(screen.queryByRole('button', { name: 'Save Skill' })).not.toBeInTheDocument(); expect(screen.getByText('Only readable Skills are shown')).toBeVisible(); expect(writes()).toHaveLength(0);
});
it('ignores a late exact detail after the Instance scope retires', async () => {
  deferred = true; const content = (id: string) => <App><TenantAgentSkills key={id} context={context} id={id} /></App>; const { rerender } = render(content('first')); await screen.findByText('Local review'); fireEvent.click(screen.getByRole('button', { name: 'Edit Skill' })); await waitFor(() => expect(resolveDetail).toBeTypeOf('function'));
  const pending = fetchMock.mock.calls.find(([path]) => String(path).includes('/first/skills/instance%3Areview/editor'))!;
  rerender(content('second')); await screen.findByText('Local review'); expect((pending[1]?.signal as AbortSignal).aborted).toBe(true);
  await act(async () => resolveDetail!(response({ agent: current.agent, item: current.items[0], securityRevision: 7 }))); expect(screen.queryByDisplayValue('Saved procedure.')).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
