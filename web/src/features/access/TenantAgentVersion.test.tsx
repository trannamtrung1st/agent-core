import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantAgentVersionCatalog } from '../../services/tenantAgentConfiguration';
import { TenantAgentVersion } from './TenantAgentVersion';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Version organization', slug: 'version' }, userId: 'user', principalId: 'viewer', permissions: [], roles: [], revision: 7 };
const catalog = () => ({ agent: { principalId: 'principal', systemIdentityId: 'identity', instance: { instanceId: 'agent', definitionId: 'definition', persona: { name: 'Alex', role: 'Guide', description: 'Scoped guide', tone: 'Calm' }, personaRevision: 1, revision: 4, activeVersion: 1, lifecycle: 'Active' } }, securityRevision: 7, canAdopt: true,
  items: [{ version: 3, status: 'Deprecated', metadataRevision: 2 }, { version: 2, status: 'Active', metadataRevision: 1 }, { version: 1, status: 'Active', metadataRevision: 1 }], total: 3, page: 0, pageSize: 10 } as TenantAgentVersionCatalog);
const fetchMock = vi.fn<typeof fetch>(); let current: TenantAgentVersionCatalog; let stale = false, malformed = false, readFailure = false, denied = false, drift = false; let previewStatus = 200;
let waitPreview: ((value: Response) => void) | undefined; let deferPreview = false;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const preview = () => ({ preview: { instanceRevision: current.agent.instance.revision, currentVersion: current.agent.instance.activeVersion, targetVersion: 2, changedDefinitionFields: ['instructions.agentInstructions', 'selfManagement'], preservedInstanceOverrides: ['identity'], newInheritedItems: [], changedInheritedItems: [], removedInheritedItems: [], activationNotice: 'Applies to the next new Run. Current Runs are unchanged.' }, securityRevision: current.securityRevision + (drift ? 1 : 0), targetPublicationRevision: 1, canAdopt: current.canAdopt, childAccess: { skills: true, resources: false, allSkills: false, allResources: false } });
beforeEach(() => {
  resetIdentityCsrf(); current = catalog(); stale = malformed = readFailure = denied = drift = deferPreview = false; previewStatus = 200; waitPreview = undefined; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'PUT') { if (stale) return response({ reasonCode: 'stale_revision' }, 409); current = structuredClone(current); current.securityRevision++; current.agent.instance.revision++; current.agent.instance.activeVersion = 2; return malformed ? new Response('{', { status: 200, headers: { 'Content-Type': 'application/json' } }) : response({ saved: true }); }
    if (url.includes('/active-version/preview')) { if (deferPreview) return new Promise(resolve => { waitPreview = resolve; }); return previewStatus === 200 ? response(preview()) : response({ reasonCode: previewStatus === 403 ? 'permission_denied' : previewStatus === 400 ? 'invalid_request' : 'unavailable' }, previewStatus); }
    if (url.includes('/versions?')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : readFailure ? response({}, 503) : response(url.includes('page=1&') ? { ...current, page: 1, items: [] } : current);
    throw new Error(`Unexpected ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentVersion context={context} id="agent" /></App>); await screen.findByText('Alex'); };
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT');
async function choose() { fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Target Definition version' })); fireEvent.click(await screen.findByText('v2 · Active', { selector: '.ant-select-item-option-content' })); }
async function review() { await choose(); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByRole('button', { name: 'Adopt Definition v2' }); }
async function confirm() {
  fireEvent.click(screen.getByRole('button', { name: 'Adopt Definition v2' }));
  // The shared test setup stubs useId to one value, so dialog name lookup resolves a duplicate ID.
  const action = await screen.findByRole('button', { name: 'Adopt version' }); const dialog = action.closest('[role="dialog"]') as HTMLElement;
  expect(dialog).toHaveTextContent('Agent: Alex. Organization: Version organization.'); fireEvent.click(within(dialog).getByRole('button', { name: 'Adopt version' }));
}

it('makes preview a read and adopts only the exact reviewed target/witnesses after confirmation', async () => {
  await open(); await review(); expect(writes()).toHaveLength(0); expect(screen.getByText('Instructions · Agent Instructions, Self-management')).toBeVisible();
  expect(screen.getByText('Child details follow your read access')).toBeVisible(); await confirm(); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(writes()[0][0]).toBe('/api/v2/tenants/tenant/agents/agent/active-version'); expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ version: 2, expectedRevision: 7, expectedInstanceRevision: 4 });
  await screen.findByText('Definition v2'); expect(screen.queryByRole('region', { name: 'Definition version preview' })).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.every(([path]) => String(path).startsWith('/api/v2/'))).toBe(true);
});
it('retires a stale review, keeps its target and requires reload plus explicit preview before retry', async () => {
  stale = true; await open(); await review(); await confirm(); await screen.findByText(/Access has changed since you opened/);
  expect(screen.queryByRole('button', { name: 'Adopt Definition v2' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Preview changes' })).toBeDisabled();
  current.securityRevision = 9; current.agent.instance.revision = 6; stale = false; fireEvent.click(screen.getByRole('button', { name: 'Reload versions' }));
  await screen.findByRole('combobox', { name: 'Target Definition version' }); expect(screen.getByText('Selected v2 is not applied. Leaving discards this review.')).toBeVisible(); expect(writes()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByRole('button', { name: 'Adopt Definition v2' }); await confirm();
  await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 9, expectedInstanceRevision: 6 });
});
it('rejects a preview whose current witnesses differ from the displayed catalog', async () => {
  drift = true; await open(); await choose(); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByText(/Access has changed since you opened/);
  expect(screen.queryByRole('button', { name: 'Adopt Definition v2' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('recovers an acknowledged malformed save by read alone without replay', async () => {
  await open(); await review(); malformed = readFailure = true; await confirm(); await screen.findByText('Version adoption was saved'); expect(writes()).toHaveLength(1);
  readFailure = false; fireEvent.click(screen.getByRole('button', { name: 'Reload versions' })); await screen.findByText('Definition v2'); expect(writes()).toHaveLength(1); expect(screen.queryByRole('button', { name: 'Adopt Definition v2' })).not.toBeInTheDocument();
});
it('allows read-only preview, disables adoption and retires protected fields on preview read loss', async () => {
  current.canAdopt = false; await open(); await review(); expect(screen.getByRole('button', { name: 'Adopt Definition v2' })).toBeDisabled();
  previewStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Target Definition version' })).not.toBeInTheDocument()); expect(screen.queryByText('Alex')).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('retains selection through transient preview failure and permits a deliberate read retry', async () => {
  previewStatus = 503; await open(); await choose(); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByText(/service is temporarily unavailable/);
  previewStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload' })); await screen.findByRole('button', { name: 'Adopt Definition v2' }); expect(writes()).toHaveLength(0);
});
it('does not apply late preview results after selection clearing', async () => {
  deferPreview = true; await open(); await choose(); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await waitFor(() => expect(waitPreview).toBeTypeOf('function'));
  fireEvent.click(screen.getByRole('button', { name: 'Clear selection' })); await act(async () => waitPreview!(response(preview()))); expect(screen.queryByRole('button', { name: 'Adopt Definition v2' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('keeps current/deprecated versions unselectable and page changes clear the old review', async () => {
  current.total = 11; await open(); await review(); fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Target Definition version' }));
  expect(screen.getByText('v3 · Deprecated', { selector: '.ant-select-item-option-content' }).closest('.ant-select-item')).toHaveClass('ant-select-item-option-disabled');
  fireEvent.click(screen.getByRole('button', { name: 'Clear selection' })); fireEvent.click(screen.getByTitle('Next Page'));
  await screen.findByText('No versions are available on this page.'); expect(screen.queryByRole('region', { name: 'Definition version preview' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});

it('aborts a retired scope and never shows its late preview in the new Instance', async () => {
  deferPreview = true; const view = (id: string) => <App><TenantAgentVersion key={id} context={context} id={id} /></App>;
  const { rerender } = render(view('first')); await screen.findByText('Alex'); await choose(); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' }));
  await waitFor(() => expect(waitPreview).toBeTypeOf('function')); const pending = fetchMock.mock.calls.find(([path]) => String(path).includes('/first/active-version/preview'))!;
  rerender(view('second')); await screen.findByText('Alex'); expect((pending[1]?.signal as AbortSignal).aborted).toBe(true);
  await act(async () => waitPreview!(response(preview()))); expect(screen.queryByRole('button', { name: 'Adopt Definition v2' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
