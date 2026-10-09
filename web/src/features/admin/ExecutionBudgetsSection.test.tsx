import { useState } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App, ConfigProvider } from 'antd';
import { DefinitionExecutionBudgets, InstanceExecutionBudgets, validExecutionBudgets } from './ExecutionBudgetsSection';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ getExecutionBudgets: vi.fn(), setExecutionBudgets: vi.fn() }));
afterEach(cleanup);
const profile = { maxSteps: 96, durationSeconds: 600, perToolSeconds: 30, preset: 1 };
describe('execution budgets', () => {
  it('rejects out of range and fractional custom values before save', () => {
    expect(validExecutionBudgets({ interactiveBrowser: profile })).toBe(true);
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, maxSteps: 145, preset: 3 } })).toBe(false);
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, durationSeconds: 900.5, preset: 3 } })).toBe(false);
    expect(validExecutionBudgets({ interactiveBrowser: { ...profile, perToolSeconds: 31, preset: 3 } })).toBe(false);
  });
  it('shows Definition presets and host limits in the existing editor', () => {
    function Fixture() { const [candidate, setCandidate] = useState({ executionBudgets: { interactiveBrowser: profile } });
      return <DefinitionExecutionBudgets candidate={candidate} busy={false} onChange={v => setCandidate(v as typeof candidate)} />; }
    render(<ConfigProvider><App><Fixture /></App></ConfigProvider>);
    expect(screen.getByText(/Host limits: 144 steps/)).toBeVisible();
    expect(screen.getByText(/96 steps · 10 minutes · 30s per tool · Definition default/)).toBeVisible();
    expect(screen.getByLabelText('Interactive Browser profile')).toBeInTheDocument();
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
});
