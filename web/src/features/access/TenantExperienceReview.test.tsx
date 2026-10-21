import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantExperienceReviewButton } from './TenantExperienceReview';
const tenant: TenantContext = { tenant: { tenantId: 'tenant-a', name: 'Review organization', slug: 'review' }, userId: 'user', principalId: 'person', permissions: [], roles: [], revision: 8 };
const id = '00000000-0000-0000-0000-000000000001'; const second = '00000000-0000-0000-0000-000000000002';
const preview = { sourceKind: 'Session', sourceId: id, title: 'Literal <script>source</script>', throughCursor: 8, sourceRevision: 3, securityRevision: 17, instanceRevision: 4, sourceFingerprint: 'a'.repeat(64) };
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let posted: unknown[]; let result: 'new' | 'duplicate' | 'stale' | 'lost';
beforeEach(() => { posted = []; result = 'new'; resetIdentityCsrf(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); fetchMock.mockImplementation(async (path, options) => {
  if (String(path).includes('/csrf')) return response({ token: 'csrf' });
  if (options?.method === 'POST') { posted.push(JSON.parse(options.body as string)); if (result === 'stale') return response({ reasonCode: 'stale_revision' }, 409); if (result === 'lost') throw new Error('Connection lost'); return response({ created: result !== 'duplicate', sessionId: 'session', runId: 'run', securityRevision: 18 }); }
  return response(preview);
}); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
function View({ context = tenant }: { context?: TenantContext }) { return <App><TenantExperienceReviewButton context={context} id="agent" /></App>; }
async function inspect() {
  fireEvent.click(screen.getByRole('button', { name: 'Review completed work' })); const dialog = await screen.findByRole('dialog', { name: 'Review completed work' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Enter Session ID' })); fireEvent.change(within(dialog).getByRole('textbox', { name: 'Session ID' }), { target: { value: id } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Inspect selected source' })); await within(dialog).findByText(preview.title); return dialog;
}
async function queue(dialog: HTMLElement) {
  fireEvent.click(within(dialog).getByRole('button', { name: 'Queue review' })); const confirm = (await screen.findByRole('button', { name: 'Keep inspecting' })).closest('[role="dialog"]') as HTMLElement;
  await waitFor(() => expect(within(confirm).getByText(/grants no source-read consent or recording permission/)).toBeVisible()); fireEvent.click(within(confirm).getByRole('button', { name: 'Queue review' }));
}
it.each(['new', 'duplicate'] as const)('reviews escaped selected provenance and sends exact consent once for a %s receipt', async mode => {
  result = mode; render(<View />); const dialog = await inspect(); expect(dialog.querySelector('script')).toBeNull(); expect(posted).toEqual([]);
  await queue(dialog); await within(dialog).findByText(mode === 'new' ? 'Review queued' : 'Existing review found');
  expect(posted).toEqual([{ sourceKind: 'Session', sourceId: id, expectedRevision: 17, expectedInstanceRevision: 4, sourceFingerprint: 'a'.repeat(64) }]);
  expect(within(dialog).getByRole('link', { name: 'Inspect review work' })).toHaveAttribute('href', '/t/tenant-a/admin/agents/agent/activity/sessions?session=session');
  expect(within(dialog).queryByText(preview.title)).not.toBeInTheDocument(); expect(within(dialog).queryByRole('button', { name: 'Queue review' })).not.toBeInTheDocument();
});
it.each(['stale', 'lost'] as const)('retires the private preview after %s without replay and requires deliberate source inspection', async mode => {
  result = mode; render(<View />); const dialog = await inspect(); await queue(dialog); await within(dialog).findByText('A fresh source read is required');
  expect(within(dialog).queryByText(preview.title)).not.toBeInTheDocument(); expect(posted).toHaveLength(1);
  result = 'new'; fireEvent.click(within(dialog).getByRole('button', { name: 'Inspect source again' })); await within(dialog).findByText(preview.title); expect(posted).toHaveLength(1);
});
it('retires the old exact source when its visible selection changes', async () => {
  render(<View />); const dialog = await inspect(); fireEvent.change(within(dialog).getByRole('textbox', { name: 'Session ID' }), { target: { value: second } });
  expect(within(dialog).queryByText(preview.title)).not.toBeInTheDocument(); expect(within(dialog).queryByRole('button', { name: 'Queue review' })).not.toBeInTheDocument(); expect(posted).toEqual([]);
});
it('aborts a retired original-human scope and cannot show its late command result', async () => {
  const view = render(<View />); const dialog = await inspect(); let signal: AbortSignal | undefined; let settle!: (value: Response) => void;
  fetchMock.mockImplementation(async (path, options) => { if (String(path).includes('/csrf')) return response({ token: 'csrf' }); if (options?.method === 'POST') { signal = options.signal as AbortSignal; return new Promise<Response>(resolve => { settle = resolve; }); } return response(preview); });
  await queue(dialog); await waitFor(() => expect(signal).toBeDefined()); view.rerender(<View context={{ ...tenant, principalId: 'new-person' }} />);
  await waitFor(() => expect(signal?.aborted).toBe(true)); settle(response({ created: true, sessionId: 'old-session', runId: 'private-old-run', securityRevision: 18 }));
  await waitFor(() => expect(screen.queryByText('Review queued')).not.toBeInTheDocument()); expect(screen.queryByRole('link', { name: 'Inspect review work' })).not.toBeInTheDocument();
});
