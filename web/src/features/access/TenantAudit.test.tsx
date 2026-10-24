import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Grid } from 'antd';
import { SystemAudit, TenantAudit } from './TenantAudit';
import type { TenantContext } from '../../services/identity';

const context: TenantContext = { tenant: { tenantId: 'tenant-one', name: 'Audit organization', slug: 'audit' },
  userId: 'user-one', principalId: 'human-one', permissions: ['security.audit.read'], roles: [], revision: 8 };
const execution = { context: { agentPrincipalId: 'agent-principal', originalInitiatorPrincipalId: 'requester-principal',
  sessionId: 'session-reference', runId: 'run-reference', activationId: 'activation-reference', rootActivationId: 'root-reference', origin: 'UserMessage' },
  runRevision: 3, securityRevision: 8, permission: 'agents.update', resource: { resourceType: 'agent', resourceId: 'agent-instance' },
  purpose: 'ApprovalPreparation', approvalKind: 'ExceptionalGrant' };
const review = { auditId: 'review', actorPrincipalId: 'agent-principal', action: 'authorization.operation', target: 'agent-instance',
  at: '2026-10-17T00:00:00Z', decision: 'requiresApproval', reasonCode: 'agent_missing_delegatable_permission', execution };
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body && typeof body === 'object' && 'items' in body ? { total: (body as { items: unknown[] }).items.length, page: 0, pageSize: 30, ...body } : body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>();
beforeEach(() => { fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('distinguishes required review from a denied effect and expands one lineage record at a time', async () => {
  const effect = { ...review, auditId: 'effect', action: 'operations.preferences.apply', decision: 'denied', reasonCode: 'operation_witness_changed',
    execution: { ...execution, purpose: 'EffectResult', approvalRequestId: 'approval-reference', operationId: 'operation-reference', grantId: 'grant-reference' } };
  fetchMock.mockResolvedValueOnce(response({ items: [review, effect] }));
  render(<TenantAudit context={context} />);
  // The cold Ant Design table/breakpoint render can exceed RTL's 1s default on hosted CPU.
  expect(await screen.findByText('Approval required', { exact: true }, { timeout: 5_000 })).toBeVisible();
  expect(screen.getByText('Not applied', { exact: true })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'View details: Agent authorization checked' }));
  const details = screen.getByRole('region', { name: 'Audit entry details' });
  expect(within(details).getByText('run-reference')).toBeVisible(); expect(within(details).getByText('root-reference')).toBeVisible();
  expect(within(details).getByText('requester-principal')).toBeVisible();
  expect(within(details).getByText(/Later work is checked again/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'View details: Conversation preference change' }));
  expect(screen.getAllByRole('region', { name: 'Audit entry details' })).toHaveLength(1);
  expect(screen.getByText('operation-reference')).toBeVisible(); expect(screen.getByText('approval-reference')).toBeVisible();
  expect(screen.getByText('grant-reference')).toBeVisible(); expect(screen.getByText(/Later work is checked again/)).not.toBeVisible();
});

it('keeps legacy records readable and never labels an unknown decision Allowed', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [
    { ...review, auditId: 'legacy', action: 'iam.roles.assign', decision: 'allowed', reasonCode: 'assignment_created', execution: null },
    { ...review, auditId: 'unknown', decision: 'future-result' }
  ] }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByRole('button', { name: 'View details: Role assigned' })).toBeVisible();
  expect(await screen.findAllByText('Recorded', { exact: true })).toHaveLength(2); expect(screen.queryByText('Allowed', { exact: true })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'View details: Role assigned' }));
  expect(screen.getByText('assignment_created')).toBeVisible(); expect(screen.queryByText('Agent run')).not.toBeInTheDocument();
});

it('recovers a failed read to an explicit empty state through the shared reload action', async () => {
  fetchMock.mockResolvedValueOnce(response({ reasonCode: 'service_unavailable' }, 503)).mockResolvedValueOnce(response({ items: [] }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByRole('alert')).toHaveTextContent('temporarily unavailable');
  fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
  expect(await screen.findByText('No audit entries yet.')).toBeVisible(); expect(fetchMock).toHaveBeenCalledTimes(2);
});

it('clears a previous organization and its expanded details while another organization loads', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [review] })).mockResolvedValueOnce(response({ items: [] }));
  const view = render(<TenantAudit context={context} />);
  fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  expect(screen.getByText('run-reference')).toBeVisible();
  view.rerender(<TenantAudit context={{ ...context, tenant: { ...context.tenant, tenantId: 'tenant-two' } }} />);
  expect(screen.queryByText('run-reference')).not.toBeInTheDocument();
  expect(await screen.findByText('No audit entries yet.')).toBeVisible();
  expect(fetchMock.mock.calls[1][0]).toBe('/api/v2/tenants/tenant-two/audit?page=0&pageSize=30');
});


it('labels only a committed successful effect Applied and shows its exact operation reference', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [{ ...review, action: 'operations.preferences.apply', decision: 'allowed', reasonCode: 'operation_completed',
    execution: { ...execution, purpose: 'EffectResult', operationId: 'completed-operation' } }] }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByText('Applied', { exact: true })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'View details: Conversation preference change' }));
  expect(screen.getByText('completed-operation')).toBeVisible();
});

it('shows uncertain credential outcomes independently of permission denial and preserves the exact effect reference', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [{ ...review, action: 'operations.credentials.check', decision: 'indeterminate', reasonCode: 'credential_check_uncertain',
    execution: { ...execution, purpose: 'EffectResult', permission: 'credentials.use', operationId: 'uncertain-operation' } }] }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByText('Outcome uncertain', { exact: true })).toBeVisible();
  expect(screen.queryByText('Not applied', { exact: true })).not.toBeInTheDocument(); expect(screen.queryByText('Denied', { exact: true })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'View details: Credential connection check' }));
  expect(screen.getByText('uncertain-operation')).toBeVisible(); expect(screen.getByText('Recorded effect result')).toBeVisible();
});

it('labels a confirmed connection check without claiming a configuration change', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [{ ...review, action: 'operations.credentials.check', decision: 'allowed', reasonCode: 'credential_reconnect_required',
    execution: { ...execution, purpose: 'EffectResult' } }] }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByText('Check completed', { exact: true })).toBeVisible(); expect(screen.queryByText('Applied', { exact: true })).not.toBeInTheDocument();
});


it('refreshes through an exactly named action and closes the previous disclosure', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [review] })).mockResolvedValueOnce(response({ items: [review] }));
  render(<TenantAudit context={context} />);
  fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  expect(screen.getByRole('region', { name: 'Audit entry details' })).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
  expect(await screen.findByRole('button', { name: 'View details: Agent authorization checked' })).toHaveAttribute('aria-expanded', 'false');
  expect(screen.queryByRole('region', { name: 'Audit entry details' })).not.toBeInTheDocument();
  expect(fetchMock).toHaveBeenCalledTimes(2);
});


it('opens mobile lineage in the shared bounded drawer and closes it by the visible action', async () => {
  const matchMedia = window.matchMedia.bind(window);
  vi.spyOn(window, 'matchMedia').mockImplementation(query => ({ ...matchMedia(query),
    matches: (!/min-width:\s*(\d+)/.test(query) || 320 >= Number(/min-width:\s*(\d+)/.exec(query)![1]))
      && (!/max-width:\s*(\d+)/.test(query) || 320 <= Number(/max-width:\s*(\d+)/.exec(query)![1])) }));
  fetchMock.mockResolvedValueOnce(response({ items: [review] }));
  render(<TenantAudit context={context} />);
  fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  // AntD test-mode useId repeats test-id for Select and Drawer; the real browser uses unique IDs.
  const drawer = await screen.findByRole('dialog');
  expect(within(drawer).getByText('Audit details')).toBeVisible();
  expect(within(drawer).getByText('Approval required', { exact: true })).toBeVisible();
  expect(within(drawer).getByText('The agent needs authorization for this exact operation.')).toBeVisible();
  expect(within(drawer).getByText('root-reference')).toBeVisible();
  fireEvent.click(within(drawer).getByRole('button', { name: 'Close' }));
  expect(await screen.findByRole('button', { name: 'View details: Agent authorization checked' })).toHaveAttribute('aria-expanded', 'false');
});

it('keeps one detail ID when a selected desktop row moves into the mobile drawer', async () => {
  const breakpoint = vi.spyOn(Grid, 'useBreakpoint').mockReturnValue({ md: true, sm: true });
  fetchMock.mockResolvedValueOnce(response({ items: [review] }));
  const view = render(<TenantAudit context={context} />);
  fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  expect(document.querySelectorAll('[id="audit-review"]')).toHaveLength(1);
  breakpoint.mockReturnValue({ md: false, sm: false });
  view.rerender(<TenantAudit context={context} />);
  expect(await screen.findByRole('dialog')).toBeVisible();
  await waitFor(() => expect(document.querySelectorAll('[id="audit-review"]')).toHaveLength(1));
});


it('uses server counts and pages, then applies exact filters without retaining an expanded prior entry', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [review], total: 31 }))
    .mockResolvedValueOnce(response({ items: [{ ...review, auditId: 'second' }], total: 31, page: 1 }))
    .mockResolvedValueOnce(response({ items: [], total: 0 }));
  render(<TenantAudit context={context} />);
  fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  expect(screen.getByText('31 entries')).toBeVisible();
  fireEvent.click(screen.getByTitle('2'));
  await waitFor(() => expect(fetchMock.mock.calls[1][0]).toBe('/api/v2/tenants/tenant-one/audit?page=1&pageSize=30'));
  expect(await screen.findByRole('button', { name: 'View details: Agent authorization checked' })).toHaveAttribute('aria-expanded', 'false');
  fireEvent.change(screen.getByLabelText('Activity code'), { target: { value: 'approvals.request' } });
  expect(fetchMock).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole('button', { name: 'Apply filters' }));
  await waitFor(() => expect(fetchMock.mock.calls[2][0]).toBe('/api/v2/tenants/tenant-one/audit?page=0&pageSize=30&action=approvals.request'));
  expect(await screen.findByText('0 entries')).toBeVisible();
});

it('blocks invalid exact principal and reversed time filters without requesting a page', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [] })); render(<TenantAudit context={context} />);
  fireEvent.change(await screen.findByLabelText('Actor principal'), { target: { value: 'arbitrary-person' } });
  fireEvent.click(screen.getByRole('button', { name: 'Apply filters' }));
  expect(await screen.findByText('Enter a complete principal ID.')).toBeVisible(); expect(fetchMock).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
  fireEvent.change(screen.getByLabelText('From (local time)'), { target: { value: '2026-10-22T12:00:00' } });
  fireEvent.change(screen.getByLabelText('Through (local time)'), { target: { value: '2026-10-21T12:00:00' } });
  fireEvent.click(screen.getByRole('button', { name: 'Apply filters' }));
  expect(await screen.findByText('Choose a time on or after From.')).toBeVisible(); expect(fetchMock).toHaveBeenCalledTimes(1);
});

it('uses only the System query for platform records and explains its independent scope', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [{ ...review, action: 'identity.bootstrap', execution: null, target: 'global-account', decision: 'allowed', reasonCode: 'explicit_cli_bootstrap' }] }));
  render(<SystemAudit />);
  expect(await screen.findByRole('button', { name: 'View details: Platform initialized' })).toBeVisible();
  expect(screen.getByText(/Organization activity requires access/)).toBeVisible();
  expect(fetchMock.mock.calls[0][0]).toBe('/api/v2/system/audit?page=0&pageSize=30');
});

it('retires visible rows and details after a denied refresh', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [review] })).mockResolvedValueOnce(response({ reasonCode: 'permission_denied' }, 403));
  render(<TenantAudit context={context} />); fireEvent.click(await screen.findByRole('button', { name: 'View details: Agent authorization checked' }));
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' })); expect(await screen.findByRole('alert')).toBeVisible();
  expect(screen.queryByText('run-reference')).not.toBeInTheDocument(); expect(screen.queryByText('1 entry')).not.toBeInTheDocument();
});

it('rejects malformed successful page metadata without rendering captured records', async () => {
  fetchMock.mockResolvedValueOnce(response({ items: [review], total: -1 })); render(<TenantAudit context={context} />);
  expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('run-reference')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'View details: Agent authorization checked' })).not.toBeInTheDocument();
});

it.each([null, [null], [{ ...review, target: { privateBody: 'must not render' } }]])('retires malformed successful audit rows without crashing (%j)', async items => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ items, total: 1, page: 0, pageSize: 30 }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
  render(<TenantAudit context={context} />);
  expect(await screen.findByRole('alert')).toBeVisible();
  expect(screen.queryByRole('button', { name: /details:/ })).not.toBeInTheDocument();
});
