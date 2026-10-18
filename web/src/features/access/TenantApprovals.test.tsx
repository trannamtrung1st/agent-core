import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { TenantApprovals, type TenantApproval } from './TenantApprovals';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';

const context: TenantContext = { tenant: { tenantId: 'tenant-a', name: 'Review organization', slug: 'review' }, userId: 'user', principalId: 'person', permissions: [], roles: [], revision: 8 };
const request: TenantApproval = {
  requestId: 'request-a', tenantId: 'tenant-a', runId: 'run-reference', sessionId: 'session-reference', rootActivationId: 'root-reference',
  kind: 'exceptionalGrant', status: 'pending', requestRevision: 1, securityRevision: 8, summary: 'Update conversation preferences',
  createdAt: '2026-10-17T08:00:00Z', expiresAt: '2026-10-17T09:00:00Z',
  preparedAction: { actionHash: 'a'.repeat(64), operationId: 'operation', expectedInstanceRevision: 2, expectedPersonaRevision: 1 }, decision: null, operationResult: null,
  review: { agentName: 'Safe <script> name', requesterName: 'Requester', reviewerName: null, canDecide: true, canCancel: true,
    decisionUnavailableReason: null, independentReview: false, serverNow: '2026-10-17T08:01:00Z', executionState: 'waitingDecision',
    changes: [{ field: 'language', before: 'en', after: 'vi', beforeAvailable: true, afterAvailable: true, restoresDefault: false }] }
};
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>();
function view(item = request) {
  fetchMock.mockImplementation(async (path, options) => {
    if (String(path).includes('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'POST') return response({ status: 'approved' });
    if (String(path).includes('/approvals?')) return response({ items: [item], total: 1, page: 0, pageSize: 20 });
    return response(item);
  });
  return render(<App><TenantApprovals context={context} requestId={item.requestId} /></App>);
}
beforeEach(() => { fetchMock.mockReset(); resetIdentityCsrf(); vi.stubGlobal('fetch', fetchMock); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('reviews escaped exact before/after values without exposing technical references by default', async () => {
  view(); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Safe <script> name')).toBeVisible();
  expect(within(dialog).getByText('en', { exact: true })).toBeVisible(); expect(within(dialog).getByText('vi', { exact: true })).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeEnabled();
  expect(within(dialog).getByText(/It does not assign a role/)).toBeVisible();
  expect(within(dialog).queryByText('run-reference')).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Technical references' }));
  expect(within(dialog).getByText('run-reference')).toBeVisible();
});

it('sends the exact witnessed object once and retains acknowledgment if the following detail read fails', async () => {
  view(); const dialog = await screen.findByRole('dialog');
  fireEvent.click(await within(dialog).findByRole('button', { name: 'Authorize once' }));
  const confirm = (await screen.findAllByText('Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  let committed = false;
  fetchMock.mockImplementation(async (path, options) => {
    if (String(path).includes('/csrf')) return response({ token: 'csrf' });
    if (options?.method === 'POST') { committed = true; return response({ status: 'approved' }); }
    if (String(path).includes('/approvals?')) return response({ items: [], total: 0, page: 0, pageSize: 20 });
    return response({ reasonCode: 'service_unavailable' }, 503);
  });
  fireEvent.click(within(confirm).getByRole('button', { name: 'Authorize once' }));
  expect(await within(dialog).findByText('Approved decision recorded.')).toBeVisible();
  await waitFor(() => expect(within(dialog).getByText(/temporarily unavailable/)).toBeVisible());
  expect(committed).toBe(true); expect(within(dialog).queryByRole('button', { name: 'Authorize once' })).not.toBeInTheDocument();
  const calls = fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST'); expect(calls).toHaveLength(1);
  expect(JSON.parse(calls[0][1]!.body as string)).toEqual({ decision: 'approve', actionHash: request.preparedAction.actionHash, expectedRevision: 8, expectedRequestRevision: 1 });
});

it('keeps stale decisions disabled until an explicit reload and never silently retries a rejected command', async () => {
  view(); const dialog = await screen.findByRole('dialog');
  fireEvent.click(await within(dialog).findByRole('button', { name: 'Authorize once' }));
  const confirm = (await screen.findAllByText('Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  fetchMock.mockImplementation(async (path, options) => String(path).includes('/csrf') ? response({ token: 'csrf' }) : options?.method === 'POST' ? response({ reasonCode: 'stale_revision' }, 409) : response(request));
  fireEvent.click(within(confirm).getByRole('button', { name: 'Authorize once' }));
  expect(await within(dialog).findByText(/Access has changed/)).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeEnabled());
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
});

it('shows current independence and expiry reasons without editable decisions', async () => {
  view({ ...request, review: { ...request.review, canDecide: false, canCancel: false, independentReview: true, decisionUnavailableReason: 'independent_reviewer_required' } });
  const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText(/A different qualified person/)).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeDisabled();
});

it('shows Applied only for a completed receipt, and clears private details on organization change', async () => {
  const applied: TenantApproval = { ...request, status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'operation_completed', recordedAt: request.createdAt } };
  const rendered = view(applied); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Applied', { exact: true })).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Authorize once' })).not.toBeInTheDocument();
  fetchMock.mockImplementation(() => new Promise<Response>(() => {}));
  rendered.rerender(<App><TenantApprovals context={{ ...context, tenant: { ...context.tenant, tenantId: 'tenant-b' } }} requestId="request-b" /></App>);
  expect(screen.queryByText('Safe <script> name')).not.toBeInTheDocument(); expect(screen.queryByText('vi', { exact: true })).not.toBeInTheDocument();
});

it('separates an uncertain credential check from denial and refreshes the saved result without another decision', async () => {
  const uncertain: TenantApproval = { ...request, status: 'approved', preparedAction: { ...request.preparedAction, operation: 'check_credential_connection' },
    operationResult: { outcome: 'indeterminate', reasonCode: 'credential_check_uncertain', recordedAt: request.createdAt } };
  view(uncertain); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Outcome uncertain', { exact: true })).toBeVisible();
  expect(within(dialog).getByText(/This operation will not run again/)).toBeVisible();
  expect(within(dialog).queryByText('Not applied', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).queryByRole('button', { name: 'Authorize once' })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Refresh details' })).toBeEnabled());
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});

it('describes a completed credential health check without claiming a configuration change', async () => {
  view({ ...request, status: 'approved', preparedAction: { ...request.preparedAction, operation: 'check_credential_connection' },
    operationResult: { outcome: 'completed', reasonCode: 'credential_reconnect_required', recordedAt: request.createdAt } });
  const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Check completed', { exact: true })).toBeVisible();
  expect(within(dialog).getByText(/credential requires reconnection/)).toBeVisible();
  expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
});

it('inspects exact Sandbox parameters with units and escaped scripts while approved execution remains unrecorded', async () => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Run exact Sandbox command',
    preparedAction: { ...request.preparedAction, operation: 'run_sandbox_command' },
    review: { ...request.review, changes: [
      { field: 'command', before: null, after: 'printf "<script>alert(1)</script>"', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'memoryMiB', before: null, after: 512, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'background', before: null, after: false, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'profileInitialization', before: null, after: 'echo <img src=x onerror=alert(1)>', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } });
  const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved command' });
  expect(within(saved).getByText('Command', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Memory (MiB)', { exact: true })).toBeVisible();
  expect(within(saved).getByText('512', { exact: true })).toBeVisible();
  expect(within(saved).getByText('No', { exact: true })).toBeVisible();
  expect(within(saved).getByText('echo <img src=x onerror=alert(1)>', { exact: true })).toBeVisible();
  expect(saved.querySelector('script, img')).toBeNull();
  expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).getByText(/No effect result recorded yet/)).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
});

it.each(['stop_sandbox_process', 'reset_sandbox_environment'])('shows %s consent independently from termination', async operation => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Exact Sandbox cleanup',
    preparedAction: { ...request.preparedAction, operation },
    operationResult: { outcome: 'completed', reasonCode: operation === 'stop_sandbox_process' ? 'sandbox_stop_requested' : 'sandbox_reset_requested', recordedAt: '2026-10-18T01:00:00Z' },
    review: { ...request.review, changes: [
      { field: 'sandboxControl', before: null, after: 'Request exact cleanup', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'generation', before: null, after: 'exact-generation', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'cleanupConsequence', before: null, after: 'Writes may continue until cleanup is confirmed.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'fileRetention', before: null, after: 'Canonical files are retained.', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } });
  const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved control request' });
  expect(within(saved).getByText('Environment generation', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Cleanup behavior', { exact: true })).toBeVisible();
  expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).getByText('Request recorded', { exact: true })).toBeVisible();
  expect(within(dialog).getByText(/writes may continue until/)).toBeVisible();
  expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
});
