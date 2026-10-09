import { useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Form } from 'antd';
import { describe, expect, it, vi } from 'vitest';
import { AutomationEventFilter } from './AutomationEventFilter';
import type { AutomationTrigger, FilterTestResult } from '../../services/adminApi';
const request = vi.hoisted(() => vi.fn());
vi.mock('../../services/adminApi', () => ({ instanceContinuityRequest: request }));
const sample = { schemaVersion: 1, data: { total: 150 } };
function Editor() {
  const [trigger, setTrigger] = useState<Exclude<AutomationTrigger, { kind: 'schedule' }>>({ kind: 'coreEvent', coreEventKey: 'run.completed' });
  return <Form><AutomationEventFilter instanceId='owned-instance' trigger={trigger} example={sample} disabled={false} onChange={setTrigger} /></Form>;
}
describe('event filter testing', () => {
  it('uses the backend evaluator, shows a safe error and never submits an Automation', async () => {
    request.mockResolvedValueOnce({ matched: false, status: 'notMatched' }).mockResolvedValueOnce({ matched: null, status: 'error', code: 'filter-result-not-boolean' });
    render(<Editor />);
    fireEvent.change(screen.getByLabelText('Event filter expression'), { target: { value: 'event.data.total < 100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Test filter' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Not matched'));
    expect(request).toHaveBeenCalledWith('owned-instance', 'automations/filter-test', 'POST', { expression: 'event.data.total < 100', event: sample });
    
    fireEvent.change(screen.getByLabelText('Event filter expression'), { target: { value: 'event.data.total' } });
    fireEvent.click(screen.getByRole('button', { name: 'Test filter' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('The expression must return true or false.'));
  });
  it('discards a result when the tested expression changes', async () => {
    let resolve!: (value: FilterTestResult) => void;
    request.mockReturnValueOnce(new Promise<FilterTestResult>(r => { resolve = r; }));
    render(<Editor />);
    fireEvent.click(screen.getByRole('button', { name: 'Test filter' }));
    fireEvent.change(screen.getByLabelText('Event filter expression'), { target: { value: 'false' } });
    await act(async () => { resolve({ matched: true, status: 'matched' }); });
    expect(screen.getByRole('status')).not.toHaveTextContent('Matched');
    expect(screen.getByRole('button', { name: 'Test filter' })).toBeEnabled();
  });
});
