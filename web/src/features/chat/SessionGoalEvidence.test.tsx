import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { createRef } from 'react';
import type { SessionGoal, GoalsView } from '../../services/tenantSessionGoals';
import { SessionGoalsDrawer } from './SessionGoalsDrawer';
const goal = (id: string, title: string): SessionGoal => ({ tenantId: 'tenant', sessionId: 'session', goalId: id, spaceId: 'space', revision: 3, planRevision: 1, status: 3,
  plan: { title, objective: `${title} objective`, successCriteria: 'Independent evidence', checkpoints: [], requireFinalAcceptance: false, autoContinuation: { enabled: false, maxRuns: 0, until: null } },
  pendingPlan: null, checkpoints: [], autoRunsUsed: 0, finalAcceptedBy: null });
const focused = goal('other', 'Different focused Goal'); const exact = goal('linked', 'Exact off-page Goal');
const view: GoalsView = { goals: { items: [focused], page: 0, pageSize: 20, total: 21 }, focusedGoalId: focused.goalId, focusRevision: 1, sessionRevision: 1, securityRevision: 4, canManage: false, canAccept: false, currentSpaceId: 'space' };
const fetchMock = vi.fn<typeof fetch>(); let denied = false;
beforeEach(() => { denied = false; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockImplementation(async path => {
  const url = String(path); if (url.endsWith('/goals/linked')) return new Response(JSON.stringify(denied ? { reasonCode: 'resource_unavailable' } : exact), { status: denied ? 403 : 200 });
  if (url.endsWith('/goals')) return new Response(JSON.stringify(view)); throw new Error(`Unexpected source ${url}`);
}); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
it('resolves an explicitly linked off-page Goal and clears its evidence on a denied reread without substituting another Goal', async () => {
  render(<ConfigProvider theme={{ token: { motion: false } }}><App><SessionGoalsDrawer tenant="tenant" session="session" instance="" source={null} initialGoalId="linked" open narrow={false} readonly refreshKey="1" openerRef={createRef<HTMLButtonElement>()} onClose={() => {}} onWorked={() => {}} /></App></ConfigProvider>);
  await screen.findByRole('heading', { name: exact.plan.title }); expect(screen.queryByRole('heading', { name: focused.plan.title })).not.toBeInTheDocument();
  expect(screen.getByRole('combobox', { name: 'Goals in this conversation' })).toBeDisabled(); expect(screen.getByText('Goal evidence is read-only here')).toBeInTheDocument();
  denied = true; fireEvent.click(screen.getByRole('button', { name: 'Refresh goals' }));
  await waitFor(() => expect(screen.queryByRole('heading', { name: exact.plan.title })).not.toBeInTheDocument());
  expect(screen.queryByRole('heading', { name: focused.plan.title })).not.toBeInTheDocument(); expect(fetchMock.mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
});
