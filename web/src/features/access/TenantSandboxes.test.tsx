import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { blankTenantSandbox, type TenantSandboxProfile } from '../../services/tenantSandboxes';
import { TenantSandboxes } from './TenantSandboxes';
const context = { tenant: { tenantId: 'tenant', name: 'Regional organization' }, permissions: [], revision: 7 } as unknown as TenantContext;
const initial = (): TenantSandboxProfile => ({ profile: { ...blankTenantSandbox(), id: 'p-exact', revision: 2, name: 'Exact environment', imageReference: 'ghcr.io/acme/tool@sha256:' + 'a'.repeat(64) }, canManage: true, securityRevision: 7 });
const fetchMock = vi.fn<typeof fetch>(); let current: TenantSandboxProfile; let writeStatus: number, detailStatus: number; let malformed: boolean, retained: boolean; let historyReads: number[];
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
const writes = () => fetchMock.mock.calls.filter(([, options]) => ['PUT', 'POST'].includes(options?.method ?? ''));
beforeEach(() => {
  current = initial(); writeStatus = detailStatus = 200; malformed = retained = false; historyReads = []; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); window.history.replaceState(null, '', '/t/tenant/integrations/sandboxes?profileId=p-exact');
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.includes('/credentials')) return response({ items: [], total: 0, page: 0, pageSize: 20 });
    if (['PUT', 'POST'].includes(options?.method ?? '')) {
      if (writeStatus !== 200) return response({ reasonCode: 'stale_revision' }, writeStatus);
      if (url.endsWith('/verify') || url.endsWith('/check-image')) { retained = true; return response({ attempt: { status: 'CleanupPending', active: true, accepted: false, imageOnly: false, profileRevision: current.profile.revision, startedAt: new Date().toISOString(), result: null }, securityRevision: 8, currentProfileRevision: current.profile.revision }); }
      const body = JSON.parse(String(options?.body)); current = { ...current, profile: { ...current.profile, ...body, revision: current.profile.revision + 1 }, securityRevision: current.securityRevision + 1 }; return malformed ? new Response('{', { status: 200 }) : response(current);
    }
    if (url.endsWith('/verification')) return response({ attempt: retained ? { status: 'CleanupPending', active: true, accepted: false, imageOnly: false, profileRevision: current.profile.revision, startedAt: new Date().toISOString(), result: null } : null, securityRevision: current.securityRevision, currentProfileRevision: current.profile.revision });
    if (url.includes('/p-exact')) {
      if (detailStatus !== 200) return response({ reasonCode: 'permission_denied' }, detailStatus);
      const revision = /revision=(\d+)/.exec(url); if (revision) { historyReads.push(Number(revision[1])); return response({ ...current, profile: { ...current.profile, name: 'Historical environment', revision: Number(revision[1]) } }); }
      return response(current);
    }
    return response({ items: [current], total: 1, page: 0, pageSize: 20, canCreate: true, allReadable: false, securityRevision: current.securityRevision });
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
async function open() { render(<ConfigProvider theme={{ token: { motion: false } }}><App><TenantSandboxes context={context} /></App></ConfigProvider>); await screen.findByRole('button', { name: 'Edit profile' }); }
async function edit() { await open(); fireEvent.click(screen.getByRole('button', { name: 'Edit profile' })); await screen.findByLabelText('Sandbox profile name'); }
it('saves exact scoped profile witnesses and normalized optional values without caller authority', async () => {
  await edit(); fireEvent.change(screen.getByLabelText('Sandbox profile name'), { target: { value: 'Explicit revision' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
  await waitFor(() => expect(writes()).toHaveLength(1)); const [url, options] = writes()[0]; expect(String(url)).toBe('/api/v2/tenants/tenant/sandbox-profiles/p-exact'); expect(options?.method).toBe('PUT'); expect(JSON.parse(String(options?.body))).toMatchObject({ expectedRevision: 7, expectedProfileRevision: 2, name: 'Explicit revision', pids: 64, registryCredentialId: null, initializationScript: null }); expect(JSON.parse(String(options?.body))).not.toHaveProperty('tenantId'); await screen.findByText('Profile changes were saved');
});
it('retains deliberate stale fields while refreshing untouched values and exact save witnesses', async () => {
  writeStatus = 409; await edit(); fireEvent.change(screen.getByLabelText('Sandbox profile description'), { target: { value: 'Deliberate draft' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await screen.findByText(/Reload the latest profile before saving/); expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
  current.securityRevision = 12; current.profile.revision = 5; current.profile.enabled = false; writeStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload latest profile' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Save profile' })).toBeEnabled()); expect(screen.getByLabelText('Sandbox profile description')).toHaveValue('Deliberate draft'); expect(screen.getByRole('switch', { name: 'Sandbox profile enabled' })).not.toBeChecked();
  fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 12, expectedProfileRevision: 5, enabled: false });
});
it('retires an acknowledged malformed save before read recovery without replay', async () => {
  await edit(); malformed = true; fireEvent.change(screen.getByLabelText('Sandbox profile name'), { target: { value: 'Acknowledged name' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await screen.findByText('Profile changes were saved'); await waitFor(() => expect(screen.queryByRole('button', { name: 'Save profile' })).not.toBeInTheDocument()); expect(writes()).toHaveLength(1); fireEvent.click(screen.getByRole('button', { name: 'Reload details' })); await screen.findAllByText('Acknowledged name'); expect(writes()).toHaveLength(1);
});
it('releases protected detail and draft when exact read access is revoked during recovery', async () => {
  await edit(); fireEvent.change(screen.getByLabelText('Sandbox profile description'), { target: { value: 'Private draft' } }); detailStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Reload latest profile' })); await waitFor(() => expect(screen.queryByLabelText('Sandbox profile description')).not.toBeInTheDocument()); await waitFor(() => expect(screen.queryByText('Immutable image')).not.toBeInTheDocument()); expect(writes()).toHaveLength(0);
});
it('keeps read-only profile inspection available without edit or verification commands', async () => {
  current.canManage = false; render(<App><TenantSandboxes context={context} /></App>); await screen.findByText('Read-only profile'); expect(screen.queryByRole('button', { name: 'Edit profile' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Verify profile' })).not.toBeInTheDocument(); expect(screen.getByText('Immutable image')).toBeInTheDocument();
});
it('records one explicit verification command and blocks another while retained cleanup is active', async () => {
  await open(); await waitFor(() => expect(screen.getByRole('button', { name: 'Verify profile' })).toBeEnabled()); fireEvent.click(screen.getByRole('button', { name: 'Verify profile' })); const dialog = (await screen.findAllByText('Verify this saved profile?', { exact: true }))[0].closest('[role=dialog]') as HTMLElement; expect(dialog).toHaveTextContent('Regional organization'); fireEvent.click(within(dialog).getByRole('button', { name: 'Verify profile' })); await screen.findByText('Cleanup pending'); expect(writes()).toHaveLength(1); expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 7, expectedProfileRevision: 2 }); expect(screen.getByRole('button', { name: 'Verify profile' })).toBeDisabled(); fireEvent.click(screen.getByRole('button', { name: 'Reload details' })); await screen.findByText('Cleanup pending'); expect(writes()).toHaveLength(1);
});
it('preserves an unreadable saved registry binding through unrelated edits', async () => {
  current.profile.registryCredentialId = 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'; await edit(); fireEvent.change(screen.getByLabelText('Sandbox profile description'), { target: { value: 'Unrelated draft' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await waitFor(() => expect(writes()).toHaveLength(1)); expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ registryCredentialId: 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa' });
});

it('reads an exact historical revision without constructing unbounded revision options or allowing changes', async () => {
  current.profile.revision = 1000000; await open(); fireEvent.change(screen.getByRole('spinbutton', { name: 'Profile revision history' }), { target: { value: '1' } }); fireEvent.click(screen.getByRole('button', { name: 'Read revision' })); await screen.findByText('Historical revision'); expect(historyReads).toEqual([1]); expect(screen.queryByRole('button', { name: 'Edit profile' })).not.toBeInTheDocument(); fireEvent.click(screen.getByRole('button', { name: 'Return to current' })); await screen.findByRole('button', { name: 'Edit profile' });
});
