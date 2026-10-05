import { App, ConfigProvider } from 'antd';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { InstanceContinuitySection } from './InstanceContinuitySection';
import { instanceContinuityRequest, type ExperienceItem } from '../../services/adminApi';
import { listModels } from '../../services/api';

vi.mock('./InstanceSchedulesSection', () => ({ InstanceSchedulesSection: () => null }));
vi.mock('../../services/adminApi', () => ({ instanceContinuityRequest: vi.fn() }));
vi.mock('../../services/api', () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
let enabled = false;
const experience = () => ({ enabled, settingsRevision: 3, contextBudgetCharacters: 6000, items: [] });
const thoughts = { minIntervalSeconds: 15, items: [] };
function view(id = 'owner-instance') {
  return <ConfigProvider><App><InstanceContinuitySection instanceId={id} /></App></ConfigProvider>;
}
const record = (index: number): ExperienceItem => ({
  experienceId: `experience-${index}`, sourceKind: 'Session', sourceId: `source-${index}`, throughCursor: index,
  sourceAt: '2026-01-01T00:00:00Z', sourceCreatedAt: '2026-01-01T00:00:00Z',
  checkpointAt: new Date(Date.UTC(2026, 1, index)).toISOString(), definitionId: 'secretary', definitionVersion: 1,
  modelKey: 'synthetic-default', generationWorkItemId: `work-${index}`, visibility: 'Eligible', revision: 1,
  status: 'Completed', eligibleForContext: true, diagnosticId: null, failureSummary: null,
  content: { goal: `Atlas review ${index}`, attempts: [], decisions: [], outcomes: [], corrections: [],
    unresolved: [], difficulties: [], lessons: [`Lesson ${index}`, 'Check unresolved decisions before confirming the agenda.'] }
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => {
  vi.clearAllMocks(); enabled = false;
  vi.mocked(listModels).mockResolvedValue({ defaultKey: 'synthetic-default', models: [] });
  request.mockImplementation(async (_id, path) => path === 'thoughts' ? thoughts : experience());
});

describe('Instance continuity owner controls', () => {
  it('paginates records and searches observation text across pages', async () => {
    const items = Array.from({ length: 21 }, (_, index) => record(index + 1));
    items[0].content!.lessons[0] = 'Unique Atlas preparation rule';
    request.mockImplementation(async (_id, path) => {
      if (path === 'thoughts') return thoughts;
      return { ...experience(), items };
    });
    render(view());
    await screen.findByRole('button', { name: 'View experience: Atlas review 21' });
    expect(screen.getAllByRole('button', { name: /^View experience:/ })).toHaveLength(10);
    fireEvent.click(screen.getByTitle('2'));
    await screen.findByRole('button', { name: 'View experience: Atlas review 11' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Search experience' }), { target: { value: 'Unique Atlas preparation rule' } });
    expect(screen.getAllByRole('button', { name: /^View experience:/ })).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'View experience: Atlas review 1' })).toBeVisible();
  });

  it('shows separate observations and preserves revisioned detail actions', async () => {
    let item = record(1);
    item.content!.lessons[0] = 'Unique Atlas preparation rule';
    request.mockImplementation(async (_id, path, method) => {
      if (path === 'thoughts') return thoughts;
      if (method === 'PUT') item = { ...item, visibility: 'Suppressed', eligibleForContext: false, revision: 2 };
      return { ...experience(), items: [item] };
    });
    render(view());
    await screen.findByRole('button', { name: 'View experience: Atlas review 1' });
    fireEvent.click(screen.getByRole('button', { name: 'View experience: Atlas review 1' }));
    const details = screen.getByRole('region', { name: 'Experience details' });
    expect(within(details).getAllByRole('listitem').map(item => item.textContent)).toEqual([
      'Unique Atlas preparation rule', 'Check unresolved decisions before confirming the agenda.'
    ]);
    expect(within(details).getByText('Source Session').closest('tr')).toHaveTextContent('source-1');
    fireEvent.click(screen.getByRole('button', { name: 'Suppress experience' }));
    await screen.findByRole('button', { name: 'Include in context' });
    expect(request).toHaveBeenCalledWith('owner-instance', 'experience/experience-1', 'PUT', { expectedRevision: 1, visibility: 'Suppressed' });
  });

  it('admits one rapid Experience mutation and releases controls for a later owner action', async () => {
    let finish!: (value: ReturnType<typeof experience>) => void;
    request.mockImplementation(async (_id, path) => {
      if (path === 'experience/configuration') return new Promise(resolve => { finish = resolve; });
      return path === 'thoughts' ? thoughts : experience();
    });
    render(view());
    const toggle = await screen.findByRole('switch', { name: 'Enable experience' });
    act(() => { toggle.click(); toggle.click(); });
    expect(request.mock.calls.filter(call => call[1] === 'experience/configuration')).toHaveLength(1);
    expect(toggle).toBeDisabled();
    enabled = true;
    await act(async () => finish(experience()));
    expect(toggle).toBeChecked();
    expect(toggle).toBeEnabled();
    fireEvent.click(toggle);
    expect(request.mock.calls.filter(call => call[1] === 'experience/configuration')).toHaveLength(2);
    enabled = false;
    await act(async () => finish(experience()));
    expect(toggle).not.toBeChecked();
    expect(toggle).toBeEnabled();
  });

  it('distinguishes captured checkpoint time from source creation and marks legacy times unknown', async () => {
    const row = { experienceId: 'checkpoint', sourceKind: 'Session', sourceId: 'source', throughCursor: 4,
      sourceAt: '2026-01-01T00:00:00Z', sourceCreatedAt: '2026-01-01T00:00:00Z', checkpointAt: '2026-02-01T00:00:00Z',
      definitionId: 'general-assistant', definitionVersion: 9, modelKey: 'synthetic-default', generationWorkItemId: 'work',
      visibility: 'Eligible', revision: 1, eligibleForContext: true, status: 'Completed', content: { goal: 'Observed correction',
        attempts: [], decisions: [], outcomes: [], corrections: [], unresolved: [], difficulties: [], lessons: [] } };
    request.mockImplementation(async (_id, path) => path === 'thoughts' ? thoughts : { ...experience(), items: [row, { ...row, experienceId: 'legacy', checkpointAt: null, content: { ...row.content, goal: 'Legacy observation' } }] });
    render(view());
    fireEvent.click(await screen.findByRole('button', { name: 'View experience: Observed correction' }));
    const current = screen.getByRole('region', { name: 'Experience details' });
    expect(within(current).getByText('Checkpoint captured').closest('tr')).toHaveTextContent(new Date(row.checkpointAt).toLocaleString());
    expect(within(current).getByText('Source created').closest('tr')).toHaveTextContent(new Date(row.sourceCreatedAt).toLocaleString());
    fireEvent.click(screen.getByRole('button', { name: 'View experience: Legacy observation' }));
    expect(screen.getByText(/Not recorded \(legacy checkpoint\)/)).toBeVisible();
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

  it('acknowledges a run immediately, admits one rapid-click request, and releases controls after failure', async () => {
    const item = { registrationId: 'run-thought', revision: 1, enabled: true, intervalSeconds: 3600,
      thinkingPrompt: 'Review current work', modelKey: null, reasoningEffort: null, nextRunAt: null,
      lastRunAt: null, lastOutcome: null, lastWorkItemId: null, executionStatus: null, effectiveModelKey: null, status: 'Active' };
    let rejectRun!: (reason: Error) => void;
    request.mockImplementation(async (_id, path) => {
      if (path === 'thoughts/run-thought/run') return new Promise((_resolve, reject) => { rejectRun = reject; });
      return path === 'thoughts' ? { ...thoughts, items: [item] } : experience();
    });
    render(view());
    fireEvent.click(await screen.findByText('Review current work'));
    const run = screen.getByRole('button', { name: 'Run now' });
    act(() => { run.click(); run.click(); run.click(); });
    expect(run).toBeDisabled();
    expect(run).toHaveAttribute('aria-busy', 'true');
    expect(request.mock.calls.filter(call => call[1] === 'thoughts/run-thought/run')).toHaveLength(1);
    await act(async () => { rejectRun(new Error('Run admission unavailable')); });
    expect(await screen.findByRole('alert')).toHaveTextContent('Run admission unavailable');
    expect(run).not.toBeDisabled();
    expect(run).toHaveAttribute('aria-busy', 'false');
    request.mockImplementation(async (_id, path) => path === 'thoughts' ? {
      ...thoughts, items: [{ ...item, lastWorkItemId: 'new-work', executionStatus: 'Queued' }]
    } : path.endsWith('/run') ? { occurrenceId: 'accepted-run' } : experience());
    fireEvent.click(run);
    await waitFor(() => expect(run).toHaveAttribute('aria-busy', 'false'));
    expect(run).toBeDisabled();
    expect(request.mock.calls.filter(call => call[1] === 'thoughts/run-thought/run')).toHaveLength(2);
  });

  it('keeps an accepted run locked across stale status and refresh failure until its new execution appears', async () => {
    let poll!: () => void;
    vi.spyOn(window, 'setInterval').mockImplementation((handler, interval) => {
      if (interval === 5000) poll = handler as () => void;
      return 123 as unknown as ReturnType<typeof window.setInterval>;
    });
    let item = { registrationId: 'run-thought', revision: 1, enabled: true, intervalSeconds: 3600,
      thinkingPrompt: 'Review current work', modelKey: null, reasoningEffort: null, nextRunAt: null,
      lastRunAt: null, lastOutcome: 'NoAction', lastWorkItemId: 'previous-work', executionStatus: 'Completed', effectiveModelKey: null, status: 'Active' };
    let failRefresh = false;
    request.mockImplementation(async (_id, path) => {
      if (path.endsWith('/run')) return { occurrenceId: 'accepted-run' };
      if (path !== 'thoughts') return experience();
      if (failRefresh) throw new Error('Status temporarily unavailable');
      return { ...thoughts, items: [item] };
    });
    render(view());
    fireEvent.click(await screen.findByText('Review current work'));
    const run = screen.getByRole('button', { name: 'Run now' });
    fireEvent.click(run);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh initiative' })).not.toBeDisabled());
    expect(run).toBeDisabled();
    expect(run).toHaveTextContent('Starting…');
    await act(async () => poll());
    expect(run).toBeDisabled();
    failRefresh = true;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh initiative' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Status temporarily unavailable');
    expect(run).toBeDisabled();
    fireEvent.click(run);
    expect(request.mock.calls.filter(call => call[1].endsWith('/run'))).toHaveLength(1);
    failRefresh = false;
    item = { ...item, lastWorkItemId: 'new-work', executionStatus: 'Queued', lastOutcome: 'Queued' };
    fireEvent.click(within(screen.getByRole('alert')).getByRole('button', { name: 'Reload' }));
    await waitFor(() => expect(run).toHaveAttribute('aria-busy', 'false'));
    expect(run).toBeDisabled();
    item = { ...item, executionStatus: 'Completed', lastOutcome: 'NoAction' };
    await act(async () => poll());
    await waitFor(() => expect(run).not.toBeDisabled());
    fireEvent.click(run);
    await waitFor(() => expect(request.mock.calls.filter(call => call[1].endsWith('/run'))).toHaveLength(2));
    expect(run).toBeDisabled();
    // A fast execution may complete between polls: its new identity still acknowledges this run.
    item = { ...item, lastWorkItemId: 'fast-work' };
    await act(async () => poll());
    await waitFor(() => expect(run).not.toBeDisabled());
  });

  it('authors seconds, minutes and hours with the minimum interval enforced', async () => {
    render(view());
    fireEvent.change(await screen.findByLabelText('Thinking prompt'), { target: { value: 'Demo review' } });
    const chooseUnit = async (label: string) => {
      fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Thought interval unit' }));
      fireEvent.click(await screen.findByText(label, { selector: '.ant-select-item-option-content' }));
    };
    await chooseUnit('Seconds');
    expect(screen.getByRole('button', { name: 'Create thought' })).toBeDisabled();
    const interval = screen.getByRole('spinbutton', { name: 'Thought interval' });
    fireEvent.change(interval, { target: { value: '15' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create thought' }));
    await waitFor(() => expect(request).toHaveBeenCalledWith('owner-instance', 'thoughts', 'POST',
      expect.objectContaining({ intervalSeconds: 15 })));
    await waitFor(() => expect(screen.getByLabelText('Thinking prompt')).toHaveValue(''));
    fireEvent.change(screen.getByLabelText('Thinking prompt'), { target: { value: 'Minute review' } });
    await chooseUnit('Minutes');
    fireEvent.change(interval, { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create thought' }));
    await waitFor(() => expect(request).toHaveBeenCalledWith('owner-instance', 'thoughts', 'POST',
      expect.objectContaining({ intervalSeconds: 120 })));
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
