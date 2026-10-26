import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { TenantGovernance, type GovernancePolicy } from './TenantGovernance';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';

const context: TenantContext = { tenant: { tenantId: 'tenant-one', name: 'Policy organization', slug: 'policy' }, userId: 'reader', principalId: 'reader', roles: [], permissions: ['tenant.governance.read', 'tenant.governance.manage'], revision: 1 };
const baseline: GovernancePolicy = { permission: 'agents.read', approvalClass: 'readOnly', minimum: { mode: 'none', independentReview: false }, effective: { mode: 'always', independentReview: true }, policyRevision: 2, securityRevision: 9, humanOnly: false, delegatable: true, canManage: true };
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>();
let current = baseline;
let retention: { savedChatRetentionDays: number | null; securityRevision: number; canManage: boolean };
let retentionFailure = false;
let put: (body: Record<string, unknown>) => Response;
beforeEach(() => {
  retention = { savedChatRetentionDays: null, securityRevision: 9, canManage: true }; retentionFailure = false;
  current = structuredClone(baseline); resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  put = body => { current = { ...current, effective: { mode: body.mode as 'none', independentReview: body.independentReview as boolean }, policyRevision: current.policyRevision + 1, securityRevision: current.securityRevision + 1 }; return response(current); };
  fetchMock.mockImplementation(async (url, options) => {
    const path = String(url);
    if (path.endsWith('/auth/csrf')) return response({ token: 'synthetic-csrf' });
    if (path.endsWith('/governance/chat-retention')) {
      if (options?.method === 'PUT') {
        if (retentionFailure) return response({ reasonCode: 'stale_revision' }, 409);
        const body = JSON.parse(String(options.body)); retention = { ...retention, savedChatRetentionDays: body.savedChatRetentionDays, securityRevision: retention.securityRevision + 1 };
      }
      return response(retention);
    }
    if (options?.method === 'PUT') return put(JSON.parse(String(options.body)));
    if (path.includes('/operation-policies?')) return response({ items: [current], total: 1, page: 0, pageSize: 20, securityRevision: current.securityRevision });
    return response(current);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const mount = (value = context) => render(<App><TenantGovernance context={value} /></App>);
// Ant Design's test-mode IDs collide; real browser naming is checked separately.
async function confirmation(title: string) { return (await screen.findAllByText(title))[0].closest('[role=dialog]') as HTMLElement; }
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT');
async function inspect() { await screen.findByRole('button', { name: 'Inspect Agents · Read' }); fireEvent.click(screen.getByRole('button', { name: 'Inspect Agents · Read' })); await waitFor(() => expect(fetchMock.mock.calls.map(([url]) => String(url))).toContain('/api/v2/tenants/tenant-one/operation-policies/agents.read')); await screen.findByText(/^Core minimum\s*:?$/); return document.querySelector<HTMLElement>('[role=dialog][aria-label="Review policy"]')!; }

it('shows Core/current policy and independent read-only authority without a save control', async () => {
  current = { ...baseline, canManage: false, humanOnly: true, delegatable: false }; mount({ ...context, permissions: ['tenant.governance.read'] });
  const dialog = await inspect(); await within(dialog).findByText('Read-only policy');
  expect(within(dialog).getByText(/^Core minimum\s*:?$/)).toBeVisible(); expect(within(dialog).getByText('None')).toBeVisible();
  expect(within(dialog).getByText('People only. Agents cannot perform this action, including through approval.')).toBeVisible();
  expect(within(dialog).queryByRole('button', { name: 'Save policy' })).toBeNull(); expect(writes()).toHaveLength(0);
});

it('confirms relaxation separately and saves only the exact current policy/security witnesses', async () => {
  mount(); const dialog = await inspect(); fireEvent.click(await within(dialog).findByRole('button', { name: 'Use Core minimum' }));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' }));
  const confirm = await confirmation('Relax organization review policy?'); expect(writes()).toHaveLength(0);
  await waitFor(() => expect(within(confirm).getByText('Agents · Read in Policy organization.')).toBeVisible());
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep editing' }));
  await waitFor(() => expect(screen.queryByText('Relax organization review policy?')).toBeNull());
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' }));
  fireEvent.click(within(await confirmation('Relax organization review policy?')).getByRole('button', { name: 'Save policy' }));
  await waitFor(() => expect(document.querySelector('[role=dialog][aria-label="Review policy"]')).toBeNull());
  expect(writes()).toHaveLength(1); expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ mode: 'none', independentReview: false, expectedRevision: 9, expectedPolicyRevision: 2 });
  expect(await screen.findByText('Review policy saved. The next protected action checks current policy.')).toBeVisible();
});

it('retains a stale draft, blocks replay and requires discard confirmation before reloading current policy', async () => {
  put = () => response({ reasonCode: 'stale_revision' }, 409); mount(); const dialog = await inspect();
  fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Require an independent reviewer' }));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' }));
  fireEvent.click(within(await confirmation('Relax organization review policy?')).getByRole('button', { name: 'Save policy' }));
  await within(dialog).findByRole('alert'); await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save policy' })).toBeDisabled());
  expect(within(dialog).getByRole('checkbox', { name: 'Require an independent reviewer' })).not.toBeChecked(); expect(writes()).toHaveLength(1);
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Reload saved policy' })).toBeEnabled());
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reload saved policy' }));
  const confirm = await confirmation('Discard changes and reload?');
  current = { ...baseline, securityRevision: 12, policyRevision: 3 };
  fireEvent.click(within(confirm).getByRole('button', { name: 'Discard and reload' }));
  await waitFor(() => expect(within(document.querySelector<HTMLElement>('[role=dialog][aria-label="Review policy"]')!).getByRole('checkbox', { name: 'Require an independent reviewer' })).toBeChecked());
  expect(writes()).toHaveLength(1);
});

it('treats a successful but malformed save acknowledgement as saved/unknown and cannot replay it', async () => {
  current = { ...baseline, effective: baseline.minimum }; put = () => response({ saved: true }); mount(); const dialog = await inspect();
  fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'Require an independent reviewer' }));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' }));
  expect(await within(dialog).findByText('The change was saved, but this view could not finish updating. Reload the page before making another change.')).toBeVisible();
  expect(within(dialog).getByRole('button', { name: 'Save policy' })).toBeDisabled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save policy' })); expect(writes()).toHaveLength(1);
  expect(screen.queryByText('Review policy saved. The next protected action checks current policy.')).toBeNull();
});

it('disables choices below the Core minimum and preserves unsaved choices when close is cancelled', async () => {
  current = { ...baseline, minimum: { mode: 'always', independentReview: false }, effective: { mode: 'always', independentReview: false } }; mount(); const dialog = await inspect();
  const mode = await within(dialog).findByRole('combobox', { name: 'Review mode' }); fireEvent.mouseDown(mode);
  expect(screen.getByText('None', { selector: '.ant-select-item-option-content' }).closest('.ant-select-item')).toHaveClass('ant-select-item-option-disabled');
  fireEvent.click(screen.getByText('Always review', { selector: '.ant-select-item-option-content' }));
  fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Require an independent reviewer' }));
  fireEvent.click(within(dialog).getByRole('button', { name: /^Cancel$/ }));
  const confirm = await confirmation('Discard unsaved changes?');
  fireEvent.click(within(confirm).getByRole('button', { name: 'Keep editing' }));
  await waitFor(() => expect(screen.queryByText('Discard unsaved changes?')).toBeNull());
  expect(within(dialog).getByRole('checkbox', { name: 'Require an independent reviewer' })).toBeChecked(); expect(writes()).toHaveLength(0);
});

it('clears protected detail after reload loses read authority', async () => {
  mount(); const dialog = await inspect(); await within(dialog).findByText(/^Current review\s*:?$/);
  fetchMock.mockImplementation(async () => response({ reasonCode: 'permission_denied' }, 403));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reload saved policy' }));
  await screen.findByText('You do not have permission for this action. Contact an organization owner if you need access.'); const currentDialog = document.querySelector<HTMLElement>('[role=dialog][aria-label="Review policy"]')!;
  expect(within(currentDialog).queryByText(/^Current review\s*:?$/)).toBeNull(); expect(within(currentDialog).queryByText('agents.read')).toBeNull();
});


it('saves a bounded retention default with exact security evidence and refreshes the pinned-policy explanation', async () => {
  mount(); const forever = await screen.findByRole('checkbox', { name: 'Forever' });
  expect(forever).toBeChecked(); fireEvent.click(forever);
  const days = screen.getByRole('spinbutton', { name: 'Saved Chat retention days' });
  fireEvent.change(days, { target: { value: '7' } }); fireEvent.blur(days);
  fireEvent.click(screen.getByRole('button', { name: 'Save retention' }));
  await screen.findByText('Chat retention saved for future chats.');
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ savedChatRetentionDays: 7, expectedRevision: 9 });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save retention' })).toBeDisabled());
  expect(screen.getByText(/Existing chats keep their policy/)).toBeVisible();
});

it('blocks stale retention replay until refresh and disables governance changes for a reader', async () => {
  retentionFailure = true; mount(); fireEvent.click(await screen.findByRole('checkbox', { name: 'Forever' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save retention' }));
  await screen.findByText('Refresh retention to review the current policy before saving again.');
  expect(screen.getByRole('button', { name: 'Save retention' })).toBeDisabled(); expect(writes()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Refresh retention' }));
  await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Forever' })).toBeChecked());
  cleanup(); retention.canManage = false; mount({ ...context, permissions: ['tenant.governance.read'] });
  await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Forever' })).toBeDisabled());
  expect(screen.getByRole('button', { name: 'Save retention' })).toBeDisabled();
});
