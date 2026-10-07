vi.mock("./ContinuityMaintenanceSection", () => ({ ContinuityMaintenanceSection: () => null }));
import { App, ConfigProvider } from 'antd';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ExperienceSection } from './InstanceContinuitySection';
import { instanceContinuityRequest, type ExperienceItem } from '../../services/adminApi';
import { listModels } from '../../services/api';

vi.mock('./InstanceSchedulesSection', () => ({ InstanceSchedulesSection: () => null }));
vi.mock('../../services/adminApi', () => ({ instanceContinuityRequest: vi.fn() }));
vi.mock('../../services/api', () => ({ listModels: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
let enabled = false;
const experience = () => ({ enabled, settingsRevision: 3, contextBudgetCharacters: 6000, items: [] });
const thoughts = { minIntervalSeconds: 15, items: [] };
function view(id = 'owner-instance') { return <ConfigProvider><App><ExperienceSection instanceId={id} onWork={vi.fn()} /></App></ConfigProvider>; }
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
  it('does not claim a checkpoint disappeared when its source read fails and recovers on Reload', async () => {
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() });
    const ui = (selection?: { workItemId: string; request: number }) => <ConfigProvider><App>
      <ExperienceSection instanceId="owner-instance" onWork={vi.fn()} selection={selection} /></App></ConfigProvider>;
    const mounted = render(ui());
    await screen.findByText(/No experience yet/);
    request.mockRejectedValue(new Error('Network disconnected'));
    mounted.rerender(ui({ workItemId: 'work-1', request: 1 }));
    const retry = await screen.findByRole('button', { name: /^Reload$/ });
    expect(screen.queryByText('This experience checkpoint is not available in the current records')).not.toBeInTheDocument();
    request.mockResolvedValue({ ...experience(), items: [record(1)] });
    fireEvent.click(retry);
    await screen.findByRole('button', { name: 'View experience: Atlas review 1' });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
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

  it('opens a retrospection checkpoint beyond pagination and keeps an unsaved source draft', async () => {
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", { configurable: true, value: vi.fn() });
    enabled = true;
    request.mockResolvedValue({ ...experience(), items: Array.from({ length: 21 }, (_, index) => record(index + 1)) });
    const onWork = vi.fn();
    const ui = (selection?: { workItemId: string; request: number }) => <ConfigProvider><App><ExperienceSection instanceId="owner-instance" onWork={onWork} selection={selection} /></App></ConfigProvider>;
    const view = render(ui());
    await screen.findByRole('button', { name: 'View experience: Atlas review 21' });
    fireEvent.click(screen.getByRole("button", { name: "Enter Session ID" }));
    fireEvent.change(screen.getByLabelText('Session ID'), { target: { value: 'Unsaved session' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Search experience' }), { target: { value: 'Atlas review 21' } });
    view.rerender(ui({ workItemId: 'work-1', request: 1 }));
    const selected = await screen.findByRole('button', { name: 'View experience: Atlas review 1' });
    await waitFor(() => expect(selected).toHaveFocus());
    expect(selected).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Session ID')).toHaveValue('Unsaved session');
    fireEvent.click(screen.getByRole('button', { name: 'View generation run' }));
    expect(onWork).toHaveBeenCalledWith('work-1');
  });

});
