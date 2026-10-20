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

const mutationRequest = (tool: string, args: unknown): TenantApproval => ({ ...request, summary: 'Change exact workspace paths', preparedAction: { ...request.preparedAction, operation: 'mutate_workspace' },
  review: { ...request.review, changes: [
    { field: 'workspaceMutationTool', before: null, after: tool, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'workspaceMutationArguments', before: null, after: JSON.stringify(args), beforeAvailable: false, afterAvailable: true, restoresDefault: false }
  ] } });
it.each(['review', 'exceptionalGrant'] as const)('reviews exact workspace literal and %s confirmation without exposing current bytes or granting read access', async kind => {
  const content = '<script>literal only</script>\nExact UTF-8: thử nghiệm';
  view({ ...mutationRequest('workspace.write', { path: '/working/very-long-exact-review-path.txt', content }), kind });
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByText('/working/very-long-exact-review-path.txt')).toBeVisible();
  expect(within(saved).getByRole('textbox', { name: 'Exact replacement text' })).toHaveValue(content);
  expect(within(saved).getByRole('textbox', { name: 'Exact replacement text' })).toHaveAttribute('readonly');
  expect(within(saved).queryByText('Before')).not.toBeInTheDocument(); expect(saved.querySelector('script')).toBeNull();
  fireEvent.click(within(dialog).getByRole('button', { name: kind === 'review' ? 'Approve' : 'Authorize once' }));
  const confirm = (await screen.findAllByText(kind === 'review' ? 'Approve?' : 'Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/No role, file read, recipient download or later mutation authority/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it('keeps ordered batch paths and recursive intent visible and disables incomplete saved mutation decisions', async () => {
  const rendered = view(mutationRequest('workspace.batch', { operations: [{ op: 'mkdir', path: '/home/new' }, { op: 'delete', path: '/home/old', recursive: true }] }));
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getAllByRole('listitem')).toHaveLength(2); expect(within(saved).getByText('Yes — recursive deletion')).toBeVisible();
  expect(within(saved).getByText('Partial effects can remain')).toBeVisible(); rendered.unmount();
  view(mutationRequest('workspace.copy', { source: '/home/a', destination: '/working/b', unexpected: true }));
  const invalid = await screen.findByRole('dialog'); expect(await within(invalid).findByText('Saved workspace changes unavailable')).toBeVisible();
  expect(within(invalid).getByRole('button', { name: 'Authorize once' })).toBeDisabled(); expect(within(invalid).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it.each(['completed', 'denied', 'indeterminate'])('labels a workspace %s result separately from approval', async outcome => {
  view({ ...mutationRequest('workspace.mkdir', { path: '/working/new' }), status: 'approved', operationResult: { outcome, reasonCode: outcome === 'completed' ? 'workspace_mutation_completed' : outcome === 'denied' ? 'workspace_mutation_not_applied' : 'workspace_mutation_uncertain', recordedAt: request.createdAt } });
  const result = await screen.findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText(outcome === 'completed' ? 'Workspace changes completed' : outcome === 'denied' ? 'Workspace changes not applied' : 'Workspace outcome uncertain')).toBeVisible();
});
it('shows proposed patch literals and empty replacement explicitly without inventing a current-file Before value', async () => {
  view(mutationRequest('workspace.patch', { path: '/home/reviewed.txt', edits: [{ oldText: 'Exact supplied match', newText: '' }, { oldText: '<script>match only</script>', newText: 'literal replacement' }] }));
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByRole('textbox', { name: 'Find exact text, edit 1' })).toHaveValue('Exact supplied match');
  expect(within(saved).getByRole('textbox', { name: 'Replacement text, edit 1' })).toHaveValue(''); expect(within(saved).getByText('Empty text')).toBeVisible();
  expect(within(saved).getByRole('textbox', { name: 'Find exact text, edit 2' })).toHaveValue('<script>match only</script>');
  expect(within(saved).getAllByRole('textbox')).toHaveLength(4); expect(within(saved).queryByText('Before')).not.toBeInTheDocument();
});
it('keeps a full-size saved literal intact and shows exact cross-root copy paths', async () => {
  const content = 'a'.repeat(32768); const rendered = view(mutationRequest('workspace.write', { path: '/working/large.txt', content }));
  const text = await screen.findByRole('textbox', { name: 'Exact replacement text' }); expect(text).toHaveValue(content); rendered.unmount();
  view(mutationRequest('workspace.copy', { source: '/home/exact-source.bin', destination: '/working/exact-copy.bin' }));
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByText('/home/exact-source.bin')).toBeVisible(); expect(within(saved).getByText('/working/exact-copy.bin')).toBeVisible();
  expect(within(saved).queryByRole('textbox')).not.toBeInTheDocument(); expect(within(saved).getByText('Partial effects can remain')).toBeVisible();
});

const artifactVerificationRequest: TenantApproval = { ...request, kind: 'review', summary: 'Verify file metadata',
  preparedAction: { ...request.preparedAction, operation: 'verify_artifact' }, review: { ...request.review, changes: [
    { field: 'artifactDisplayName', before: null, after: 'private-report.md', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'artifactStoredByteSize', before: null, after: 200, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'artifactChecksum', before: null, after: 'b'.repeat(64), beforeAvailable: false, afterAvailable: true, restoresDefault: false }
  ] } };
it('reviews exact file metadata with a separate checksum disclosure and no body or download grant', async () => {
  view(artifactVerificationRequest); const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved read' });
  expect(within(saved).getByText('private-report.md')).toBeVisible(); expect(within(saved).getByText('Stored bytes')).toBeVisible();
  expect(within(saved).queryByText('Before')).not.toBeInTheDocument(); expect(within(saved).queryByText('b'.repeat(64))).not.toBeInTheDocument();
  expect(within(dialog).queryByRole('textbox', { name: 'Exact file content' })).not.toBeInTheDocument();
  expect(within(dialog).queryByRole('button', { name: /Download/ })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Technical references' }));
  expect(await within(dialog).findByText('b'.repeat(64))).toBeVisible();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
  const confirm = (await screen.findAllByText('Approve?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/no file creation, contents, download access or later-read authority/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
  fetchMock.mockImplementation(async path => String(path).includes('/approvals?') ? response({ items: [], total: 0, page: 0, pageSize: 20 }) : response({ reasonCode: 'approval_unavailable' }, 403));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).queryByText('private-report.md')).not.toBeInTheDocument());
  expect(await within(dialog).findByText(/This approval is unavailable or you no longer have access/)).toBeVisible();
});
it.each(['completed', 'denied'])('labels an exact metadata %s result as consent rather than file publication', async outcome => {
  view({ ...artifactVerificationRequest, status: 'approved', operationResult: { outcome, reasonCode: 'artifact_metadata_read_authorized', recordedAt: request.createdAt } });
  const dialog = await screen.findByRole('dialog'); const result = await within(dialog).findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText(outcome === 'completed' ? 'Metadata inspection authorized' : 'Metadata not authorized')).toBeVisible();
  expect(within(result).queryByText('Applied')).not.toBeInTheDocument();
});

const artifactRequest: TenantApproval = { ...request, summary: 'Publish exact file', preparedAction: { ...request.preparedAction, operation: 'publish_artifact' },
  review: { ...request.review, changes: [
    { field: 'artifactDisplayName', before: null, after: 'reviewed-report.md', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'artifactContentType', before: null, after: 'text/markdown', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'artifactByteSize', before: null, after: new TextEncoder().encode('# Exact file\n<script>alert("literal")</script>\nUTF-8: thử nghiệm').length, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    { field: 'artifactContent', before: null, after: '# Exact file\n<script>alert("literal")</script>\nUTF-8: thử nghiệm', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
  ] } };

it.each(['review', 'exceptionalGrant'] as const)('reviews complete literal file content and confirms %s publication without granting read access', async kind => {
  view({ ...artifactRequest, kind }); const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved file' });
  const content = within(saved).getByRole('textbox', { name: 'Exact file content' });
  expect(content).toHaveValue(artifactRequest.review.changes[3].after as string); expect(content).toHaveAttribute('readonly');
  expect(saved.querySelector('script, img')).toBeNull(); expect(within(saved).queryByText('Before')).not.toBeInTheDocument();
  expect(within(saved).getByText('UTF-8 bytes')).toBeVisible();
  const label = kind === 'review' ? 'Approve' : 'Authorize once';
  fireEvent.click(within(dialog).getByRole('button', { name: label }));
  const confirm = (await screen.findAllByText(label + '?'))[0].closest('[role="dialog"]') as HTMLElement;
  expect(within(confirm).getByText(/only this exact saved file once/)).toBeInTheDocument();
  expect(within(confirm).getByText(/No role or file read access is granted/)).toBeInTheDocument();
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});

it.each(['completed', 'denied'])('labels an Artifact %s receipt separately from the approval decision', async outcome => {
  view({ ...artifactRequest, status: 'approved', operationResult: { outcome, reasonCode: outcome === 'completed' ? 'operation_completed' : 'grantor_authority_lost', recordedAt: request.createdAt } });
  const dialog = await screen.findByRole('dialog'); const result = await within(dialog).findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText(outcome === 'completed' ? 'File published' : 'File not published')).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Authorize once' })).not.toBeInTheDocument();
});

it('removes saved file content when current approval read access is lost', async () => {
  view(artifactRequest); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByRole('textbox', { name: 'Exact file content' })).toBeVisible();
  fetchMock.mockImplementation(async path => String(path).includes('/approvals?') ? response({ items: [], total: 0, page: 0, pageSize: 20 }) : response({ reasonCode: 'approval_unavailable' }, 403));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).queryByRole('textbox', { name: 'Exact file content' })).not.toBeInTheDocument());
});

it('reviews exact Browser parameters with clear consent and stale-target recovery in the shared command inspector', async () => {
  const browser = { ...request, kind: 'review' as const, summary: 'Review exact Browser command',
    preparedAction: { ...request.preparedAction, operation: 'browser_command' },
    review: { ...request.review, canDecide: false, decisionUnavailableReason: 'browser_target_changed', changes: [
      { field: 'browserTool', before: null, after: 'browser.navigate', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'browserArguments', before: null, after: 'URL: https://example.test/<script>\nOperation: goto', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'browserLimit', before: null, after: 'Approval records exact consent. Dispatch is unavailable.', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } };
  view(browser); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Exact saved command')).toBeVisible();
  expect(within(dialog).getByText('Exact parameters')).toBeVisible();
  expect(within(dialog).getByText('URL: https://example.test/<script> Operation: goto')).toBeVisible();
  expect(within(dialog).getByText('Execution boundary')).toBeVisible();
  expect(within(dialog).getByText(/The Browser target changed/)).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Approve' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
  expect(within(dialog).queryByText('Before')).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});

it.each(['completed', 'indeterminate'])('describes a recorded Browser %s outcome without claiming application success or offering replay', async outcome => {
  const reasonCode = outcome === 'completed' ? 'browser_command_completed' : 'browser_command_uncertain';
  view({ ...request, kind: 'review', status: 'approved', preparedAction: { ...request.preparedAction, operation: 'browser_command' },
    operationResult: { outcome, reasonCode, recordedAt: request.createdAt },
    review: { ...request.review, canDecide: false, canCancel: false, executionState: 'recorded' } });
  const dialog = await screen.findByRole('dialog'); const result = await within(dialog).findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText(outcome === 'completed' ? 'Command completed' : 'Outcome uncertain')).toBeVisible();
  expect(within(result).getByText(outcome === 'completed' ? /has not been independently verified/ : /This request will not run again/)).toBeVisible();
  expect(within(result).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});

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

it.each(['inspect_sandbox_profiles', 'inspect_sandbox_status', 'inspect_sandbox_process'])('shows %s as exact read consent without claiming a mutation or delivered output', async operation => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Inspect stored Sandbox observation',
    preparedAction: { ...request.preparedAction, operation },
    operationResult: { outcome: 'completed', reasonCode: 'sandbox_read_authorized', recordedAt: '2026-10-18T03:00:00Z' },
    review: { ...request.review, changes: [
      { field: 'sandboxInspection', before: null, after: 'Read this exact stored observation', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readProfiles', before: null, after: 'Readable source (revision 1)', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readDisclosure', before: null, after: 'Return bounded stored stdout/stderr for the exact receipt.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readLimit', before: null, after: 'One stored observation; current access is checked before delivery.', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } });
  const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved read' });
  expect(within(saved).getByText('Data disclosed', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Read boundary', { exact: true })).toBeVisible();
  expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).getByText('Read authorized', { exact: true })).toBeVisible();
  expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
});

it('shows exact Credential metadata scope and page as a read, with escaped names and no implied use authority', async () => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Read bound Credential metadata',
    preparedAction: { ...request.preparedAction, operation: 'inspect_credential_metadata' },
    operationResult: { outcome: 'completed', reasonCode: 'credential_metadata_read_authorized', recordedAt: '2026-10-18T12:00:00Z' },
    review: { ...request.review, changes: [
      { field: 'credentialMetadata', before: null, after: 'Bound Credential metadata', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readCredentials', before: null, after: 'Account <script>not markup</script> (account)', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readPage', before: null, after: 'Up to 1 record, starting from the first alias.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readDisclosure', before: null, after: 'Return aliases, names, kinds and non-secret metadata. No protected values or use permission.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readLimit', before: null, after: 'One exact bounded page. Later reads require current policy again.', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } });
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved read' });
  expect(within(saved).getByText('Credential scope', { exact: true })).toBeVisible(); expect(within(saved).getByText('Page', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Account <script>not markup</script> (account)', { exact: true })).toBeVisible(); expect(saved.querySelector('script')).toBeNull();
  expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument(); expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).getByText('Read authorized', { exact: true })).toBeVisible(); expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
  expect(within(dialog).getByText(/no protected value or use permission is granted/)).toBeVisible();
});

it('presents workspace consent as an exact saved read with a path and truthful recorded result', async () => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Read exact workspace observation',
    preparedAction: { ...request.preparedAction, operation: 'inspect_workspace' },
    operationResult: { outcome: 'completed', reasonCode: 'workspace_read_authorized', recordedAt: '2026-10-19T01:00:00Z' },
    review: { ...request.review, changes: [
      { field: 'path', before: null, after: '/home/notes/<script>literal</script>-試験-thử-nghiệm.txt', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readPage', before: null, after: 'One exact current UTF-8 file', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readDisclosure', before: null, after: 'One saved observation. No file changes or later discovery.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readLimit', before: null, after: 'Only this exact continuation.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
    ] } });
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved read' });
  expect(within(saved).getByText('Exact path', { exact: true })).toBeVisible(); expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument(); expect(saved.querySelector('script')).toBeNull();
  expect(within(dialog).getByText('Read authorized', { exact: true })).toBeVisible(); expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
});

it('reviews complete Word sections as inert literal fields and retains protected confirmation', async () => {
  view({ ...mutationRequest('office.document.create', { path: '/home/exact.docx', title: '<script>literal title</script>', sections: [{ heading: 'Exact heading', paragraphs: ['', 'thử nghiệm'], table: [['zero', '']], pageBreakBefore: false }] }), kind: 'review' });
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByText('Create Word document')).toBeVisible();
  expect(within(saved).getByRole('textbox', { name: 'Document title' })).toHaveValue('<script>literal title</script>');
  expect(within(saved).getByRole('textbox', { name: 'Section 1 · paragraphs in order' })).toHaveValue('Paragraph 1\n(empty text)\n\nParagraph 2\nthử nghiệm');
  expect(within(saved).getByRole('textbox', { name: 'Section 1 · table cells in row order' })).toHaveValue('Row 1, column 1\nzero\n\nRow 1, column 2\n(empty text)');
  expect(within(saved).getByRole('textbox', { name: 'Section 1 · page break before' })).toHaveValue('No'); expect(saved.querySelector('script')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
  const confirm = (await screen.findAllByText('Approve?'))[0].closest('[role="dialog"]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it('preserves typed zero, false and stored formulas in full exact Excel review', async () => {
  view(mutationRequest('office.spreadsheet.edit', { path: '/working/exact.xlsx', operations: [{ operation: 'set_cells', sheet: '試験', range: 'A1:C1', cells: [[{ type: 'number', number: 0 }, { type: 'boolean', boolean: false }, { type: 'text', text: '', formula: '1+1', format: 'general' }]] }] }));
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByText('Edit Excel workbook')).toBeVisible();
  expect(within(saved).getByRole('textbox', { name: 'Edit 1 · Sheet' })).toHaveValue('試験');
  expect(within(saved).getByRole('textbox', { name: 'Edit 1 · Exact range' })).toHaveValue('A1:C1');
  expect(within(saved).getByRole('textbox', { name: 'Edit 1 · Typed cells in range order' })).toHaveValue('Row 1, column 1\nType: number\nNumber: 0\n\nRow 1, column 2\nType: boolean\nBoolean: No\n\nRow 1, column 3\nType: text\nText: (empty text)\nStored formula: 1+1\nNumber format: general');
  expect(within(saved).getByText(/Stored formulas are not calculated/)).toBeVisible();
});
it('blocks malformed or hidden Office fields while preserving cancellation', async () => {
  view(mutationRequest('office.document.create', { path: '/home/a.docx', sections: [{ paragraphs: ['safe'], grantId: 'hidden' }] }));
  const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Saved workspace changes unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('shows Word edit order, selectors, styles and explicit empty replacement', async () => {
  view(mutationRequest('office.document.edit', { path: '/home/exact.docx', operations: [{ operation: 'replace_paragraph', selector: 'p:exact', text: '' }, { operation: 'set_heading_style', selector: 'p:other', style: 'Heading2' }] }));
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByRole('textbox', { name: 'Edit 1 · Exact selector' })).toHaveValue('p:exact');
  expect(within(saved).getByRole('textbox', { name: 'Edit 1 · Replacement text' })).toHaveValue(''); expect(within(saved).getByText('Empty text')).toBeVisible();
  expect(within(saved).getByRole('textbox', { name: 'Edit 2 · Paragraph style' })).toHaveValue('Heading2');
});
it('keeps every Excel create sheet and typed value separately reviewable', async () => {
  view(mutationRequest('office.spreadsheet.create', { path: '/working/exact.xlsx', sheets: [{ name: 'First', rows: [[{ type: 'text', text: 'Exact value' }]] }, { name: 'Second', rows: [] }] }));
  const saved = await screen.findByRole('region', { name: 'Exact saved workspace changes' });
  expect(within(saved).getByRole('textbox', { name: 'Sheet 1 · name' })).toHaveValue('First'); expect(within(saved).getByRole('textbox', { name: 'Sheet 2 · name' })).toHaveValue('Second');
  expect(within(saved).getByRole('textbox', { name: 'Sheet 1 · typed cells in row order' })).toHaveValue('Row 1, column 1\nType: text\nText: Exact value');
  expect(within(saved).getByRole('textbox', { name: 'Sheet 2 · typed cells in row order' })).toHaveValue('No stored cells');
});

const sourceRequest: TenantApproval = { ...artifactRequest, review: { ...artifactRequest.review, changes: [
  ...artifactRequest.review.changes.slice(0, 2),
  ...Object.entries({ artifactByteSize: 0, artifactSourcePath: '/working/試験-thử-nghiệm.docx', artifactChecksum: 'a'.repeat(64), publicationDisclosure: 'Copy only the exact saved workspace bytes. Publication grants no file-read or download access.' }).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false }))
] } };
it('reviews workspace publication metadata without an inline body and confirms exact source checks', async () => {
  view(sourceRequest); const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved file' });
  expect(within(saved).getByText('Exact source path')).toBeVisible(); expect(within(saved).getByText('/working/試験-thử-nghiệm.docx')).toBeVisible();
  expect(within(saved).getByText('Stored bytes')).toBeVisible(); expect(within(saved).getByText('0', { exact: true })).toBeVisible();
  expect(within(saved).queryByRole('textbox')).not.toBeInTheDocument(); expect(within(saved).queryByText('Saved field')).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Authorize once' }));
  const confirm = (await screen.findAllByText('Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/Current source and access are checked again/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['missing', 'duplicate', 'mixed', 'unknown', 'hidden', 'invalid-size'])('withholds %s source publication preview and decisions but keeps cancellation', async failure => {
  let changes = sourceRequest.review.changes;
  if (failure === 'missing') changes = changes.filter(c => c.field !== 'artifactSourcePath');
  if (failure === 'duplicate') changes = [...changes, changes[0]];
  if (failure === 'mixed') changes = [...changes, artifactRequest.review.changes[3]];
  if (failure === 'unknown') changes = [...changes, { ...changes[0], field: 'privateWitness' }];
  if (failure === 'hidden') changes = changes.map(c => c.field === 'artifactChecksum' ? { ...c, afterAvailable: false } : c);
  if (failure === 'invalid-size') changes = changes.map(c => c.field === 'artifactByteSize' ? { ...c, after: -1 } : c);
  view({ ...sourceRequest, review: { ...sourceRequest.review, changes } }); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Saved file publication unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});


it('shows exact Attachment source and page as escaped read consent with separate delivery authority', async () => {
  view({ ...request, kind: 'review', status: 'approved', summary: 'Read attached file',
    preparedAction: { ...request.preparedAction, operation: 'read_attachment' },
    operationResult: { outcome: 'completed', reasonCode: 'attachment_read_authorized', recordedAt: '2026-10-19T04:00:00Z' },
    review: { ...request.review, changes: [
      { field: 'attachmentDisplayName', before: null, after: '<script>literal</script>.xlsx', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'attachmentContentType', before: null, after: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'attachmentByteSize', before: null, after: 0, beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'attachmentChecksum', before: null, after: 'c'.repeat(64), beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readPage', before: null, after: 'Excel: sheet 試験; exact range A1:B2.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readDisclosure', before: null, after: 'Only this saved file and window. No file changes or recipient download access.', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'readLimit', before: null, after: 'One bounded observation in this continuation.', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } });
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved read' });
  expect(within(saved).getByText('<script>literal</script>.xlsx', { exact: true })).toBeVisible();
  expect(within(saved).getByText('File name', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Stored bytes', { exact: true })).toBeVisible();
  expect(within(saved).getByText('0', { exact: true })).toBeVisible();
  expect(within(saved).getByText('Excel: sheet 試験; exact range A1:B2.')).toBeVisible();
  expect(within(saved).queryByText('Before', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('Saved field', { exact: true })).not.toBeInTheDocument();
  expect(within(saved).queryByText('c'.repeat(64))).not.toBeInTheDocument();
  expect(within(dialog).getByText('Read authorized', { exact: true })).toBeVisible();
  expect(within(dialog).queryByText('Applied', { exact: true })).not.toBeInTheDocument();
  expect(document.querySelector('script')).toBeNull();
});

it.each(['/agent/resources/試験-thử-nghiệm.docx', '/agent/instance/resources/report.docx', '/attachments/11111111-1111-1111-1111-111111111111'])('reviews converted source publication %s through the shared inspector and confirmation', async path => {
  view({ ...sourceRequest, review: { ...sourceRequest.review, changes: [...sourceRequest.review.changes.map(c => c.field === 'artifactSourcePath' ? { ...c, after: path } : c), ...(path.startsWith('/attachments/') ? [{ ...sourceRequest.review.changes[0], field: 'artifactSourceName', after: '原本 thử nghiệm.txt' }] : [])] } });
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved file' });
  expect(within(saved).getByText(path)).toBeVisible(); if (path.startsWith('/attachments/')) expect(within(saved).getByText('原本 thử nghiệm.txt')).toBeVisible(); expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeEnabled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Authorize once' }));
  const confirm = (await screen.findAllByText('Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/exact saved source bytes/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
});
it.each(['/attachments/00000000-0000-0000-0000-000000000000', '/attachments/11111111-1111-1111-1111-111111111111/file.txt', '/agent/resources/../secret'])('keeps malformed source %s unavailable with cancellation', async path => {
  view({ ...sourceRequest, review: { ...sourceRequest.review, changes: [...sourceRequest.review.changes.map(c => c.field === 'artifactSourcePath' ? { ...c, after: path } : c), ...(path.startsWith('/attachments/') ? [{ ...sourceRequest.review.changes[0], field: 'artifactSourceName', after: '原本 thử nghiệm.txt' }] : [])] } });
  const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Saved file publication unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});

it('withholds a saved Attachment publication missing its canonical original filename', async () => {
  const path = '/attachments/11111111-1111-1111-1111-111111111111';
  view({ ...sourceRequest, review: { ...sourceRequest.review, changes: sourceRequest.review.changes.map(c => c.field === 'artifactSourcePath' ? { ...c, after: path } : c) } });
  const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Saved file publication unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});

const browserFilesRequest = (browserArtifacts: NonNullable<TenantApproval['operationResult']>['browserArtifacts']): TenantApproval => ({
  ...request, kind: 'review', status: 'approved', preparedAction: { ...request.preparedAction, operation: 'browser_command' },
  operationResult: { outcome: 'completed', reasonCode: 'browser_command_completed', recordedAt: request.createdAt, ...(browserArtifacts ? { browserArtifacts } : {}) },
  review: { ...request.review, canDecide: false, canCancel: false, executionState: 'recorded' },
});
const captureRef = { kind: 'capture' as const, artifactId: 'capture-file-reference' };
it.each([
  ['published', null, [captureRef], 'Files published', '1 captured image saved to this conversation.'],
  ['published', 'download_limit', [captureRef, { kind: 'download', artifactId: 'download-file-reference' }], 'Some files published', 'The download size or task limit prevented publication of every download.'],
  ['empty', null, [], 'No files published', 'The command produced no files for publication.'],
  ['unavailable', 'permission_denied', [], 'File publication unavailable', 'Current permission no longer allowed file publication.'],
  ['unavailable', 'artifact_quota_exceeded', [], 'File publication unavailable', 'The Session file storage limit prevented publication.'],
  ['unavailable', 'future_reason', [], 'File publication unavailable', 'Some requested files could not be published.'],
] as const)('keeps native completion separate from %s file publication (%s)', async (status, reasonCode, files, title, description) => {
  view(browserFilesRequest({ status, reasonCode, files: [...files] }));
  const publication = await screen.findByRole('region', { name: 'Browser files' });
  expect(within(publication).getByText(title)).toBeVisible(); expect(within(publication).getByText(description)).toBeVisible();
  const result = screen.getByRole('region', { name: 'Operation result' }); expect(within(result).getByText('Command completed')).toBeVisible();
  expect(within(result).getByText(/has not been independently verified/)).toBeVisible();
  expect(within(publication).queryByRole('button', { name: 'Inspect published files' }) !== null).toBe(status === 'published');
  expect(within(publication).queryByText(captureRef.artifactId)).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.some(([path]) => String(path).includes('/artifacts/'))).toBe(false);
  expect(fetchMock.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
});
it('leaves legacy Browser receipts without an invented publication outcome', async () => {
  view(browserFilesRequest(undefined)); await screen.findByRole('region', { name: 'Operation result' });
  expect(screen.queryByRole('region', { name: 'Browser files' })).not.toBeInTheDocument();
});
it('inspects published files deliberately with independent current access, recovery, and read-state retirement on refresh', async () => {
  const item = browserFilesRequest({ status: 'published', reasonCode: null, files: [captureRef] }); view(item);
  const dialog = await screen.findByRole('dialog'); const publication = await within(dialog).findByRole('region', { name: 'Browser files' });
  let allowed = false;
  fetchMock.mockImplementation(async path => {
    if (String(path).includes('/artifacts/')) return allowed ? response({ artifactId: captureRef.artifactId, sessionId: item.sessionId, displayName: 'current-capture.png', contentType: 'image/png', byteSize: 40 }) : response({ reasonCode: 'resource_unavailable' }, 403);
    return String(path).includes('/approvals?') ? response({ items: [item], total: 1, page: 0, pageSize: 20 }) : response(item);
  });
  fireEvent.click(within(publication).getByRole('button', { name: 'Inspect published files' }));
  expect(await within(publication).findByText('File unavailable')).toBeVisible();
  expect(within(publication).queryByRole('button', { name: /Download/ })).not.toBeInTheDocument();
  allowed = true; fireEvent.click(within(publication).getByRole('button', { name: 'Retry file' }));
  expect(await within(publication).findByText('current-capture.png')).toBeVisible();
  expect(within(publication).getByRole('button', { name: 'Download current-capture.png' })).toBeEnabled();
  expect(within(publication).getByRole('button', { name: 'Preview current-capture.png' })).toBeEnabled();
  const fileCalls = fetchMock.mock.calls.filter(([path]) => String(path).includes('/artifacts/'));
  expect(fileCalls).toHaveLength(2); expect(fileCalls.every(([path]) => String(path) === '/api/v2/tenants/tenant-a/sessions/session-reference/artifacts/capture-file-reference')).toBe(true);
  allowed = false; fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).queryByText('current-capture.png')).not.toBeInTheDocument());
  const refreshed = within(dialog).getByRole('region', { name: 'Browser files' });
  fireEvent.click(within(refreshed).getByRole('button', { name: 'Inspect published files' }));
  expect(await within(refreshed).findByText('File unavailable')).toBeVisible();
  expect(within(refreshed).queryByText('current-capture.png')).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});

const backgroundRequest = (): TenantApproval => ({ ...request, summary: 'Start exact background work', preparedAction: { ...request.preparedAction, operation: 'start_background_work' },
  review: { ...request.review, changes: Object.entries({ backgroundObjective: '<script>literal task only</script>\nExact Unicode task: thử nghiệm.', backgroundTitle: 'Reviewed task',
    backgroundReportCompletion: false, backgroundModel: 'synthetic', backgroundPrimaryPermission: 'agent-runs.create' }).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } });
it.each(['review', 'exceptionalGrant'] as const)('reviews one exact background task and %s consequences through the shared inspector', async kind => {
  view({ ...backgroundRequest(), kind });
  const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved background task' });
  expect(within(saved).getByText(/Exact Unicode task: thử nghiệm/)).toBeVisible();
  expect(within(saved).getByText('No')).toBeVisible(); expect(within(saved).getByText('agent-runs.create')).toBeVisible();
  expect(within(saved).queryByText('Before')).not.toBeInTheDocument(); expect(saved.querySelector('script')).toBeNull();
  fireEvent.click(within(dialog).getByRole('button', { name: kind === 'review' ? 'Approve' : 'Authorize once' }));
  const confirm = (await screen.findAllByText(kind === 'review' ? 'Approve?' : 'Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/one child Session and Run/)).toBeVisible());
  expect(within(confirm).getByText(/No role, child read access or later-task authority/)).toBeVisible();
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it('withholds background decisions when the exact completion preference is missing while preserving cancellation', async () => {
  const item = backgroundRequest(); item.review.changes = item.review.changes.filter(change => change.field !== 'backgroundReportCompletion'); view(item);
  const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByRole('button', { name: 'Authorize once' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('distinguishes created background work from task completion', async () => {
  view({ ...backgroundRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'background_created', recordedAt: request.createdAt } });
  const dialog = await screen.findByRole('dialog'); const result = await within(dialog).findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText('Background work created')).toBeVisible(); expect(within(result).getByText(/Execution status and results remain in Background work/)).toBeVisible();
  expect(within(result).queryByText('Task completed')).not.toBeInTheDocument();
});

const completionReportRequest = (): TenantApproval => ({ ...request, destinationSessionId: '12345678-1234-1234-1234-123456789abc', summary: 'Deliver exact background completion', preparedAction: { ...request.preparedAction, operation: 'deliver_background_completion' },
  review: { ...request.review, changes: Object.entries({ reportDestination: 'Original conversation', reportContent: 'Background work: <script>literal completion</script>\nKết quả cần xem xét.', reportModel: 'synthetic' })
    .map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } });
it.each(['review', 'exceptionalGrant'] as const)('reviews saved completion and independent %s report-creation consequences', async kind => {
  view({ ...completionReportRequest(), kind }); const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved completion report' });
  expect(within(saved).getByText(/Kết quả cần xem xét/)).toBeVisible(); expect(saved.querySelector('script')).toBeNull();
  expect(within(dialog).getByRole('link', { name: 'Open destination conversation' })).toHaveAttribute('href', '/t/tenant-a/c/12345678-1234-1234-1234-123456789abc');
  fireEvent.click(within(dialog).getByRole('button', { name: kind === 'review' ? 'Approve' : 'Authorize once' }));
  const confirm = (await screen.findAllByText(kind === 'review' ? 'Approve?' : 'Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/one report Run for this exact saved completion/)).toBeVisible());
  expect(within(confirm).getByText(/does not confirm delivery/)).toBeVisible(); fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['content', 'destination'] as const)('withholds completion decisions for missing %s without removing cancellation', async missing => {
  const item = completionReportRequest(); if (missing === 'content') item.review.changes = item.review.changes.filter(c => c.field !== 'reportContent'); else delete item.destinationSessionId;
  view(item); const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByRole('button', { name: 'Authorize once' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('separates report Run creation from completion delivery', async () => {
  view({ ...completionReportRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'completion_report_created', recordedAt: request.review.serverNow } });
  const result = await screen.findByRole('region', { name: 'Operation result' }); expect(within(result).getByText('Report Run created')).toBeVisible();
  expect(within(result).getByText(/Delivery and recipient access remain separately checked/)).toBeVisible(); expect(within(result).queryByText('Report delivered')).not.toBeInTheDocument();
});

const completionReadRequest = (): TenantApproval => {
  const parent = '12345678-1234-1234-1234-123456789abc'; const child = '22345678-1234-1234-1234-123456789abc';
  const initial = '32345678-1234-1234-1234-123456789abc'; const source = '42345678-1234-1234-1234-123456789abc';
  const after: Record<string, string> = { completionInitialRunId: initial, completionSourceRunId: source, completionSessionId: child,
    completionObjective: 'Use the exact owned result', completionObservationHash: 'b'.repeat(64), completionMetadata: '[]',
    readDisclosure: 'Return this exact result without file bytes.', readLimit: 'Use only this unchanged observation in the same task.',
    completionObservation: JSON.stringify({ untrusted: true, tenantCompletionRead: true, backgroundSessionId: child, parentSessionId: parent, agentRunId: initial,
      sourceAgentRunId: source, status: 'Completed', outcome: 'Response', summary: '<script>saved result only</script>\nExact UTF-8: thử nghiệm',
      progress: null, updatedAtUtc: request.createdAt, attentionRequired: false, failureCode: null, artifacts: [], artifactsHasMore: false }) };
  return { ...request, sessionId: parent, kind: 'review', summary: 'Read exact background result', preparedAction: { ...request.preparedAction, operation: 'inspect_background_completion' },
    review: { ...request.review, changes: Object.entries(after).map(([field, value]) => ({ field, before: null, after: value, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } };
};
it('reviews literal completed-child evidence with distinct initial/result Runs and separate read/handled truth', async () => {
  view(completionReadRequest()); const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved background result' });
  expect(within(saved).getByText('<script>saved result only</script> Exact UTF-8: thử nghiệm')).toBeVisible();
  expect(within(saved).getByText('Result Run')).toBeVisible(); expect(saved.querySelector('script')).toBeNull();
  expect(within(saved).getByText(/Taking or acknowledging the result does not mark it handled/)).toBeVisible();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
  const confirm = (await screen.findAllByText('Approve?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/raw read permissions and reviewer authority are checked again/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['missing', 'duplicate', 'source', 'metadata', 'hash'] as const)('blocks decisions but retains cancellation for %s completion evidence', async invalid => {
  const item = completionReadRequest(); const change = (field: string) => item.review.changes.find(c => c.field === field)!;
  if (invalid === 'missing') item.review.changes.pop();
  else if (invalid === 'duplicate') item.review.changes[1] = { ...item.review.changes[0] };
  else if (invalid === 'source') change('completionSourceRunId').after = '52345678-1234-1234-1234-123456789abc';
  else if (invalid === 'metadata') change('completionMetadata').after = '[{"ArtifactId":"not-an-exact-file"}]';
  else change('completionObservationHash').after = 'untrusted-checksum';
  view(item); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Saved completion read unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Approve' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('labels a completion-read receipt as read authorization while consumption stays separate', async () => {
  view({ ...completionReadRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'completion_read_authorized', recordedAt: request.review.serverNow } });
  const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Read authorized')).toBeVisible();
  expect(within(dialog).getByText(/Completion accounting remains separate/)).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
});

function accountReadRequest(): TenantApproval {
  return { ...request, kind: 'review', summary: 'Read exact connected account',
    preparedAction: { ...request.preparedAction, operation: 'read_connected_account' },
    review: { ...request.review, changes: [
      { field: 'accountName', before: null, after: 'Work <script> mailbox', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'accountPurpose', before: null, after: 'Email', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'accountReference', before: null, after: 'exact-account', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'accountReadTool', before: null, after: 'Search mailbox', beforeAvailable: false, afterAvailable: true, restoresDefault: false },
      { field: 'accountReadArguments', before: null, after: '{"accountRef":"exact-account","query":"from:work@example.test subject:<img src=x>","limit":1}', beforeAvailable: false, afterAvailable: true, restoresDefault: false }
    ] } };
}
it('reviews exact account parameters as literal text and confirms only the bounded read', async () => {
  view(accountReadRequest()); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Account type')).toBeVisible(); expect(within(dialog).getByText('Search mailbox')).toBeVisible();
  expect(within(dialog).getByText(/subject:<img src=x>/)).toBeVisible(); expect(dialog.querySelector('img')).toBeNull();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
  const confirmation = (await screen.findAllByText('Approve?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirmation).getByText(/Authorize only the saved bounded read/)).toBeVisible());
  expect(within(confirmation).getByText(/permits no send, write or later query/)).toBeVisible();
});
it('labels exact account consent as read authorization without claiming data delivery', async () => {
  view({ ...accountReadRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'account_read_authorized', recordedAt: request.review.serverNow } });
  const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Read authorized')).toBeVisible();
  expect(within(dialog).getByText(/Current access is checked before data reaches the agent/)).toBeVisible();
  expect(within(dialog).queryByText('Applied', { exact: true })).toBeNull();
});

const reportSourceReadRequest = (): TenantApproval => {
  const parent = '12345678-1234-1234-1234-123456789abc', child = '22345678-1234-1234-1234-123456789abc';
  const initial = '32345678-1234-1234-1234-123456789abc', source = '42345678-1234-1234-1234-123456789abc';
  const fields = { reportSourceParentSessionId: parent, reportSourceInitialRunId: initial, reportSourceRunId: source, reportSourceSessionId: child,
    reportSourceObjective: 'Report the exact finished result', reportSourceHash: 'b'.repeat(64),
    reportSourceObservation: JSON.stringify({ TriggerKind: 7, Text: JSON.stringify({ childSessionId: child, initialAgentRunId: source,
      objective: 'Report exact result', status: 'Completed', summary: '<script>literal report source</script>', attentionRequired: false, artifacts: [] }), EnvironmentKind: null }),
    readDisclosure: 'Read this saved result for its original conversation. No file bytes.', readLimit: 'Report creation requires separate authority.' };
  return { ...request, sessionId: child, destinationSessionId: parent, kind: 'review', summary: 'Read exact background report source',
    preparedAction: { ...request.preparedAction, operation: 'inspect_background_report_source' },
    review: { ...request.review, changes: Object.entries(fields).map(([field, after]) => ({ field, before: null, after, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } };
};
it('reviews exact report source with destination and separate creation consequences', async () => {
  view(reportSourceReadRequest()); const dialog = await screen.findByRole('dialog');
  const saved = await within(dialog).findByRole('region', { name: 'Exact saved report source' });
  expect(within(saved).getByText('<script>literal report source</script>')).toBeVisible(); expect(saved.querySelector('script')).toBeNull();
  expect(within(saved).getByText('Destination conversation', { exact: true })).toBeVisible(); expect(within(saved).getByText('Result Run', { exact: true })).toBeVisible();
  expect(within(dialog).getByRole('link', { name: 'Open destination conversation' })).toHaveAttribute('href', '/t/tenant-a/c/12345678-1234-1234-1234-123456789abc');
  fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
  const confirm = (await screen.findAllByRole('dialog')).find(el => el !== dialog)!;
  await waitFor(() => expect(within(confirm).getByText(/This creates no report Run/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' })); expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['source', 'destination', 'missing'] as const)('withholds report source read decisions for %s mismatch while retaining cancel', async invalid => {
  const item = reportSourceReadRequest();
  if (invalid === 'source') item.review.changes.find(c => c.field === 'reportSourceRunId')!.after = '52345678-1234-1234-1234-123456789abc';
  else if (invalid === 'destination') delete item.destinationSessionId;
  else item.review.changes.pop();
  view(item); const dialog = await screen.findByRole('dialog'); expect(await within(dialog).findByText('Saved completion read unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Approve' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('separates report source read receipt from report creation and delivery', async () => {
  view({ ...reportSourceReadRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'report_source_read_authorized', recordedAt: request.review.serverNow } });
  const result = await screen.findByRole('region', { name: 'Operation result' }); expect(within(result).getByText('Read authorized', { exact: true })).toBeVisible();
  expect(within(result).getByText(/Report creation and delivery remain separate/)).toBeVisible(); expect(within(result).queryByText('Report Run created')).not.toBeInTheDocument();
});


const accountActionRequest = (): TenantApproval => {
  const args = { accountRef: 'operations-mail', to: ['friend@example.test'], cc: [], bcc: ['private@example.test'], subject: '<script>Exact subject</script>', body: '<script>window.accountShouldNotRun = true</script>\nFinal exact line' };
  const fields: Record<string, string> = { accountName: 'Exact connected mailbox', accountPurpose: 'Email', accountReference: 'operations-mail', accountActionTool: 'email.create_draft', accountActionArguments: JSON.stringify(args),
    accountActionDisclosure: 'Save this exact immutable draft. Sending needs separate consent.', accountActionLimit: 'No role or later action authority is granted.',
    emailSender: 'sender@example.test', emailTo: 'friend@example.test', emailCc: '', emailBcc: 'private@example.test', emailSubject: args.subject, emailBody: args.body, emailDraftId: 'exact-draft' };
  return { ...request, kind: 'review', summary: 'Use exact connected account', preparedAction: { ...request.preparedAction, operation: 'use_connected_account' },
    review: { ...request.review, changes: Object.entries(fields).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } };
};
it.each(['review', 'exceptionalGrant'] as const)('reviews full inert account content with independent %s consequences', async kind => {
  const item = { ...accountActionRequest(), kind }; view(item);
  const dialog = await screen.findByRole('dialog'); const saved = await within(dialog).findByRole('region', { name: 'Exact saved account action' });
  expect(within(saved).getByRole('textbox', { name: 'Exact email body' })).toHaveValue('<script>window.accountShouldNotRun = true</script>\nFinal exact line');
  expect(within(saved).getByText('private@example.test')).toBeVisible(); expect(saved.querySelector('script')).toBeNull();
  fireEvent.click(within(dialog).getByRole('button', { name: kind === 'review' ? 'Approve' : 'Authorize once' }));
  const confirm = (await screen.findAllByText(kind === 'review' ? 'Approve?' : 'Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/Raw account use, source reads and reviewer authority are checked again/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['missing', 'duplicate', 'substituted'])('withholds decisions for %s account evidence while preserving cancel', async mode => {
  const item = accountActionRequest();
  if (mode === 'missing') item.review.changes = item.review.changes.filter(c => c.field !== 'emailBody');
  if (mode === 'duplicate') item.review.changes.push({ ...item.review.changes[0] });
  if (mode === 'substituted') item.review.changes.find(c => c.field === 'emailBody')!.after = 'Substituted body';
  view(item); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('Saved account action unavailable')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Approve' })).toBeDisabled(); expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled();
  expect(within(dialog).getByRole('button', { name: 'Cancel request' })).toBeEnabled();
});
it('shows the draft receipt separately and never infers a send or permission from it', async () => {
  view({ ...accountActionRequest(), status: 'approved', operationResult: { outcome: 'completed', reasonCode: 'account_action_completed', recordedAt: request.createdAt,
    accountAction: { status: 'draft_created', draftId: 'exact-draft', fileId: null, path: null, byteSize: null, sha256: null, items: null } } });
  const result = await screen.findByRole('region', { name: 'Operation result' });
  expect(within(result).getByText(/No message was sent. Sending requires separate consent/)).toBeVisible();
  expect(within(result).getByText('Recorded draft')).toBeVisible(); expect(within(result).getByText('exact-draft')).toBeVisible();
});
it('removes account content when refresh loses current read authority', async () => {
  view(accountActionRequest()); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByRole('textbox', { name: 'Exact email body' })).toBeVisible();
  fetchMock.mockImplementation(async path => String(path).includes('/approvals?') ? response({ items: [], total: 0, page: 0, pageSize: 20 }) : response({ reasonCode: 'approval_unavailable' }, 403));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Refresh details' }));
  await waitFor(() => expect(within(dialog).queryByRole('textbox', { name: 'Exact email body' })).not.toBeInTheDocument());
  expect(within(dialog).queryByText('private@example.test')).not.toBeInTheDocument();
});

const admissionRequest = (): TenantApproval => ({ ...request, summary: 'Admit exact Automation occurrence', runId: null, sessionId: null,
  preparedAction: { ...request.preparedAction, operation: 'admit_automation_occurrence' },
  review: { ...request.review, requesterName: null, changes: Object.entries({ admissionAutomation: 'Saved exact task', admissionSource: 'Scheduled occurrence · no human requester',
    admissionOccurrence: 'Invoked at the saved UTC time · registration revision 1', admissionPrimaryPermission: 'agent-runs.create', admissionObjective: '<script>Literal admission objective</script>', admissionSchedule: 'Once at the saved UTC time', admissionDestination: 'New separate Background Session · no completion report',
    admissionModel: 'Synthetic model', admissionScope: 'automations.run; sessions.create; agent-runs.create', admissionLimit: 'One exact occurrence. Primary grant only; later tools remain independent.' })
    .map(([field, after]) => ({ field, before: null, after, beforeAvailable: false, afterAvailable: true, restoresDefault: false })) } });
it('reviews source-less exact admission intent without inventing a conversation or executing work', async () => {
  view(admissionRequest()); const dialog = await screen.findByRole('dialog');
  expect(await within(dialog).findByText('<script>Literal admission objective</script>')).toBeVisible(); expect(dialog.querySelector('script')).toBeNull();
  expect(within(dialog).queryByRole('link', { name: 'Open original conversation' })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Authorize once' }));
  const confirm = (await screen.findAllByText('Authorize once?'))[0].closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/Current registration, configuration, requester, executor and reviewer authority/)).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep reviewing' }));
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(0);
});
it.each(['missing', 'duplicate', 'source', 'permission'] as const)('blocks a malformed admission preview: %s', async mode => {
  const item = admissionRequest(); if (mode === 'missing') item.review.changes.pop(); else if (mode === 'duplicate') item.review.changes[0] = item.review.changes[1]; else if (mode === 'permission') item.review.changes.find(change => change.field === 'admissionPrimaryPermission')!.after = 'system.admin'; else item.sessionId = 'invented-session';
  view(item); const dialog = await screen.findByRole('dialog'); await within(dialog).findByText('Saved exact task').catch(() => {});
  expect(within(dialog).getByRole('button', { name: 'Authorize once' })).toBeDisabled();
});
it.each(['admitted', 'notAdmitted', 'awaitingAdmission'] as const)('presents factual Automation settlement separately from approval: %s', async executionState => {
  const item = admissionRequest(); item.status = 'approved'; item.review = { ...item.review, canDecide: false, canCancel: false, executionState }; view(item);
  const dialog = await screen.findByRole('dialog'); const expected = executionState === 'admitted' ? /One Background Session and Run were admitted/ : executionState === 'notAdmitted' ? /This occurrence was not admitted/ : /Decision recorded. No admission result yet/;
  expect(await within(dialog).findByText(expected)).toBeVisible(); expect(within(dialog).queryByRole('link', { name: /conversation/ })).not.toBeInTheDocument();
});

it.each(['Built-in event · no human requester', 'Webhook event · no human requester'])('reviews exact captured event admission: %s', async source => {
  const item = admissionRequest(); const raw = source.startsWith('Built-in') ? 'core-events.read' : 'webhooks.read';
  item.kind = 'review';
  item.review.changes.find(c => c.field === 'admissionSource')!.after = source;
  item.review.changes.find(c => c.field === 'admissionPrimaryPermission')!.after = raw;
  item.review.changes.find(c => c.field === 'admissionScope')!.after += `; ${raw}`;
  item.review.changes.push(...Object.entries({ admissionEventSource: 'One exact saved source', admissionEventFilter: 'event.data.status === "received"', admissionEventDispatch: 'Every matched receipt', admissionEventData: '{"note":"<script>literal event data</script>"}' }).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })));
  view(item); const dialog = await screen.findByRole('dialog');
  expect(within(dialog).getByText('{"note":"<script>literal event data</script>"}')).toBeVisible(); expect(dialog.querySelector('script')).toBeNull();
  expect(within(dialog).getByRole('button', { name: /^Approve$/ })).toBeEnabled();
});

it.each(['credentials.read', 'credentials.use', 'email.read'])('reviews exact email source admission with primary %s', async primary => {
  const item = admissionRequest(); item.kind = 'review';
  item.review.changes.find(c => c.field === 'admissionSource')!.after = 'Built-in event · no human requester';
  item.review.changes.find(c => c.field === 'admissionPrimaryPermission')!.after = primary;
  item.review.changes.find(c => c.field === 'admissionScope')!.after += '; core-events.read; credentials.read; credentials.use; email.read';
  item.review.changes.push(...Object.entries({ admissionEventSource: 'One exact saved source', admissionEventFilter: 'true', admissionEventDispatch: 'Every matched receipt', admissionEventData: JSON.stringify({ accountId: 'aaaaaaaa-0000-0000-0000-000000000001', accountRef: 'saved-account', messageRef: '<literal reference>', provider: 'Gmail' }) }).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })));
  view(item); const dialog = await screen.findByRole('dialog'); expect(within(dialog).getByRole('button', { name: /^Approve$/ })).toBeEnabled();
});
it('blocks a widened email admission scope with malformed original metadata', async () => {
  const item = admissionRequest(); item.kind = 'review';
  item.review.changes.find(c => c.field === 'admissionSource')!.after = 'Built-in event · no human requester';
  item.review.changes.find(c => c.field === 'admissionPrimaryPermission')!.after = 'credentials.use';
  item.review.changes.find(c => c.field === 'admissionScope')!.after += '; core-events.read; credentials.read; credentials.use; email.read';
  item.review.changes.push(...Object.entries({ admissionEventSource: 'One exact saved source', admissionEventFilter: 'true', admissionEventDispatch: 'Every matched receipt', admissionEventData: '{"body":"not an email reference"}' }).map(([field, after]) => ({ field, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false })));
  view(item); const dialog = await screen.findByRole('dialog'); expect(within(dialog).getByRole('button', { name: /^Approve$/ })).toBeDisabled();
});
