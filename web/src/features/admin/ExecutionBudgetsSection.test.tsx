import { useState } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App, Button, ConfigProvider } from 'antd';
import { DefinitionExecutionBudgets, validExecutionBudgets } from './ExecutionBudgetsSection';
afterEach(cleanup);
import { ScopedExecutionBudgetLimits } from './executionBudgetLimits';
const limits = { maxSteps: 144, durationSeconds: 900, perToolSeconds: 30 };
const profile = { maxSteps: 96, durationSeconds: 600, perToolSeconds: 30, preset: 1 };
describe('execution budgets', () => {
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
    render(<ConfigProvider><App><ScopedExecutionBudgetLimits value={{ limits, error: null, reload: () => {} }}><Fixture /></ScopedExecutionBudgetLimits></App></ConfigProvider>);
    expect(await screen.findByText(/Host limits: 144 steps/)).toBeVisible();
    expect(screen.getByText(/96 steps · 10 minutes · 30s per tool · Definition default/)).toBeVisible();
    expect(screen.getByLabelText('Interactive Browser profile')).toBeInTheDocument();
  });
  it('preserves the latest unrelated Definition edits when changing a budget', async () => {
    const first = { identity: { name: 'Original' }, executionBudgets: { interactiveBrowser: profile } };
    const previousChange = vi.fn(); const currentChange = vi.fn();
    const { rerender } = render(<ConfigProvider><App><ScopedExecutionBudgetLimits value={{ limits, error: null, reload: () => {} }}><DefinitionExecutionBudgets candidate={first} busy={false} onChange={previousChange} /></ScopedExecutionBudgetLimits></App></ConfigProvider>);
    rerender(<ConfigProvider><App><ScopedExecutionBudgetLimits value={{ limits, error: null, reload: () => {} }}><DefinitionExecutionBudgets candidate={{ ...first, identity: { name: 'Updated' } }} busy={false} onChange={currentChange} /></ScopedExecutionBudgetLimits></App></ConfigProvider>);
    fireEvent.mouseDown(await screen.findByLabelText('Interactive Browser profile'));
    fireEvent.click(await screen.findByText('Deep Workflow'));
    expect(previousChange).not.toHaveBeenCalled();
    expect(currentChange).toHaveBeenCalledWith(expect.objectContaining({ identity: { name: 'Updated' },
      executionBudgets: expect.objectContaining({ interactiveBrowser: expect.objectContaining({ maxSteps: 144 }) }) }));
  });
  it('resets only the selected Definition class and permits discarding the reset', async () => {
    const initial = { executionBudgets: { interactiveBrowser: profile, standard: { maxSteps: 48, durationSeconds: 360, perToolSeconds: 30, preset: 1 } } };
    function Fixture() { const [candidate, setCandidate] = useState(initial);
      return <><DefinitionExecutionBudgets candidate={candidate} busy={false} onChange={next => setCandidate(next as typeof initial)} />
        <Button onClick={() => setCandidate(initial)}>Discard draft changes</Button>
        <output aria-label="Current budgets">{JSON.stringify(candidate.executionBudgets)}</output></>; }
    render(<ConfigProvider><App><ScopedExecutionBudgetLimits value={{ limits, error: null, reload: () => {} }}><Fixture /></ScopedExecutionBudgetLimits></App></ConfigProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Reset Interactive Browser to system default' }));
    expect(JSON.parse(screen.getByLabelText('Current budgets').textContent!)).toEqual({ interactiveBrowser: null, standard: initial.executionBudgets.standard });
    expect(screen.getByText(/48 steps · 5 minutes · 30s per tool · System default/)).toBeVisible();
    fireEvent.click(screen.getByText('Discard draft changes'));
    expect(screen.getByText(/96 steps · 10 minutes · 30s per tool · Definition default/)).toBeVisible();
  });
});
