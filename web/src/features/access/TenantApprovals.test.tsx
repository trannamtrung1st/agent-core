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
