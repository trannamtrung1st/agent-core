import { App, ConfigProvider } from 'antd';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { instanceContinuityRequest } from '../../services/adminApi';
import { ContinuityMaintenanceSection } from './ContinuityMaintenanceSection';
vi.mock('../../services/adminApi', () => ({ instanceContinuityRequest: vi.fn() }));
const request = vi.mocked(instanceContinuityRequest);
const initial = { configuredIntervalSeconds: null, effectiveIntervalSeconds: 300, minimumIntervalSeconds: 60,
  maximumIntervalSeconds: 86400, defaultIntervalSeconds: 300, usesDefault: true, configuredIntervalAllowed: true,
  revision: 0, lastMaintenanceAtUtc: null };
const view = (id = 'instance-a') => <ConfigProvider><App><ContinuityMaintenanceSection instanceId={id} /></App></ConfigProvider>;
afterEach(() => { cleanup(); vi.restoreAllMocks(); });
beforeEach(() => { vi.clearAllMocks(); request.mockResolvedValue(initial); });

describe('Automatic continuity review', () => {
  it('keeps effective default until a revisioned edit saves and can restore inheritance', async () => {
    render(view());
    await screen.findByText('Effective interval: 5 minutes · System default');
    fireEvent.click(screen.getByRole('switch', { name: 'Use system default for continuity review' }));
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Continuity review interval' }), { target: { value: '15' } });
    expect(screen.getByText('Effective interval: 5 minutes · System default')).toBeInTheDocument();
    request.mockResolvedValueOnce({ ...initial, configuredIntervalSeconds: 900, effectiveIntervalSeconds: 900, usesDefault: false, revision: 1 });
    fireEvent.click(screen.getByRole('button', { name: 'Save review interval' }));
    await screen.findByText('Effective interval: 15 minutes · Custom interval');
    expect(request).toHaveBeenLastCalledWith('instance-a', 'continuity-maintenance', 'PUT', { expectedRevision: 0, intervalSeconds: 900 });
    request.mockResolvedValueOnce({ ...initial, revision: 2 });
    fireEvent.click(screen.getByRole('switch', { name: 'Use system default for continuity review' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save review interval' }));
    await screen.findByText('Effective interval: 5 minutes · System default');
    expect(request).toHaveBeenLastCalledWith('instance-a', 'continuity-maintenance', 'PUT', { expectedRevision: 1, intervalSeconds: null });
  });
  it('rejects both range edges without silently clamping or sending an update', async () => {
    render(view()); await screen.findByText('Effective interval: 5 minutes · System default');
    fireEvent.click(screen.getByRole('switch', { name: 'Use system default for continuity review' }));
    for (const value of ['0', '1441']) {
      fireEvent.change(screen.getByRole('spinbutton', { name: 'Continuity review interval' }), { target: { value } });
      fireEvent.blur(screen.getByRole('spinbutton', { name: 'Continuity review interval' }));
      expect(screen.getByRole('spinbutton')).toHaveValue(value);
      expect(screen.getByText('Enter an interval between 1 and 1440 minutes.')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save review interval' })).toBeDisabled();
    }
    expect(request).toHaveBeenCalledTimes(1);
  });
  it('retains an edit after a failed save and exposes recovery', async () => {
    render(view()); await screen.findByText('Effective interval: 5 minutes · System default');
    fireEvent.click(screen.getByRole('switch', { name: 'Use system default for continuity review' }));
    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '30' } });
    request.mockRejectedValueOnce(new Error('conflict'));
    fireEvent.click(screen.getByRole('button', { name: 'Save review interval' }));
    await screen.findByRole('alert');
    expect(screen.getByRole('spinbutton')).toHaveValue('30');
    expect(screen.getByText('Effective interval: 5 minutes · System default')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reload review interval' }));
    await waitFor(() => expect(screen.getByRole('spinbutton')).toHaveValue('5'));
  });
  it('discards a stale read when navigating to another instance', async () => {
    let resolve!: (value: typeof initial) => void;
    request.mockReturnValueOnce(new Promise(done => { resolve = done; }));
    const mounted = render(view()); mounted.rerender(view('instance-b'));
    await screen.findByText('Effective interval: 5 minutes · System default');
    await act(async () => { resolve({ ...initial, effectiveIntervalSeconds: 900 }); });
    expect(screen.queryByText(/Effective interval: 15/)).not.toBeInTheDocument();
  });
});
