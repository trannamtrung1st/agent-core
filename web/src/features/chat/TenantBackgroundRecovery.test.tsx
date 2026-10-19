import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { expect, it, vi } from 'vitest';
import { AgentRunDetails } from './AgentRunDetails';
import { fixtureRun } from './agentRunFixtures';
import { BackgroundWorkDrawer } from './BackgroundWorkDrawer';
import { fixtureBackground } from './agentRunFixtures';
import { legacyActivitySource, type ActivitySource } from '../../services/activitySource';
vi.mock('../../services/artifacts', async original => ({ ...await original<object>(), listArtifactPage: vi.fn(async () => ({ items: [], nextCursor: null, hasMore: false })) }));
it('locks continuation after a lost acknowledgement and opens saved visibility only after deliberate readback', async () => {
  const item = { ...fixtureBackground, securityRevision: 7, session: { ...fixtureBackground.session, agentInstanceId: 'agent-a' } };
  let saved = item; const next = { ...item, surfaces: ['BackgroundWork', 'ChatList'] };
  const source: ActivitySource = { ...legacyActivitySource, scope: 'tenant-a:person-a:agent-a', tenantId: 'tenant-a', readScope: 'tenant-a:person-a',
    listBackground: vi.fn(async () => ({ items: [saved], hasMore: false, nextCursor: null })), getBackground: vi.fn(async () => saved),
    continue: vi.fn(async () => { saved = next; throw new Error('The response was lost.'); }), listRuns: vi.fn(async () => ({ items: [], hasMore: false, nextCursor: null })) };
  const open = vi.fn();
  render(<App><BackgroundWorkDrawer instanceId="agent-a" source={source} open wide initialSessionId={item.session.sessionId} onClose={vi.fn()} onOpenConversation={open} /></App>);
  fireEvent.click(await screen.findByRole('button', { name: 'Continue in chat' }));
  expect(await screen.findByText('The response was lost.')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Continue in chat' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Read current background facts' }));
  const link = await screen.findByRole('link', { name: 'Open chat' }); fireEvent.click(link);
  await waitFor(() => expect(open).toHaveBeenCalledWith(item.session.sessionId)); expect(source.continue).toHaveBeenCalledOnce();
});
it('keeps missing Run authority and original-task files unavailable without falling back to all files', async () => {
  const item = { ...fixtureBackground, session: { ...fixtureBackground.session, agentInstanceId: 'agent-a' }, origin: { ...fixtureBackground.origin, initialAgentRunId: null }, initialRun: null, canContinueInChat: false };
  const source: ActivitySource = { ...legacyActivitySource, scope: 'tenant-b:person-b', tenantId: 'tenant-b', getBackground: vi.fn(async () => item), listRuns: vi.fn(async () => ({ items: [], hasMore: false, nextCursor: null })) };
  render(<App><BackgroundWorkDrawer instanceId="agent-a" source={source} open wide initialSessionId={item.session.sessionId} onClose={vi.fn()} /></App>);
  expect(await screen.findByText('The original run is unavailable.')).toBeVisible(); expect(screen.getByRole('button', { name: 'Continue in chat' })).toBeDisabled();
  expect(screen.getByText('Reading original task files requires access to the original Run.')).toBeVisible();
});

it('requires saved Run readback after a lost cancellation response without repeating the command', async () => {
  const run = { ...fixtureRun, status: 'running', cancellationAvailable: true, outcome: null };
  const actions = { cancel: vi.fn(async () => { throw new Error('Cancellation reply was lost.'); }), read: vi.fn(async () => ({ ...run, status: 'cancelled', cancellationAvailable: false })) };
  const change = vi.fn(); render(<App><AgentRunDetails run={run} actions={actions} onChange={change} /></App>);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel run' }));
  const dialog = await screen.findByRole('dialog', { name: 'Cancel this run?' });
  fireEvent.click((await import('@testing-library/react')).within(dialog).getByRole('button', { name: 'Cancel run' }));
  expect(await screen.findByText('Cancellation reply was lost.')).toBeVisible();
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Cancel this run?' })).not.toBeInTheDocument());
  expect(screen.getByRole('button', { name: 'Cancel run' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Reload run' }));
  await waitFor(() => expect(change).toHaveBeenCalledWith(expect.objectContaining({ status: 'cancelled' })));
  expect(actions.cancel).toHaveBeenCalledOnce(); expect(actions.read).toHaveBeenCalledOnce();
});
