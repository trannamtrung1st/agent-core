import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { vi, test, expect, beforeEach } from 'vitest';
import { InstanceStateSection } from './InstanceStateSection';
import { getRuntimeState, type RuntimeState } from '../../services/runtimeState';

vi.mock('../../services/runtimeState', () => ({ getRuntimeState: vi.fn() }));
const read = vi.mocked(getRuntimeState);
const snapshot = (owner = 'owner', count = 2): RuntimeState => ({
  agentInstanceId: owner, observedAtUtc: new Date().toISOString(), freshThroughUtc: new Date(Date.now() + 15_000).toISOString(), availability: 'partial',
  sessions: { open: count, paused: 0, terminal: 0, lastActivityAtUtc: null }, hostedSessions: { runtimes: 0, attached: 0 },
  runs: { queued: 0, executing: 0, waitingApproval: 0, waitingSignal: 0, retrying: 0, terminal: 0 },
  resources: [{ kind: 'browser', availability: 'unavailable', sharing: 'isolated', observedAtUtc: new Date().toISOString(),
    liveContexts: null, openPages: null, activeGenerations: null, activeCommands: null, cleanupPending: null, code: 'provider_unavailable' }],
  issues: [{ kind: 'browser', code: 'provider_unavailable' }],
});
beforeEach(() => { read.mockReset(); });
const view = (owner = 'owner', active = true) => <ConfigProvider><InstanceStateSection instanceId={owner} active={active} /></ConfigProvider>;

test('failed refresh retains a stale observation, unknown provider counts and retry recovers', async () => {
  read.mockResolvedValueOnce(snapshot()).mockRejectedValueOnce(new Error('private provider error')).mockResolvedValue(snapshot('owner', 3));
  render(view());
  await screen.findByText('Partial');
  expect(screen.getAllByText('Unknown')).toHaveLength(2);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh state' })).not.toHaveClass('ant-btn-loading'));
  fireEvent.click(screen.getByRole('button', { name: 'Refresh state' }));
  await screen.findByText('Stale');
  expect(screen.queryByText('private provider error')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Retry state' }));
  await screen.findByText('3');
  expect(screen.queryByText('Stale')).toBeNull();
});

test('inactive surfaces do not read and late foreign responses cannot replace the selected owner', async () => {
  let complete: (value: RuntimeState) => void = () => {};
  read.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; })).mockResolvedValue(snapshot('other', 4));
  const rendered = render(view('owner', false));
  expect(read).not.toHaveBeenCalled();
  rendered.rerender(view());
  await waitFor(() => expect(read).toHaveBeenCalledTimes(1));
  const firstSignal = read.mock.calls[0][1];
  rendered.rerender(view('other'));
  await screen.findByText('4');
  expect(firstSignal.aborted).toBe(true);
  await act(async () => { complete(snapshot('owner', 99)); });
  expect(screen.queryByText('99')).toBeNull();
  rendered.rerender(view('other', false));
  expect(read.mock.calls[1][1].aborted).toBe(true);
});
