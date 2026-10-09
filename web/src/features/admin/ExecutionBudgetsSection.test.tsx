import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App, Button, ConfigProvider } from 'antd';
import { DefinitionExecutionBudgets, InstanceExecutionBudgets, validExecutionBudgets } from './ExecutionBudgetsSection';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ getExecutionBudgetLimits: vi.fn(), getExecutionBudgets: vi.fn(), setExecutionBudgets: vi.fn() }));
afterEach(cleanup);
const limits = { maxSteps: 144, durationSeconds: 900, perToolSeconds: 30 };
const profile = { maxSteps: 96, durationSeconds: 600, perToolSeconds: 30, preset: 1 };
describe('execution budgets', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getExecutionBudgetLimits).mockResolvedValue(limits); });
  it('rejects out of range and fractional custom values before save', () => {
    expect(validExecutionBudgets({ interactiveBrowser: profile }, limits)).toBe(true);
    expect(validExecutionBudgets({ interactiveBrowser: profile }, null)).toBe(false);
    for (const [preset, factor] of [[0, 1], [1, 2], [2, 3]]) {
      expect(validExecutionBudgets({ interactiveBrowser: { maxSteps: 48 * factor, durationSeconds: 300 * factor, perToolSeconds: 30, preset } }, limits)).toBe(true);
    }
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, maxSteps: 145, preset: 3 } }, limits)).toBe(false);
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, durationSeconds: 900.5, preset: 3 } }, limits)).toBe(false);
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, perToolSeconds: 31, preset: 3 } }, limits)).toBe(false);
  });
  it('shows Definition presets and host limits in the existing editor', async () => {
    function Fixture() { const [candidate, setCandidate] = useState({ executionBudgets: { interactiveBrowser: profile } });
      return <DefinitionExecutionBudgets candidate={candidate} busy={false} onChange={v => setCandidate(v as typeof candidate)} />; }
    render(<ConfigProvider><App><Fixture /></App></ConfigProvider>);
    expect(await screen.findByText(/Host limits: 144 steps/)).toBeVisible();
    expect(screen.getByText(/96 steps · 10 minutes · 30s per tool · Definition default/)).toBeVisible();
    expect(screen.getByLabelText('Interactive Browser profile')).toBeInTheDocument();
  });
  it('preserves the latest unrelated Definition edits when changing a budget', async () => {
    const first = { identity: { name: 'Original' }, executionBudgets: { interactiveBrowser: profile } };
    const previousChange = vi.fn(); const currentChange = vi.fn();
    const { rerender } = render(<ConfigProvider><App><DefinitionExecutionBudgets candidate={first} busy={false} onChange={previousChange} /></App></ConfigProvider>);
    rerender(<ConfigProvider><App><DefinitionExecutionBudgets candidate={{ ...first, identity: { name: 'Updated' } }} busy={false} onChange={currentChange} /></App></ConfigProvider>);
    fireEvent.mouseDown(await screen.findByLabelText('Interactive Browser profile'));
    fireEvent.click(await screen.findByText('Deep Workflow'));
    expect(previousChange).not.toHaveBeenCalled();
    expect(currentChange).toHaveBeenCalledWith(expect.objectContaining({ identity: { name: 'Updated' },
      executionBudgets: expect.objectContaining({ interactiveBrowser: expect.objectContaining({ maxSteps: 144 }) }) }));
  });
  it('inherits each class independently, retains a conflicting draft and permits discard', async () => {
    vi.mocked(api.getExecutionBudgets).mockResolvedValue({ revision: 3, executionBudgets: { interactiveBrowser: profile }, definitionDefaults: null });
    vi.mocked(api.setExecutionBudgets).mockRejectedValue(new Error('The Instance changed. Reload and review your changes before saving.'));
    render(<ConfigProvider><App><InstanceExecutionBudgets instanceId="owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
    await screen.findByText(/96 steps · 10 minutes · 30s per tool · Instance override/);
    fireEvent.mouseDown(screen.getByLabelText('Interactive Browser profile'));
    fireEvent.click(await screen.findByText('Deep Workflow'));
    expect(screen.getByText(/Unsaved budget changes/)).toBeVisible();
    fireEvent.click(screen.getByText('Save execution budgets'));
    await waitFor(() => expect(api.setExecutionBudgets).toHaveBeenCalledWith('owner', 3, expect.objectContaining({ interactiveBrowser: expect.objectContaining({ maxSteps: 144 }) })));
    await screen.findByText('The Instance changed. Reload and review your changes before saving.');
    expect(screen.getByText(/144 steps · 15 minutes · 30s per tool · Instance override/)).toBeVisible();
    vi.mocked(api.getExecutionBudgets).mockResolvedValue({ revision: 4, executionBudgets: { interactiveBrowser: profile }, definitionDefaults: null });
    fireEvent.click(screen.getByText('Reload'));
    await waitFor(() => expect(api.getExecutionBudgets).toHaveBeenCalledTimes(2));
    await screen.findByText(/144 steps · 15 minutes · 30s per tool · Instance override/);
    expect(screen.getByText(/Unsaved budget changes/)).toBeVisible();
    fireEvent.click(screen.getByText('Discard changes'));
    expect(screen.queryByText(/Unsaved budget changes/)).not.toBeInTheDocument();
    expect(screen.getByText(/24 steps · 3 minutes · 30s per tool · System default/)).toBeVisible();
  });
  it('resets only the selected Definition class and permits discarding the reset', async () => {
    const initial = { executionBudgets: { interactiveBrowser: profile, standard: { maxSteps: 48, durationSeconds: 360, perToolSeconds: 30, preset: 1 } } };
    function Fixture() { const [candidate, setCandidate] = useState(initial);
      return <><DefinitionExecutionBudgets candidate={candidate} busy={false} onChange={next => setCandidate(next as typeof initial)} />
        <Button onClick={() => setCandidate(initial)}>Discard draft changes</Button>
        <output aria-label="Current budgets">{JSON.stringify(candidate.executionBudgets)}</output></>; }
    render(<ConfigProvider><App><Fixture /></App></ConfigProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Reset Interactive Browser to system default' }));
    expect(JSON.parse(screen.getByLabelText('Current budgets').textContent!)).toEqual({ interactiveBrowser: null, standard: initial.executionBudgets.standard });
    expect(screen.getByText(/48 steps · 5 minutes · 30s per tool · System default/)).toBeVisible();
    fireEvent.click(screen.getByText('Discard draft changes'));
    expect(screen.getByText(/96 steps · 10 minutes · 30s per tool · Definition default/)).toBeVisible();
  });
  it('uses returned ceilings for display, accessible validation and Save admission', async () => {
    vi.mocked(api.getExecutionBudgetLimits).mockResolvedValue({ maxSteps: 100, durationSeconds: 700, perToolSeconds: 25 });
    vi.mocked(api.getExecutionBudgets).mockResolvedValue({ revision: 3, executionBudgets: { interactiveBrowser: { ...profile, perToolSeconds: 20, preset: 3 } }, definitionDefaults: null });
    render(<ConfigProvider><App><InstanceExecutionBudgets instanceId="limits-owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
    expect(await screen.findByText(/Host limits: 100 steps/)).toBeVisible();
    const group = screen.getByRole('group', { name: 'Interactive Browser budget' });
    fireEvent.click(within(group).getByText('Advanced limits'));
    fireEvent.change(await screen.findByLabelText('Interactive Browser maxSteps'), { target: { value: '101' } });
    expect(screen.getByLabelText('Interactive Browser maxSteps')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Enter a whole number from 8 to 100.')).toBeVisible();
    expect(screen.getByText('Save execution budgets').closest('button')).toBeDisabled();
    expect(api.setExecutionBudgets).not.toHaveBeenCalled();
  });
  it('fails closed on limit loading and restores the retained draft after retry', async () => {
    vi.mocked(api.getExecutionBudgetLimits).mockRejectedValueOnce(new Error('offline'));
    vi.mocked(api.getExecutionBudgets).mockResolvedValue({ revision: 3, executionBudgets: { interactiveBrowser: profile }, definitionDefaults: null });
    render(<ConfigProvider><App><InstanceExecutionBudgets instanceId="retry-owner" archived={false} onUpdated={() => {}} /></App></ConfigProvider>);
    await screen.findByText(/Execution limits could not be loaded/);
    expect(screen.getByText('Save execution budgets').closest('button')).toBeDisabled();
    expect(screen.queryByLabelText('Interactive Browser profile')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Retry limits'));
    expect(await screen.findByText(/96 steps · 10 minutes · 30s per tool · Instance override/)).toBeVisible();
    expect(api.setExecutionBudgets).not.toHaveBeenCalled();
  });
  it('ignores an earlier Instance save after switching to another Instance', async () => {
    let complete!: (value: Awaited<ReturnType<typeof api.setExecutionBudgets>>) => void;
    vi.mocked(api.getExecutionBudgets).mockImplementation(async id => ({ revision: id === 'first' ? 3 : 9,
      executionBudgets: id === 'first' ? { interactiveBrowser: profile } : null, definitionDefaults: null }));
    vi.mocked(api.setExecutionBudgets).mockImplementation(() => new Promise(resolve => { complete = resolve; }));
    const updated = vi.fn();
    const view = (id: string) => <ConfigProvider><App><InstanceExecutionBudgets instanceId={id} archived={false} onUpdated={updated} /></App></ConfigProvider>;
    const { rerender } = render(view('first'));
    await screen.findByText(/96 steps · 10 minutes · 30s per tool · Instance override/);
    fireEvent.mouseDown(screen.getByLabelText('Interactive Browser profile'));
    fireEvent.click(await screen.findByText('Deep Workflow'));
    fireEvent.click(screen.getByText('Save execution budgets'));
    await waitFor(() => expect(complete).toBeTypeOf('function'));
    rerender(view('second'));
    await screen.findByText(/48 steps · 5 minutes · 30s per tool · System default/);
    await act(async () => complete({ revision: 4 }));
    expect(updated).not.toHaveBeenCalled();
    expect(screen.getByText(/48 steps · 5 minutes · 30s per tool · System default/)).toBeVisible();
    expect(screen.queryByText('144 steps · 15 minutes · 30s per tool · Instance override', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText(/Unsaved budget changes/)).not.toBeInTheDocument();
  });
});
