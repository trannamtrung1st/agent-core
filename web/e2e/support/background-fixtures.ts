import type { Page } from '@playwright/test';
import type { AgentRun, BackgroundSession, CursorPage } from '../../src/services/api';
import { fixtureBackground, fixtureRun } from '../../src/features/chat/agentRunFixtures';

export function backgroundFixture(index: number, title: string, patch: Partial<AgentRun> = {}): BackgroundSession {
  const id = `90000000-0000-4000-8000-${String(index).padStart(12, '0')}`;
  const run = { ...fixtureRun, agentRunId: id, sessionId: id, ...patch };
  return { ...fixtureBackground, originalTitle: title, session: { ...fixtureBackground.session, sessionId: id, title },
    origin: { ...fixtureBackground.origin, initialAgentRunId: run.agentRunId }, latestRun: run };
}
export function cursorPage<T>(items: T[], start: number, limit: number, key: (row: T) => string): CursorPage<T> {
  const rows = items.slice(start, start + limit); const hasMore = start + rows.length < items.length;
  return { items: rows, hasMore, nextCursor: hasMore ? key(rows.at(-1)!) : null };
}
/** Owner-scoped public DTO fixtures; private evidence/checkpoints never reach the UI. */
export async function mockBackgroundSessions(page: Page, rows: BackgroundSession[]) {
  await page.route('**/background-sessions?**', route => {
    const url = new URL(route.request().url()); const cursor = url.searchParams.get('cursor');
    const start = cursor ? rows.findIndex(row => row.session.sessionId === cursor) + 1 : 0;
    return route.fulfill({ json: cursorPage(rows, start, Number(url.searchParams.get('limit') ?? 20), row => row.session.sessionId) });
  });
  await page.route('**/sessions/*/agent-runs**', route => {
    const url = new URL(route.request().url()); const id = url.pathname.split('/sessions/')[1].split('/')[0];
    const row = rows.find(row => row.session.sessionId === id);
    if (!row) return route.continue();
    if (route.request().method() === 'POST' && url.pathname.endsWith('/approve')) {
      row.latestRun = { ...row.latestRun!, revision: row.latestRun!.revision + 1, status: 'queued', approval: null };
      return route.fulfill({ json: row.latestRun });
    }
    return route.fulfill({ json: { items: row.latestRun ? [row.latestRun] : [], hasMore: false, nextCursor: null } });
  });
  await page.route('**/sessions/*/artifacts/page?**', route => {
    const id = new URL(route.request().url()).pathname.split('/sessions/')[1].split('/')[0];
    return rows.some(row => row.session.sessionId === id) ? route.fulfill({ json: { items: [], hasMore: false, nextCursor: null } }) : route.continue();
  });
}
export const approvalFixture: AgentRun['approval'] = {
  approvalId: '90000000-0000-4000-8000-999999999999', revision: 1, actionHash: 'a'.repeat(64),
  toolName: 'http.request', preview: 'POST https://example.com/items', expiresAt: '2099-10-08T09:00:00Z'
};
