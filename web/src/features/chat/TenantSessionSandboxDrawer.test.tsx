import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { resetIdentityCsrf } from '../../services/identity';
import type { TenantSandboxControls } from '../../services/tenantSessionSandbox';
import { TenantSessionSandboxDrawer } from './TenantSessionSandboxDrawer';
const executionId = 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'; const oldId = 'bbbbbbbb-1111-4111-8111-bbbbbbbbbbbb';
const initial = (): TenantSandboxControls => ({ instanceRevision: 5, canReset: true, canStop: false, defaultProfileId: 'one',
  profiles: [{ id: 'one', revision: 2, name: 'Current environment', imageReference: 'ghcr.io/acme/tool@sha256:' + 'a'.repeat(64), availability: 'Eligible', canSelect: false }, { id: 'two', revision: 4, name: 'Alternate environment', imageReference: 'ghcr.io/acme/alternate@sha256:' + 'b'.repeat(64), availability: 'Eligible', canSelect: true }],
  observation: { sessionId: 'session', sessionRevision: 7, securityRevision: 11, liveAuthorityConfirmed: false,
    environment: { generation: 'generation', revision: 3, storedState: 'Ready', profileId: 'one', profileRevision: 2, profileName: 'Current environment', imageReference: 'ghcr.io/acme/tool@sha256:' + 'a'.repeat(64), createdAt: '2026-10-18T00:00:00Z', expiresAt: '2026-10-18T00:10:00Z', activeExecutionId: null, lastExecutionId: executionId, cleanupPending: false, message: null, initialization: null, storageEnforcement: 'sampled', selectedProfileId: 'one', selection: null, selectionRestricted: false },
    execution: { executionId, revision: 6, agentRunId: 'run', generation: 'generation', state: 'Completed', profileId: 'one', profileRevision: 2, commandText: 'printf safe-evidence', background: true, startedAt: '2026-10-18T00:00:01Z', finishedAt: '2026-10-18T00:00:02Z', exitCode: 0, stdout: '<script>inert-output</script>', stderr: '', truncated: true, durationMs: 1000, filesystemEffectsMayHaveOccurred: true, terminationConfirmed: true, remoteTerminationConfirmed: false, diagnosticId: null, stopRequested: false, stopDispatchStarted: false }
  }
});
let current: TenantSandboxControls, status: number, readStatus: number, malformed: boolean;
const fetchMock = vi.fn<typeof fetch>(); const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
const posts = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
beforeEach(() => {
  current = initial(); status = readStatus = 200; malformed = false; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return json({ token: 'csrf' });
    if (options?.method === 'POST') {
      if (status !== 200) return json({ reasonCode: status === 409 ? 'stale_revision' : 'permission_denied' }, status);
      current = structuredClone(current); current.observation.securityRevision++; current.observation.environment!.revision++;
      current.observation.environment!.cleanupPending = true; current.observation.environment!.storedState = 'Terminating'; current.canReset = current.canStop = false;
      current.profiles.forEach(p => p.canSelect = false);
      return malformed ? new Response('unreadable', { status: 202 }) : json({ retirementConfirmed: false }, 202);
    }
    if (url.endsWith('/controls')) return readStatus === 200 ? json(current) : json({ reasonCode: 'permission_denied' }, readStatus);
    if (url.endsWith('/executions/' + oldId)) return json({ ...current.observation, execution: { ...current.observation.execution!, executionId: oldId, commandText: 'printf historical', stdout: 'historical-only', generation: 'old-generation' } });
    throw Error('Unexpected route: ' + url);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
function tree(session = 'session') { return <ConfigProvider theme={{ token: { motion: false } }}><App><TenantSessionSandboxDrawer tenant="tenant" organization="Regional organization" sessionId={session} open narrow={false} onClose={() => {}} /></App></ConfigProvider>; }
async function open() { const rendered = render(tree()); await screen.findByText('Stored: Ready'); return rendered; }
async function confirm(title: string, action: string) { const found = await screen.findAllByText(title, { selector: '.ant-modal-confirm-title' }); const dialog = found[0].closest('[role=dialog]') as HTMLElement; expect(dialog).toHaveTextContent('Regional organization'); fireEvent.click(within(dialog).getByRole('button', { name: action })); }
it('shows an empty observation without provisioning or using owner routes', async () => {
  current.observation.environment = null; current.observation.execution = null; current.canReset = false; current.profiles.forEach(p => p.canSelect = false);
  render(tree()); await screen.findByText(/No stored Sandbox environment/); expect(screen.getByRole('button', { name: 'Reset sandbox' })).toBeDisabled(); expect(posts()).toHaveLength(0);
  expect(fetchMock.mock.calls.every(([path]) => String(path).includes('/tenants/tenant/') || String(path).endsWith('/csrf'))).toBe(true);
});
it('confirms exact reset witnesses and reads cleanup without replay', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); expect(posts()).toHaveLength(0); await confirm('Reset this Session sandbox?', 'Request reset'); await screen.findByText('Cleanup pending');
  expect(posts()).toHaveLength(1); expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({ expectedRevision: 11, expectedSessionRevision: 7, expectedGeneration: 'generation', expectedLeaseRevision: 3, expectedExecutionId: executionId });
  fireEvent.click(screen.getByRole('button', { name: 'Reload Sandbox' })); await screen.findByText('Cleanup pending'); expect(posts()).toHaveLength(1);
});
it('uses independent server hints and escaped bounded evidence for a read-only user', async () => {
  current.canReset = current.canStop = false; current.profiles.forEach(p => p.canSelect = false); await open();
  expect(screen.getByRole('button', { name: 'Reset sandbox' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Stop command' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Command output (combined)' })); await screen.findByText('<script>inert-output</script>'); expect(document.querySelector('script')).toBeNull(); expect(screen.getByText('Command output was truncated at the bounded limit.')).toBeInTheDocument();
});
it('blocks a stale mutation until an explicit read and a fresh confirmation', async () => {
  status = 409; await open(); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); await confirm('Reset this Session sandbox?', 'Request reset'); await screen.findByText(/No request is repeated automatically/);
  expect(screen.getByRole('button', { name: 'Reset sandbox' })).toBeDisabled(); await waitFor(() => expect(screen.getByRole('button', { name: 'Reload Sandbox' })).toBeEnabled()); current.observation.securityRevision = 21; current.observation.environment!.revision = 9; status = 200;
  fireEvent.click(screen.getByRole('button', { name: 'Reload Sandbox' })); await screen.findByText('Stored: Ready'); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); await confirm('Reset this Session sandbox?', 'Request reset'); await screen.findByText('Cleanup pending'); expect(posts()).toHaveLength(2); expect(JSON.parse(String(posts()[1][1]?.body))).toMatchObject({ expectedRevision: 21, expectedLeaseRevision: 9 });
});
it('recovers an acknowledged unreadable response by read with one POST', async () => {
  malformed = true; await open(); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); await confirm('Reset this Session sandbox?', 'Request reset'); await screen.findByText(/request response could not be read/); await screen.findByText('Cleanup pending'); expect(posts()).toHaveLength(1);
});
it('clears protected controls and output after a denied command or read', async () => {
  status = 403; await open(); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); await confirm('Reset this Session sandbox?', 'Request reset'); await waitFor(() => expect(screen.queryByText('Latest stored command')).not.toBeInTheDocument()); expect(screen.queryByRole('button', { name: 'Reset sandbox' })).not.toBeInTheDocument();
  readStatus = 403; fireEvent.click(screen.getByRole('button', { name: 'Reload Sandbox' })); await waitFor(() => expect(screen.queryByText('Current environment · revision 2')).not.toBeInTheDocument()); expect(posts()).toHaveLength(1);
});
it('submits Stop with the exact current execution revision only', async () => {
  current.canStop = true; current.observation.execution!.revision = 31; current.observation.execution!.state = 'Running'; current.observation.execution!.terminationConfirmed = false; current.observation.environment!.activeExecutionId = executionId; await open();
  fireEvent.click(screen.getByRole('button', { name: 'Stop command' })); await confirm('Stop this exact command?', 'Request Stop'); await screen.findByText('Cleanup pending');
  expect(String(posts()[0][0])).toContain('/executions/' + executionId + '/stop'); expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({ expectedRevision: 11, expectedSessionRevision: 7, expectedGeneration: 'generation', expectedLeaseRevision: 3, expectedExecutionRevision: 31 });
});
it('switches only an explicitly selected readable target with exact setup witnesses', async () => {
  await open(); fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Profile for this Session' })); const option = (await screen.findAllByText('Alternate environment · revision 4 · Verified · eligible')).at(-1)!; fireEvent.click(option);
  fireEvent.click(screen.getByRole('button', { name: 'Switch profile' })); await confirm('Switch this Session profile?', 'Request profile switch'); await screen.findByText('Cleanup pending');
  expect(JSON.parse(String(posts()[0][1]?.body))).toMatchObject({ profileId: 'two', expectedProfileRevision: 4, expectedInstanceRevision: 5, expectedRevision: 11, expectedLeaseRevision: 3 });
});
it('inspects an exact historical receipt without changing current control witnesses', async () => {
  await open(); fireEvent.change(screen.getByRole('textbox', { name: 'Execution receipt ID' }), { target: { value: oldId } }); fireEvent.click(screen.getByRole('button', { name: 'Inspect exact receipt' })); await screen.findByText('printf historical'); expect(screen.getByText('printf safe-evidence')).toBeInTheDocument(); expect(posts()).toHaveLength(0);
});
it('aborts retired scope reads and destroys its confirmation', async () => {
  const rendered = await open(); fireEvent.click(screen.getByRole('button', { name: 'Reset sandbox' })); await screen.findAllByText('Reset this Session sandbox?', { selector: '.ant-modal-confirm-title' });
  const signal = fetchMock.mock.calls.find(([path]) => String(path).endsWith('/controls'))![1]!.signal!;
  current.observation.sessionId = 'next'; rendered.rerender(tree('next')); await waitFor(() => expect(signal.aborted).toBe(true)); await waitFor(() => expect(screen.queryByText('Reset this Session sandbox?', { selector: '.ant-modal-confirm-title' })).not.toBeInTheDocument()); expect(posts()).toHaveLength(0);
});
