import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantResourceEditor } from '../../services/tenantAgentConfiguration';
import { TenantAgentResources } from './TenantAgentResources';
vi.mock('../filePreview/FilePreview', () => ({ FilePreviewButton: () => <button>Preview</button> }));
const context = { tenant: { tenantId: 'tenant', name: 'Regional organization', slug: 'regional' }, userId: 'u', principalId: 'p', permissions: [], roles: [], revision: 7 } as TenantContext;
const initial = () => ({ agent: { principalId: 'principal', systemIdentityId: 'identity', instance: { instanceId: 'agent', definitionId: 'source', persona: { name: 'Alex', role: 'Guide', description: 'Regional guide', tone: 'Calm' }, revision: 4, personaRevision: 1, activeVersion: 1, lifecycle: 'Active' } },
  items: [{ resource: { key: 'instance:00000000-0000-0000-0000-000000000001', origin: 'Instance', logicalPath: 'references/local.txt', kind: 2, mediaType: 'text/plain', contentSha256: 'a'.repeat(64), byteLength: 6, enabled: true, enabledOverride: null, revision: 2, virtualPath: '/agent/instance/resources/references/local.txt', dependencies: [], sourceDefinitionId: null, sourceDefinitionVersion: null }, canManage: true, canDelete: true, canCopy: false }], total: 1, page: 0, pageSize: 10, securityRevision: 7, canCreate: true, allReadable: true } as TenantResourceEditor);
const fetchMock = vi.fn<typeof fetch>(); let current: TenantResourceEditor, writeStatus: number, readStatus: number, malformed: boolean, detailDenied: boolean, deferred: boolean, failureAt: number, manifestDrift: number, manifestCount: number;
let resolveDetail: ((response: Response) => void) | undefined;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
beforeEach(() => {
  resetIdentityCsrf(); current = initial(); writeStatus = readStatus = 200; failureAt = manifestDrift = manifestCount = 0; malformed = detailDenied = deferred = false; resolveDetail = undefined; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'POST') {
      if (writeStatus !== 200 && (!failureAt || writes().length === failureAt)) return response({ reasonCode: 'stale_revision' }, writeStatus);
      const body = JSON.parse(String(options.body)); current = structuredClone(current); current.securityRevision++; current.agent.instance.revision++;
      let item = current.items[0];
      if (body.input.resourceId) item.resource = { ...item.resource, ...body.input, revision: item.resource.revision + 1 };
      else { item = structuredClone(item); item.resource = { ...item.resource, ...body.input, key: `instance:added-${writes().length}`, revision: 1 }; current.items.push(item); current.total++; }
      return malformed ? new Response('{', { status: 200 }) : response({ resource: item.resource, instanceRevision: current.agent.instance.revision, securityRevision: current.securityRevision });
    }
    if (/\/resources\/instance%3A.+\/editor/.test(url)) {
      if (deferred) return new Promise(resolve => { resolveDetail = resolve; });
      return detailDenied ? response({ reasonCode: 'permission_denied' }, 403) : response({ agent: current.agent, item: current.items[0], securityRevision: current.securityRevision });
    }
    if (url.includes('/resources/editor')) return readStatus === 200 ? response(current) : response({ reasonCode: readStatus === 403 ? 'permission_denied' : 'unavailable' }, readStatus);
    if (url.endsWith('/resources')) return readStatus === 200 ? response({ catalog: { instanceRevision: current.agent.instance.revision, resources: manifestCount ? Array.from({ length: manifestCount }, (_, index) => ({ ...current.items[0].resource, logicalPath: `existing/${index}.txt` })) : current.items.map(item => item.resource) }, securityRevision: current.securityRevision + manifestDrift }) : response({ reasonCode: 'unavailable' }, readStatus);
    throw new Error('Unexpected ' + url);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
async function open() { render(<App><TenantAgentResources context={context} id="agent" /></App>); await screen.findByText('references/local.txt'); }
async function edit() { fireEvent.click(screen.getByRole('button', { name: 'Edit resource' })); await screen.findByDisplayValue('references/local.txt'); }
const staged = (name: string) => { const file = new File([name], name, { type: 'text/plain' }); Object.defineProperties(file, { webkitRelativePath: { value: `package/references/${name}` }, arrayBuffer: { value: async () => new TextEncoder().encode(name).buffer } }); return file; };

it('saves exact local metadata with current witnesses while preserving omitted bytes and enabled choice', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Resource logical path'), { target: { value: 'references/renamed.txt' } }); fireEvent.click(screen.getByRole('button', { name: 'Save resource' }));
  await waitFor(() => expect(writes()).toHaveLength(1)); const body = JSON.parse(String(writes()[0][1]?.body));
  expect(body).toMatchObject({ expectedRevision: 7, expectedInstanceRevision: 4, input: { resourceId: '00000000-0000-0000-0000-000000000001', expectedResourceRevision: 2, logicalPath: 'references/renamed.txt', kind: 2, mediaType: 'text/plain' } });
  expect(body.input).not.toHaveProperty('content'); expect(body.input).not.toHaveProperty('enabled'); await waitFor(() => expect(screen.queryByRole('button', { name: 'Save resource' })).not.toBeInTheDocument());
});
it('retains a stale draft and refreshes exact witnesses and untouched availability before an explicit retry', async () => {
  writeStatus = 409; await open(); await edit(); fireEvent.change(screen.getByLabelText('Resource logical path'), { target: { value: 'references/retained.txt' } }); fireEvent.click(screen.getByRole('button', { name: 'Save resource' })); await screen.findByText(/Access has changed since/);
  expect(screen.getByRole('button', { name: 'Save resource' })).toBeDisabled(); current.securityRevision = 11; current.agent.instance.revision = 8; current.items[0].resource.revision = 5; current.items[0].resource.enabled = false; writeStatus = 200;
  fireEvent.click(screen.getByRole('button', { name: 'Reload latest resources' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Save resource' })).toBeEnabled()); expect(screen.getByLabelText('Resource logical path')).toHaveValue('references/retained.txt'); expect(screen.getByRole('switch', { name: 'Instance resource enabled' })).not.toBeChecked(); expect(writes()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Save resource' })); await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 11, expectedInstanceRevision: 8, input: { expectedResourceRevision: 5 } });
});
it('retires acknowledged malformed metadata saves before failed-read recovery without replay', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Resource logical path'), { target: { value: 'references/saved.txt' } }); malformed = true; readStatus = 503;
  fireEvent.click(screen.getByRole('button', { name: 'Save resource' })); await screen.findByText('Resource changes were saved'); expect(writes()).toHaveLength(1); expect(screen.queryByRole('button', { name: 'Save resource' })).not.toBeInTheDocument();
  readStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload resources' })); await screen.findByText('references/saved.txt'); expect(writes()).toHaveLength(1);
});
it('guards dirty cancellation and adopts saved values only on explicit discard', async () => {
  await open(); await edit(); fireEvent.change(screen.getByLabelText('Resource logical path'), { target: { value: 'references/unsaved.txt' } }); fireEvent.click(screen.getByRole('button', { name: 'Cancel editing' })); fireEvent.click(await screen.findByRole('button', { name: 'Keep editing' })); await waitFor(() => expect(screen.queryByRole('button', { name: 'Keep editing' })).not.toBeInTheDocument());
  expect(screen.getByLabelText('Resource logical path')).toHaveValue('references/unsaved.txt'); fireEvent.click(screen.getByRole('button', { name: 'Discard draft' })); await screen.findByDisplayValue('references/local.txt'); expect(screen.getByRole('button', { name: 'Save resource' })).toBeDisabled(); expect(writes()).toHaveLength(0);
});
it('stages files until Save, retains only the uncommitted remainder and resumes with refreshed witnesses', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Add Instance resources' })); await screen.findByRole('button', { name: 'Choose resource files' });
  fireEvent.change(document.querySelector('input[type=file]')!, { target: { files: [staged('one.txt'), staged('two.txt')] } }); await screen.findByDisplayValue('references/two.txt'); expect(writes()).toHaveLength(0);
  writeStatus = 409; failureAt = 2; fireEvent.click(screen.getByRole('button', { name: 'Add resources' })); await screen.findByText('1 resource saved'); await screen.findByText(/Access has changed since/);
  expect(screen.queryByDisplayValue('references/one.txt')).not.toBeInTheDocument(); expect(screen.getByDisplayValue('references/two.txt')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Add resources' })).toBeDisabled();
  current.securityRevision = 12; current.agent.instance.revision = 9; writeStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload latest resources' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Add resources' })).toBeEnabled()); expect(writes()).toHaveLength(2);
  fireEvent.click(screen.getByRole('button', { name: 'Add resources' })); await waitFor(() => expect(writes()).toHaveLength(3)); expect(JSON.parse(String(writes()[2][1]?.body))).toMatchObject({ expectedRevision: 12, expectedInstanceRevision: 9, input: { logicalPath: 'references/two.txt' } });
  expect(writes().map(([, options]) => JSON.parse(String(options?.body)).input.logicalPath)).toEqual(['references/one.txt', 'references/two.txt', 'references/two.txt']);
});
it('permits exact read-only inspection without IAM tools and retires protected content on read loss', async () => {
  current.canCreate = false; current.allReadable = false; current.items[0].canManage = current.items[0].canDelete = false; await open(); expect(screen.getByRole('button', { name: 'Add Instance resources' })).toBeDisabled(); expect(screen.queryByRole('button', { name: 'Edit resource' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Inspect resource' })); await screen.findByText('/agent/instance/resources/references/local.txt'); expect(screen.queryByRole('button', { name: 'Save resource' })).not.toBeInTheDocument();
  detailDenied = true; readStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Reload latest resources' })); await waitFor(() => { expect(screen.queryByText('/agent/instance/resources/references/local.txt')).not.toBeInTheDocument(); expect(screen.queryByText('references/local.txt')).not.toBeInTheDocument(); }); expect(writes()).toHaveLength(0);
});
it('requires coherent complete-manifest facts before an explicit staged import', async () => {
  manifestDrift = 1; await open(); fireEvent.click(screen.getByRole('button', { name: 'Add Instance resources' })); await screen.findByRole('button', { name: 'Choose resource files' });
  fireEvent.change(document.querySelector('input[type=file]')!, { target: { files: [staged('one.txt')] } }); await screen.findByText('Resource facts changed while loading'); expect(screen.getByRole('button', { name: 'Add resources' })).toBeDisabled(); expect(writes()).toHaveLength(0);
  manifestDrift = 0; fireEvent.click(screen.getByRole('button', { name: 'Reload latest resources' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Add resources' })).toBeEnabled()); expect(screen.getByDisplayValue('references/one.txt')).toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('uses the full authorized manifest for import capacity beyond the visible collection page', async () => {
  manifestCount = 64; await open(); fireEvent.click(screen.getByRole('button', { name: 'Add Instance resources' })); await screen.findByRole('button', { name: 'Choose resource files' });
  fireEvent.change(document.querySelector('input[type=file]')!, { target: { files: [staged('one.txt')] } }); await screen.findByText('This import would exceed 64 resources on the Instance.'); expect(screen.getByRole('button', { name: 'Add resources' })).toBeDisabled(); expect(writes()).toHaveLength(0);
});
it('ignores and aborts late exact detail after the Instance scope retires', async () => {
  deferred = true; const content = (id: string) => <App><TenantAgentResources key={id} context={context} id={id} /></App>; const { rerender } = render(content('first')); await screen.findByText('references/local.txt'); fireEvent.click(screen.getByRole('button', { name: 'Edit resource' })); await waitFor(() => expect(resolveDetail).toBeTypeOf('function'));
  const pending = fetchMock.mock.calls.find(([path]) => String(path).includes('/first/resources/instance%3A'))!; rerender(content('second')); await screen.findByText('references/local.txt'); expect((pending[1]?.signal as AbortSignal).aborted).toBe(true);
  await act(async () => resolveDetail!(response({ agent: current.agent, item: current.items[0], securityRevision: 7 }))); expect(screen.queryByRole('button', { name: 'Save resource' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
