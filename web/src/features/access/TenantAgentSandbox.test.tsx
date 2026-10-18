import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantInstanceSandbox, TenantInstanceSandboxVerification } from '../../services/tenantAgentSandbox';
import { TenantAgentSandbox } from './TenantAgentSandbox';
const context = { tenant: { tenantId: 'tenant', name: 'Regional organization' }, permissions: [], revision: 7 } as unknown as TenantContext;
const initial = (): TenantInstanceSandbox => {
  const effective = { profileId: 'p-exact', allowedProfileIds: ['p-exact'], initializationScript: null, cpu: 1, storageBytes: 262144000 };
  return { instanceId: 'agent', instanceName: 'Alex', definitionVersion: 1, instanceRevision: 4, lifecycle: 'Active', canManage: true, completeCatalog: true, securityRevision: 7,
    profiles: [{ id: 'p-exact', revision: 2, name: 'Exact environment', description: 'Source', imageReference: 'ghcr.io/acme/tool@sha256:' + 'a'.repeat(64), availability: 'Eligible', message: 'Verified profile, independent of Instance evidence.' }],
    section: { section: 'sandboxPolicy', instanceRevision: 4, definitionId: 'definition', definitionVersion: 1, configurationHash: 'hash', overrides: {}, effective, definitionDefaults: effective, sources: { profileId: 'definition', cpu: 'profile', storageBytes: 'profile', initializationScript: 'profile' }, constraints: { profileId: { reason: 'Definition choices', choices: ['p-exact'], requiredBoolean: null, minimum: null, maximum: null }, allowedProfileIds: { reason: 'Definition choices', choices: ['p-exact'], requiredBoolean: null, minimum: null, maximum: null }, cpu: { reason: 'Profile ceiling', requiredBoolean: null, minimum: .25, maximum: 1 }, storageBytes: { reason: 'Profile ceiling', requiredBoolean: null, minimum: 1, maximum: 262144000 } } }
  };
};
let current: TenantInstanceSandbox, verification: TenantInstanceSandboxVerification, writeStatus: number, readStatus: number, malformed: boolean;
const fetchMock = vi.fn<typeof fetch>(); const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
const writes = () => fetchMock.mock.calls.filter(([, options]) => ['POST', 'PATCH'].includes(options?.method ?? ''));
beforeEach(() => {
  resetIdentityCsrf(); current = initial(); verification = { instanceId: 'agent', currentInstanceRevision: 4, currentProfileRevision: 2, securityRevision: 7, status: 'NeedsVerification', canVerify: true, observation: null, attempt: null }; writeStatus = readStatus = 200; malformed = false; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'PATCH') {
      if (writeStatus !== 200) return response({ reasonCode: 'stale_revision' }, writeStatus);
      const body = JSON.parse(String(options.body)); current = structuredClone(current); current.instanceRevision++; current.securityRevision++;
      current.section!.instanceRevision = current.instanceRevision; Object.assign(current.section!.effective, body.set); Object.assign(current.section!.overrides, body.set);
      verification = { ...verification, currentInstanceRevision: current.instanceRevision, securityRevision: current.securityRevision };
      return malformed ? response({}) : response({ instance: { instanceId: 'agent', revision: current.instanceRevision }, securityRevision: current.securityRevision });
    }
    if (url.endsWith('/sandbox/verify') && options?.method === 'POST') {
      current = { ...current, securityRevision: current.securityRevision + 1 }; verification = { ...verification, status: 'VerificationPending', canVerify: false, securityRevision: current.securityRevision,
        attempt: { attemptId: 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa', instanceId: 'agent', instanceRevision: current.instanceRevision, profileId: 'p-exact', profileRevision: 2, active: true, accepted: false, status: 'CleanupPending', imageOnly: false, startedAt: new Date().toISOString(), result: null } };
      return response(verification);
    }
    if (url.includes('/sandbox/verification')) return readStatus === 200 ? response(verification) : response({ reasonCode: 'permission_denied' }, readStatus);
    if (url.endsWith('/sandbox')) return readStatus === 200 ? response(current) : response({ reasonCode: 'permission_denied' }, readStatus);
    throw Error('Unexpected non-tenant dependency: ' + url);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
async function open() { render(<ConfigProvider theme={{ token: { motion: false } }}><App><TenantAgentSandbox context={context} id="agent" /></App></ConfigProvider>); await screen.findByText('Alex'); }
it('reuses sparse settings and saves exact bytes/witnesses without a global catalog or unrelated model read', async () => {
  await open(); fireEvent.change(screen.getByRole('spinbutton', { name: 'Canonical /working storage (MiB)' }), { target: { value: '128.5' } }); expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeDisabled(); fireEvent.click(screen.getByRole('button', { name: 'Save Sandbox environment' })); await waitFor(() => expect(writes()).toHaveLength(1)); expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 7, expectedInstanceRevision: 4, set: { storageBytes: 134742016 }, clear: [] }); expect(fetchMock.mock.calls.every(([url]) => String(url).includes('/tenants/tenant/') || String(url).endsWith('/csrf'))).toBe(true);
});
it('retains a stale script draft and refreshes exact witnesses before explicit resubmission', async () => {
  writeStatus = 409; await open(); fireEvent.change(screen.getByRole('textbox', { name: 'Instance initialization script' }), { target: { value: 'echo deliberate' } }); fireEvent.click(screen.getByRole('button', { name: 'Save Sandbox environment' })); await screen.findByText('Reload before saving'); expect(screen.getByRole('button', { name: 'Save Sandbox environment' })).toBeDisabled();
  current.securityRevision = 11; current.instanceRevision = current.section!.instanceRevision = 6; current.section!.effective.cpu = .4; verification.securityRevision = 11; verification.currentInstanceRevision = 6; writeStatus = 200; fireEvent.click(screen.getByRole('button', { name: 'Reload settings' })); expect(await screen.findByRole('textbox', { name: 'Instance initialization script' })).toHaveValue('echo deliberate'); expect(screen.getByRole('spinbutton', { name: 'CPU cores' })).toHaveValue('0.4'); fireEvent.click(screen.getByRole('button', { name: 'Save Sandbox environment' })); await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 11, expectedInstanceRevision: 6 });
});
it('retires a successful but semantically malformed acknowledgement before read recovery without replay', async () => {
  await open(); malformed = true; fireEvent.change(screen.getByRole('textbox', { name: 'Instance initialization script' }), { target: { value: 'echo acknowledged' } }); fireEvent.click(screen.getByRole('button', { name: 'Save Sandbox environment' })); await screen.findByText(/Settings were saved\./); expect(screen.queryByRole('button', { name: 'Save Sandbox environment' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(1); fireEvent.click(screen.getByRole('button', { name: 'Retry settings' })); await screen.findByRole('textbox', { name: 'Instance initialization script' }); expect(screen.getByRole('button', { name: 'Save Sandbox environment' })).toBeDisabled(); expect(writes()).toHaveLength(1);
});
it('retires private settings and verification on exact read revocation', async () => {
  await open(); readStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Reload settings' })); await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Instance initialization script' })).not.toBeInTheDocument()); expect(screen.queryByRole('combobox', { name: 'Profile to verify' })).not.toBeInTheDocument(); expect(screen.queryByText('Exact environment')).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});
it('allows separately authorized verification without configuration update permission and preserves pending cleanup', async () => {
  current.canManage = false; await open(); await screen.findByText('Read-only configuration'); expect(screen.getByRole('spinbutton', { name: 'CPU cores' })).toBeDisabled(); await waitFor(() => expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeEnabled()); fireEvent.click(screen.getByRole('button', { name: 'Verify Instance setup' })); const dialog = (await screen.findAllByText('Verify saved Instance setup?'))[0].closest('[role=dialog]') as HTMLElement; expect(dialog).toHaveTextContent('Regional organization'); fireEvent.click(within(dialog).getByRole('button', { name: 'Verify setup' })); await screen.findByText('Verification or cleanup pending'); expect(writes()).toHaveLength(1); expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 7, expectedInstanceRevision: 4, profileId: 'p-exact', expectedProfileRevision: 2 }); expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeDisabled(); fireEvent.click(screen.getByRole('button', { name: 'Reload verification' })); await screen.findByText('Verification or cleanup pending'); expect(writes()).toHaveLength(1);
});
it('requires matching saved setup and evidence witnesses before a verification POST', async () => {
  verification.currentInstanceRevision = 5; await open(); await screen.findByText('Saved setup changed'); expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeDisabled(); expect(writes()).toHaveLength(0);
});
it('keeps partial-catalog verification independent while omitting inaccessible configuration', async () => {
  current.completeCatalog = false; current.section = null; current.canManage = false; await open(); await screen.findByText('Saved Sandbox settings are restricted'); expect(screen.queryByRole('textbox', { name: 'Instance initialization script' })).not.toBeInTheDocument(); await waitFor(() => expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeEnabled()); expect(screen.queryByText('hidden-profile')).not.toBeInTheDocument();
});
it('shows archived inspection and disables settings and unauthorized verification', async () => {
  current.lifecycle = 'Archived'; current.canManage = false; verification.canVerify = false; verification.status = 'Inactive'; await open(); await screen.findByText('Archived instance'); expect(screen.getByRole('spinbutton', { name: 'CPU cores' })).toBeDisabled(); await screen.findByText('Inactive instance'); expect(screen.getByRole('button', { name: 'Verify Instance setup' })).toBeDisabled();
});
