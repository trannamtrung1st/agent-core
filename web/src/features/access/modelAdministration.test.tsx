import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { resetIdentityCsrf } from '../../services/identity';
import { connectionId, keyId, modelCatalog, modelConnection, modelContext, modelOffering, modelPreset, offeringId } from '../../test/modelFixtures';
import type { ModelAdmin } from '../../services/models';
import { ModelConnectionEditor } from './ModelConnectionEditor';
import { ModelOfferingEditor } from './ModelOfferingEditor';
import { ModelRoutingEditor } from './ModelRoutingEditor';
import { ModelDefaultEditor } from './ModelDefaultEditor';
import { ModelReferences } from './ModelReferences';
import { TenantModels } from './TenantModels';
const fetchMock = vi.fn<typeof fetch>();
let current: ModelAdmin, stale: boolean;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const writes = () => fetchMock.mock.calls.filter(([, options]) => ['POST', 'PATCH', 'PUT'].includes(options?.method ?? ''));
const body = () => JSON.parse(String(writes().at(-1)?.[1]?.body));
beforeEach(() => {
  resetIdentityCsrf(); current = modelCatalog(); stale = false; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (['POST', 'PATCH', 'PUT'].includes(options?.method ?? '')) {
      if (stale) return response({ reasonCode: 'stale_revision' }, 409);
      if (url.endsWith('/test')) return response({ metadataAvailable: true, result: 'synthetic_metadata_available', modelsObserved: 0, testedAt: '2026-10-25T00:00:00Z' });
      if (url.endsWith('/discover')) {
        const discovered = { ...structuredClone(modelOffering), offeringId: '88888888-8888-4888-8888-888888888888', label: 'Unknown observed model', externalModelId: 'new-unknown-model', enabled: false, presetId: null, capabilities: { ...structuredClone(modelPreset.capabilities), source: 'Unknown', hierarchy: 'Unsupported' as const } };
        current.offerings.push(discovered); current.availability.push({ offeringId: discovered.offeringId, available: false, reason: 'disabled' }); current.securityRevision++;
        return response({ metadataAvailable: true, result: 'synthetic_metadata_available', modelsObserved: 2, testedAt: '2026-10-25T00:00:00Z' });
      }
      return response(url.includes('model-connections') ? modelConnection : url.endsWith('/default') ? current.settings : modelOffering);
    }
    if (url.includes('/references?')) return stale ? response({ reasonCode: 'stale_revision' }, 409) : response({ items: [], total: 0, page: 0, pageSize: 20, securityRevision: current.securityRevision });
    if (url.includes('/credentials?')) return response({ items: [{ tenantId: modelContext.tenant.tenantId, credential: { credentialId: keyId, displayName: 'Inference key', kind: 'ApiKey', purpose: 'ModelInference', status: 'Active' } }], total: 1, page: 0, pageSize: 20 });
    if (url.endsWith('/models/routing/status')) return response({ status: 'disabled', engineId: 'localKev', engineVersion: 'pinned-fixture', shadowMode: true, preAdmissionSupported: true });
    if (url.endsWith('/models')) return response(current);
    throw new Error(`Unexpected fixture request: ${url}`);
  });
});
function choose(label: string, option: string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: label }));
  fireEvent.click(screen.getByTitle(option, { exact: true }));
}
async function confirmConfiguration() {
  const dialog = (await screen.findByText('Apply model configuration change?', { exact: true })).closest('[role=dialog]') as HTMLElement;
  const apply = within(dialog).getByRole('button', { name: 'Apply configuration change' });
  await waitFor(() => expect(apply).toBeEnabled());
  fireEvent.click(apply);
}
describe('canonical model administration commands', () => {
  it('keeps metadata testing available without tenant-wide model management while hiding discovery', async () => {
    current.access.canCreateOffering = false; current.access.canManageDefault = false;
    render(<App><TenantModels context={modelContext} page="connections" /></App>);
    const test = await screen.findByRole('button', { name: `Test metadata ${modelConnection.label}` });
    expect(test).toBeEnabled();
    expect(screen.queryByRole('button', { name: `Discover models ${modelConnection.label}` })).not.toBeInTheDocument();
    fireEvent.click(test);
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(String(writes()[0][0])).toContain('/test');
  });

  it('requires deliberate recovery confirmation for a model omitted by discovery', async () => {
    const missing = { ...modelOffering, observation: 'NotReturned' as const };
    current.offerings = [missing]; const saved = vi.fn();
    render(<App><ModelOfferingEditor context={modelContext} catalog={current} offering={missing} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    expect(screen.getByRole('checkbox', { name: 'Restore availability for this exact model' })).not.toBeChecked();
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Restore availability for this exact model' })).toBeEnabled());
    fireEvent.click(screen.getByRole('checkbox', { name: 'Restore availability for this exact model' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save model' }));
    await screen.findByText('Restore availability despite missing provider metadata; later discovery can block this model again.');
    await confirmConfiguration();
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ restoreAvailability: true, externalModelId: missing.externalModelId, expectedOfferingRevision: 1 });
  });

  it('sends an explicit false no-credential flag for a native connection with no mounted credential-mode field', async () => {
    const saved = vi.fn();
    render(<App><ModelConnectionEditor context={modelContext} catalog={current} connection={modelConnection} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    await screen.findByTitle('Inference key');
    fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
    await confirmConfiguration();
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ kind: 'OpenAI', noCredential: false, credentialId: keyId, endpoint: null, enabled: true, expectedRevision: 10, expectedConnectionRevision: 1 });
  });

  it('shows an audited key replacement timestamp without a protected value', async () => {
    const original = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (path, options) => String(path).includes('/credentials?') ? response({ items: [{ tenantId: modelContext.tenant.tenantId,
      credential: { credentialId: keyId, displayName: 'Inference key', kind: 'ApiKey', purpose: 'ModelInference', status: 'Active' }, lastRotatedAt: '2026-10-25T01:00:00Z' }], total: 1, page: 0, pageSize: 20 }) : original(path, options));
    render(<App><ModelConnectionEditor context={modelContext} catalog={current} connection={modelConnection} close={vi.fn()} saved={vi.fn()} readLost={vi.fn()} /></App>);
    await screen.findByText(`Last key replacement in Agent Core: ${new Date('2026-10-25T01:00:00Z').toLocaleString()}.`);
    expect(screen.queryByLabelText('Protected value')).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
  });

  it('retains a connection draft across a conflict and refreshes exact revision witnesses before another save', async () => {
    stale = true; const saved = vi.fn();
    render(<App><ModelConnectionEditor context={modelContext} catalog={current} connection={modelConnection} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    fireEvent.change(screen.getByLabelText('Connection name'), { target: { value: 'Retained deliberate name' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
    await screen.findByText('Reload before saving again');
    expect(screen.getByRole('button', { name: 'Save connection' })).toBeDisabled();
    current.securityRevision = 12; current.connections[0].revision = 2; current.connections[0].label = 'Concurrent name'; current.connections[0].credentialId = '55555555-5555-4555-8555-555555555555'; stale = false;
    fireEvent.click(screen.getByRole('button', { name: 'Reload current configuration' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save connection' })).toBeEnabled());
    expect(screen.getByLabelText('Connection name')).toHaveValue('Retained deliberate name');
    expect(screen.getByText(/Protected inference key reference: 55555555-5555-4555-8555-555555555555/)).toBeInTheDocument();
    expect(screen.getByText(`Draft key reference: ${keyId}`)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
    await confirmConfiguration();
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ label: 'Retained deliberate name', expectedRevision: 12, expectedConnectionRevision: 2 });
  });

  it('adopts a preset with explicit state flags and sends only the canonical capability input', async () => {
    const saved = vi.fn();
    render(<App><ModelOfferingEditor context={modelContext} catalog={current} connectionId={connectionId} presetId={modelPreset.presetId} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    fireEvent.click(screen.getByRole('button', { name: 'Save model' }));
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ connectionId, presetId: modelPreset.presetId, enabled: true, retired: false, expectedOfferingRevision: 0 });
    expect(body().capabilities).not.toHaveProperty('source'); expect(body().capabilities).not.toHaveProperty('verifiedAt');
  });

  it('preserves the immutable preset identity when editing an existing offering', async () => {
    const saved = vi.fn();
    render(<App><ModelOfferingEditor context={modelContext} catalog={current} offering={modelOffering} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    fireEvent.change(screen.getByLabelText('Model name'), { target: { value: 'Operator label' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save model' }));
    await confirmConfiguration();
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ presetId: modelPreset.presetId, label: 'Operator label', expectedOfferingRevision: 1 });
    expect(screen.getByLabelText('Retire this offering permanently')).toBeDisabled();
  });

  it('previews readable references and retains the draft when confirmation is cancelled', async () => {
    const saved = vi.fn();
    render(<App><ModelConnectionEditor context={modelContext} catalog={current} connection={modelConnection} close={vi.fn()} saved={saved} readLost={vi.fn()} /></App>);
    fireEvent.change(screen.getByLabelText('Connection name'), { target: { value: 'Deliberate pending edit' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
    const dialog = (await screen.findByText('Apply model configuration change?', { exact: true })).closest('[role=dialog]') as HTMLElement;
    await within(dialog).findByText('Current readable references');
    expect(within(dialog).getByText('Runs: 0')).toBeInTheDocument();
    expect(writes()).toHaveLength(0);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep editing' }));
    await waitFor(() => expect(screen.queryByText('Apply model configuration change?', { exact: true })).not.toBeInTheDocument());
    expect(screen.getByLabelText('Connection name')).toHaveValue('Deliberate pending edit');
    expect(saved).not.toHaveBeenCalled(); expect(writes()).toHaveLength(0);
  });

  it('keeps an unavailable default visible without choosing another model or writing', () => {
    current.availability[0] = { offeringId, available: false, reason: 'credential_unavailable' };
    render(<App><ModelDefaultEditor context={modelContext} catalog={current} saved={vi.fn()} dirtyChanged={vi.fn()} /></App>);
    expect(screen.getByText('Selected default unavailable')).toBeInTheDocument();
    expect(screen.getByText('Credential unavailable')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save organization default' })).toBeDisabled();
    expect(writes()).toHaveLength(0);
  });

  it('retains reasoning across stale-default reload and uses the new settings/security revisions', async () => {
    const saved = vi.fn(); stale = true;
    render(<App><ModelDefaultEditor context={modelContext} catalog={current} saved={saved} dirtyChanged={vi.fn()} /></App>);
    choose('Default reasoning effort', 'high');
    fireEvent.click(screen.getByRole('button', { name: 'Save organization default' }));
    await screen.findByText('Reload before saving again');
    current.securityRevision = 15; current.settings.revision = 3; stale = false;
    const productionConnection = '66666666-6666-4666-8666-666666666666';
    const productionOffering = '77777777-7777-4777-8777-777777777777';
    current.connections.push({ ...modelConnection, connectionId: productionConnection, label: 'Production connection' });
    current.offerings.push({ ...modelOffering, offeringId: productionOffering, connectionId: productionConnection });
    current.availability.push({ offeringId: productionOffering, available: true, reason: 'available' });
    current.settings.defaultOfferingId = productionOffering;
    fireEvent.click(screen.getByRole('button', { name: 'Reload current default' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save organization default' })).toBeEnabled());
    expect(screen.getByText(/Current saved default: GPT-6 Luna · Production connection/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save organization default' }));
    await confirmConfiguration();
    await waitFor(() => expect(saved).toHaveBeenCalled());
    expect(body()).toMatchObject({ offeringId, reasoningEffort: 'high', expectedRevision: 15, expectedSettingsRevision: 3 });
  });

  it('uses scoped action projections instead of a tenant-wide manage guess', async () => {
    current.access.canCreateConnection = false; current.access.canCreateOffering = false; current.access.manageConnectionIds = []; current.access.testConnectionIds = [connectionId];
    render(<App><TenantModels context={{ ...modelContext, permissions: [] }} page="connections" /></App>);
    await screen.findByText('Research connection');
    expect(screen.queryByRole('button', { name: 'Connect a provider' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit connection Research connection' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test metadata Research connection' })).toBeEnabled();
  });
  it('clears reference rows when the newly selected category loses read access', async () => {
    const original = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (path, options) => {
      if (String(path).includes('/references?')) return String(path).includes('kind=instances') ? response({ reasonCode: 'permission_denied' }, 403) : response({
        items: [{ resourceId: modelContext.tenant.tenantId, referenceId: 'default', offeringId, label: 'Readable saved default', usage: 'Organization default', revision: 1, state: 'Configured' }],
        total: 1, page: 0, pageSize: 20, securityRevision: current.securityRevision
      });
      return original(path, options);
    });
    render(<App><ModelReferences tenant={modelContext.tenant.tenantId} catalog={current} target={{ kind: 'offering', id: offeringId, label: 'Exact model' }} close={vi.fn()} /></App>);
    await screen.findByText('Readable saved default', { exact: true });
    choose('Model reference category', 'Instances');
    await screen.findByRole('alert');
    expect(screen.queryByText('Readable saved default', { exact: true })).not.toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });

  it('combines provider, connection, availability and capability filters and resets through explicit All choices', async () => {
    const compatibleId = '99999999-9999-4999-8999-999999999999';
    const compatibleModel = '88888888-8888-4888-8888-888888888888';
    current.connections.push({ ...modelConnection, connectionId: compatibleId, kind: 'OpenAICompatible', label: 'Compatible fixture' });
    current.offerings.push({ ...modelOffering, offeringId: compatibleModel, connectionId: compatibleId, kind: 'OpenAICompatible', label: 'No-tools fixture', enabled: false,
      capabilities: { ...modelOffering.capabilities, tools: false, vision: false, structuredOutput: false, reasoningEfforts: [] } });
    current.availability.push({ offeringId: compatibleModel, available: false, reason: 'disabled' });
    render(<App><TenantModels context={modelContext} page="models" /></App>);
    await screen.findByText('No-tools fixture', { exact: true });
    choose('Filter model provider', 'OpenAI');
    expect(screen.queryByText('No-tools fixture', { exact: true })).not.toBeInTheDocument();
    choose('Filter model provider', 'All providers');
    choose('Filter model capability', 'Images');
    expect(screen.getByText('GPT-6 Luna', { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('No-tools fixture', { exact: true })).not.toBeInTheDocument();
    choose('Filter model connection', 'Compatible fixture');
    expect(screen.queryByText('GPT-6 Luna', { exact: true })).not.toBeInTheDocument();
    choose('Filter model capability', 'All capabilities');
    expect(screen.getByText('No-tools fixture', { exact: true })).toBeInTheDocument();
    choose('Filter model availability', 'Available for admission');
    expect(screen.queryByText('No-tools fixture', { exact: true })).not.toBeInTheDocument();
    expect(screen.getByText('No models match this search and filters.', { exact: true })).toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });

  it('keeps discovered unknown models disabled and the explicit default unchanged', async () => {
    const view = render(<App><TenantModels context={modelContext} page="connections" /></App>);
    fireEvent.click(await screen.findByRole('button', { name: 'Discover models Research connection' }));
    await screen.findByText('Offline Synthetic metadata fixture', { exact: true });
    view.rerender(<App><TenantModels context={modelContext} page="models" /></App>);
    await screen.findByText('Unknown observed model', { exact: true });
    expect(screen.getByText('Capabilities unverified', { exact: true })).toBeInTheDocument();
    expect(screen.getByText('Disabled', { exact: true })).toBeInTheDocument();
    expect(current.settings.defaultOfferingId).toBe(offeringId);
    expect(current.offerings.at(-1)?.enabled).toBe(false);
    expect(writes()).toHaveLength(1);
  });

});

it('routing saves canonical intent under CAS and retains its draft through a stale readback', async () => {
  const saved = vi.fn(); render(<App><ModelRoutingEditor context={modelContext} catalog={current} saved={saved} dirtyChanged={() => {}} /></App>);
  await screen.findByText('Decision service: disabled');
  fireEvent.click(screen.getByRole('checkbox', { name: 'Allow Auto for new Runs' }));
  stale = true; fireEvent.click(screen.getByRole('button', { name: 'Save routing' }));
  await screen.findByText('Reload before saving again');
  expect(body()).toMatchObject({ expectedRevision: current.securityRevision, expectedSettingsRevision: current.settings.revision,
    routing: { enabled: true, allowConfiguredDefault: false, minimumProbability: .7 } });
  expect(saved).not.toHaveBeenCalled();
  stale = false; current.securityRevision++;
  fireEvent.click(screen.getByRole('button', { name: 'Reload routing' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save routing' })).toBeEnabled());
  expect(screen.getByRole('checkbox', { name: 'Allow Auto for new Runs' })).toBeChecked();
  fireEvent.click(screen.getByRole('button', { name: 'Save routing' }));
  await waitFor(() => expect(saved).toHaveBeenCalledOnce());
  expect(body().expectedRevision).toBe(current.securityRevision);
});
