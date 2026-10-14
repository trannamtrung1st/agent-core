import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, expect, it, vi } from 'vitest';
import { SessionSandboxDrawer } from './SessionSandboxDrawer';
import { getSessionSandbox, getSandboxExecution, resetSessionSandbox, stopSandboxExecution, type SandboxEnvironment, type SandboxExecution } from '../../services/sandboxes';
vi.mock('../../services/sandboxes', () => ({ getSessionSandbox:vi.fn(),getSandboxExecution:vi.fn(),resetSessionSandbox:vi.fn(),stopSandboxExecution:vi.fn() }));
const env: SandboxEnvironment = { sessionId:'s1',generation:'g1',state:'Ready',profileId:'workbench',profileRevision:2,expiresAt:'2026-10-14T03:00:00Z',activeExecutionId:null,lastExecutionId:'e1',diagnosticId:null };
const result: SandboxExecution = { executionId:'e1',status:'Completed',exitCode:7,stdout:'test failed',stderr:'failure details',truncated:true,durationMs:1200,workspaceSyncStatus:'committed',diagnosticId:null,background:false,commandText:'python -m pytest' };
beforeEach(() => { vi.mocked(getSessionSandbox).mockReset().mockResolvedValue(env); vi.mocked(getSandboxExecution).mockReset().mockResolvedValue(result); vi.mocked(resetSessionSandbox).mockReset().mockResolvedValue({reset:true}); vi.mocked(stopSandboxExecution).mockReset().mockResolvedValue(result); });
function tree(id='s1') { return <ConfigProvider theme={{token:{motion:false}}}><App><SessionSandboxDrawer sessionId={id} open narrow={false} onClose={() => {}} /></App></ConfigProvider>; }
async function resetDialog() {
  // AntD's test-mode id hook uses "test-id" for both nested dialogs. The real
  // browser uses unique ids; locate the visible confirmation title here.
  return (await screen.findByText('Reset this Session sandbox?', { selector: '.ant-modal-confirm-title' })).closest('[role="dialog"]') as HTMLElement;
}
it('shows failed command evidence with separate collapsed stderr and truthful truncation',async () => {
  render(tree()); await screen.findByText('test failed'); expect(screen.getByText(/Exit 7.*committed/)).toBeInTheDocument();
  expect(screen.getByText('Output was truncated at the bounded log limit.')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Standard error'})); await screen.findByText('failure details');
});
it('keeps recovery disabled after a failed read until status is observed again',async () => {
  vi.mocked(getSessionSandbox).mockRejectedValueOnce(new Error('offline')); render(tree());
  await screen.findByText(/Sandbox status is unavailable/); expect(screen.queryByRole('button',{name:'Reset sandbox'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Retry'})); await screen.findByRole('button',{name:'Reset sandbox'});
});
it('requires exact generation confirmation and ignores a modal retained across Session selection',async () => {
  const view=render(tree()); await screen.findByText('test failed'); fireEvent.click(screen.getByRole('button',{name:'Reset sandbox'}));
  const modal=await resetDialog();
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,sessionId:'s2',generation:'g2'});
  view.rerender(tree('s2')); await waitFor(() => expect(getSessionSandbox).toHaveBeenCalledWith('s2',expect.any(AbortSignal)));
  await act(async () => { fireEvent.click(within(modal).getByRole('button',{name:'Reset sandbox'})); });
  expect(resetSessionSandbox).not.toHaveBeenCalled();
});
it('resets only the observed owned generation and keeps foreground cancellation in Chat',async () => {
  render(tree()); await screen.findByText('test failed'); expect(screen.queryByRole('button',{name:'Stop command'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Reset sandbox'}));
  fireEvent.click(within(await resetDialog()).getByRole('button',{name:'Reset sandbox'}));
  await waitFor(() => expect(resetSessionSandbox).toHaveBeenCalledWith('s1','g1'));
});
