import { App, ConfigProvider } from 'antd';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { InstanceContinuitySection } from './InstanceContinuitySection';
import { instanceContinuityRequest } from '../../services/adminApi';
import { listModels } from '../../services/api';

vi.mock('../../services/adminApi', () => ({ instanceContinuityRequest: vi.fn() }));
vi.mock('../../services/api', () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
let enabled = false;
const experience = () => ({ enabled, settingsRevision: 3, contextBudgetCharacters: 6000, items: [] });
const thoughts = { minIntervalSeconds: 3600, items: [] };
function view(id = 'owner-instance') {
  return <ConfigProvider><App><InstanceContinuitySection instanceId={id} /></App></ConfigProvider>;
}
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => {
  vi.clearAllMocks(); enabled = false;
  vi.mocked(listModels).mockResolvedValue({ defaultKey: 'synthetic-default', models: [] });
  request.mockImplementation(async (_id, path) => path === 'thoughts' ? thoughts : experience());
});

describe('Instance continuity owner controls', () => {
  it('distinguishes captured checkpoint time from source creation and marks legacy times unknown', async () => {
    const row = { experienceId: 'checkpoint', sourceKind: 'Session', sourceId: 'source', throughCursor: 4,
      sourceAt: '2026-01-01T00:00:00Z', sourceCreatedAt: '2026-01-01T00:00:00Z', checkpointAt: '2026-02-01T00:00:00Z',
      definitionId: 'general-assistant', definitionVersion: 9, modelKey: 'synthetic-default', generationWorkItemId: 'work',
      visibility: 'Eligible', revision: 1, eligibleForContext: true, status: 'Completed', content: { goal: 'Observed correction',
        attempts: [], decisions: [], outcomes: [], corrections: [], unresolved: [], difficulties: [], lessons: [] } };
    request.mockImplementation(async (_id, path) => path === 'thoughts' ? thoughts : { ...experience(), items: [row, { ...row, experienceId: 'legacy', checkpointAt: null, content: { ...row.content, goal: 'Legacy observation' } }] });
    render(view());
    fireEvent.click(await screen.findByText('Observed correction'));
    fireEvent.click(await screen.findByText('Legacy observation'));
    expect(screen.getByText(/Not recorded \(legacy checkpoint\)/)).toBeVisible();
    expect(screen.getAllByText(/Checkpoint captured:/).length).toBeGreaterThan(1);
    expect(screen.getAllByText(/Source created:/).length).toBeGreaterThan(1);
  });


  it('keeps a newer experience configuration when an earlier refresh completes late', async () => {
    let finishRead!: (value: ReturnType<typeof experience>) => void;
    let reads = 0;
    request.mockImplementation(async (_id, path) => {
      if (path === 'thoughts') return thoughts;
      if (path === 'experience/configuration') { enabled = true; return experience(); }
      if (++reads === 2) return new Promise(resolve => { finishRead = resolve; });
      return experience();
    });
    render(view());
    await screen.findByText('No experience yet. Enable experience and retrospect a completed task.');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh experience' }));
    fireEvent.click(screen.getByRole('switch', { name: 'Enable experience' }));
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Enable experience' })).toBeChecked());
    await act(async () => { finishRead({ ...experience(), enabled: false }); });
    expect(screen.getByRole('switch', { name: 'Enable experience' })).toBeChecked();
  });

  it('does not let an older initiative poll erase a newly created registration', async () => {
    let poll!: () => void;
    vi.spyOn(window, 'setInterval').mockImplementation((handler, interval) => {
      if (interval === 5000) poll = handler as () => void;
      return 123 as unknown as ReturnType<typeof window.setInterval>;
    });
    let finishPoll!: (value: typeof thoughts) => void;
    let reads = 0;
    const created = { registrationId: 'new-thought', revision: 1, enabled: true, intervalSeconds: 3600,
      thinkingPrompt: 'Review experience', modelKey: null, reasoningEffort: null, nextRunAt: null,
      lastRunAt: null, lastStatus: null, lastOutcome: null, lastModelKey: null };
    request.mockImplementation(async (_id, path, method) => {
      if (path !== 'thoughts') return experience();
      if (method === 'POST') return created;
      if (++reads === 2) return new Promise(resolve => { finishPoll = resolve; });
      return reads > 2 ? { ...thoughts, items: [created] } : thoughts;
    });
    render(view());
    await screen.findByText(/No thought activations configured/);
    act(() => poll());
    fireEvent.change(screen.getByLabelText('Thinking prompt'), { target: { value: 'Review experience' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create thought' }));
    await screen.findByText('Review experience');
    await act(async () => { finishPoll(thoughts); });
    expect(screen.getByText('Review experience')).toBeVisible();
    expect(screen.queryByText(/No thought activations configured/)).not.toBeInTheDocument();
  });

  it('keeps disabled and empty states clear and prevents checkpoint requests until enabled', async () => {
    render(view());
    expect(await screen.findByText('No experience yet. Enable experience and retrospect a completed task.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Retrospect now' })).toBeDisabled();
    expect(screen.getByLabelText('Retrospection source Session')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Create thought' })).toBeDisabled();
    expect(screen.getByText(/No thought activations configured/)).toBeVisible();
    expect(request.mock.calls.every(call => call[0] === 'owner-instance')).toBe(true);
  });

  it('reports a revision conflict and reloads without automatically replaying the mutation', async () => {
    request.mockImplementation(async (_id, path, method) => {
      if (path === 'experience/configuration' && method === 'PUT') throw new Error('Experience settings revision is stale.');
      return path === 'thoughts' ? thoughts : experience();
    });
    render(view());
    fireEvent.click(await screen.findByRole('switch', { name: 'Enable experience' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/stale/);
    expect(screen.getByRole('switch', { name: 'Enable experience' })).not.toBeChecked();
    fireEvent.click(within(screen.getByRole('alert')).getByRole('button', { name: 'Reload' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(request.mock.calls.filter(call => call[1] === 'experience/configuration')).toHaveLength(1);
  });

  it('uses revisioned owner configuration and bounded defaults for a new thought', async () => {
    render(view());
    const prompt = await screen.findByLabelText('Thinking prompt');
    fireEvent.change(prompt, { target: { value: 'Review experience. Do nothing when no useful action exists.' } });
    fireEvent.click(screen.getByRole('switch', { name: 'Enable thought activation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create thought' }));
    await waitFor(() => expect(request).toHaveBeenCalledWith('owner-instance', 'thoughts', 'POST', {
      expectedRevision: 0, enabled: true, intervalSeconds: 3600,
      thinkingPrompt: 'Review experience. Do nothing when no useful action exists.', modelKey: null, reasoningEffort: null
    }));
    await waitFor(() => expect(prompt).toHaveValue(''));
  });
});
