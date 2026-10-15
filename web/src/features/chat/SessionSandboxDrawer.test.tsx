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
  expect(screen.queryByRole('button', {name:'Command diagnostics'})).not.toBeInTheDocument();
  expect(screen.getByText('Output was truncated at the bounded log limit.')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Standard error'})); await screen.findByText('failure details');
});
it('groups labeled command diagnostics with metadata and copies the owned context', async () => {
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, 'clipboard', {configurable:true,value:{writeText}});
  vi.mocked(getSandboxExecution).mockResolvedValue({...result,exitCode:0,diagnosticId:'diag-command',truncated:false});
  const onClose = vi.fn();
  render(<ConfigProvider theme={{token:{motion:false}}}><App><SessionSandboxDrawer sessionId="s1" open narrow={false} onClose={onClose} /></App></ConfigProvider>);
  const section = await screen.findByRole('region', {name:'Sandbox command result'});
  const trigger = within(section).getByRole('button', {name:'Command diagnostics'});
  expect(trigger).toHaveTextContent('Command diagnostics');
  expect(trigger.closest('.ant-flex')).toHaveTextContent('Exit 0 · 1.2s · Files committed');
  expect(trigger.compareDocumentPosition(within(section).getByRole('button', {name:'Standard output'})) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  fireEvent.click(trigger);
  await waitFor(() => expect(screen.getByText('Command diagnostics', {selector:'.ant-popover-title'})).toBeVisible());
  const details = screen.getByTestId('diagnostic-details');
  expect(details).toHaveTextContent('diag-command');
  expect(details).toHaveTextContent('Session ID');
  fireEvent.click(within(details).getByRole('button', {name:'Copy details'}));
  await waitFor(() => expect(writeText).toHaveBeenCalledWith('Agent Core diagnostic\nDiagnostic ID: diag-command\nSession ID: s1'));
  fireEvent.keyDown(within(details).getByRole('button', {name:'Copy details'}), {key:'Escape'});
  expect(onClose).not.toHaveBeenCalled(); expect(trigger).toHaveFocus();
  await waitFor(() => expect(details).not.toBeVisible());
  fireEvent.keyDown(trigger, {key:'Escape'}); expect(onClose).toHaveBeenCalledTimes(1);
  expect(resetSessionSandbox).not.toHaveBeenCalled(); expect(stopSandboxExecution).not.toHaveBeenCalled();
});
it('keeps initialization and fallback command diagnostics in their own metadata groups', async () => {
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,diagnosticId:'diag-environment',initialization:{state:'Failed',profileId:'workbench',profileRevision:2,instanceRevision:3,startedAt:null,finishedAt:null,exitCode:7,stdout:'',stderr:'setup failed',truncated:false,durationMs:1000,diagnosticId:'diag-initialization',phase:'Instance'}});
  render(tree());
  const section = await screen.findByRole('region', {name:'Sandbox initialization'});
  const trigger = within(section).getByRole('button', {name:'Initialization diagnostics'});
  expect(trigger.closest('.ant-flex')).toHaveTextContent('Profile revision 2 · Instance revision 3 · Exit 7 · 1.0s');
  fireEvent.click(trigger);
  expect(await screen.findByTestId('diagnostic-id')).toHaveTextContent('diag-initialization');
  fireEvent.click(screen.getByRole('button', {name:'Command diagnostics'}));
  await waitFor(() => expect(screen.getAllByTestId('diagnostic-id').some(el => el.textContent==='diag-environment')).toBe(true));
});
it('keeps recovery disabled after a failed read until status is observed again',async () => {
  vi.mocked(getSessionSandbox).mockRejectedValueOnce(new Error('offline')); render(tree());
  await screen.findByText(/Sandbox status is unavailable/); expect(screen.queryByRole('button',{name:'Reset sandbox'})).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button',{name:'Retry'})); await screen.findByRole('button',{name:'Reset sandbox'});
});
it('shows confirmed absence without an error or recovery action', async () => {
  vi.mocked(getSessionSandbox).mockResolvedValue(null);
  render(tree()); await screen.findByText(/No sandbox environment yet/);
  expect(screen.queryByRole('button', { name:'Retry' })).not.toBeInTheDocument();
  expect(getSandboxExecution).not.toHaveBeenCalled();
});
it('recovers automatically from a failed read without presenting false absence', async () => {
  vi.mocked(getSessionSandbox).mockRejectedValueOnce(new Error('offline'));
  render(tree()); await screen.findByText(/Sandbox status is unavailable/);
  expect(screen.queryByText(/No sandbox environment yet/)).not.toBeInTheDocument();
  await screen.findByText('test failed', {}, { timeout: 3500 });
  expect(screen.queryByText(/Sandbox status is unavailable/)).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeEnabled();
});
it('retains evidence after a refresh failure and clears the error on recovery', async () => {
  render(tree()); await screen.findByText('test failed');
  vi.mocked(getSessionSandbox).mockRejectedValueOnce(new Error('offline'));
  fireEvent.click(screen.getByRole('button', { name:'Inspect status' }));
  await screen.findByText(/Sandbox status is unavailable/);
  expect(screen.getByText('test failed')).toBeInTheDocument();
  expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name:'Retry' }));
  await waitFor(() => expect(screen.queryByText(/Sandbox status is unavailable/)).not.toBeInTheDocument());
  expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeEnabled();
});
it('shares a pending manual refresh with polling and ignores late reads after Session changes', async () => {
  const view = render(tree()); await screen.findByText('test failed');
  let finish!: (value: SandboxEnvironment) => void;
  vi.mocked(getSessionSandbox).mockResolvedValue({ ...env, sessionId:'s2', generation:'g2' });
  vi.mocked(getSessionSandbox).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  vi.useFakeTimers();
  try {
    fireEvent.click(screen.getByRole('button', { name:'Inspect status' }));
    expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeDisabled();
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(getSessionSandbox).toHaveBeenCalledTimes(2);
    await act(async () => { view.rerender(tree('s2')); });
    await act(async () => { finish({ ...env, profileId:'obsolete-profile' }); });
    expect(screen.queryByText(/obsolete-profile/)).not.toBeInTheDocument();
    expect(screen.getByText(/workbench · revision 2/)).toBeInTheDocument();
  } finally { vi.useRealTimers(); }
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
  await waitFor(() => expect(resetSessionSandbox).toHaveBeenCalledWith('s1','g1',env.activeExecutionId??env.lastExecutionId));
});

it('explains a confirmed commit even when command completion is uncertain', async () => {
  vi.mocked(getSandboxExecution).mockResolvedValue({ ...result, status:'Indeterminate', workspaceSyncStatus:'committed' });
  render(tree()); await screen.findByText(/Canonical files were committed, but command completion was not confirmed/);
  expect(screen.queryByText(/Files may already/)).not.toBeInTheDocument();
});
it('does not label an unknown commit as definitely absent', async () => {
  vi.mocked(getSandboxExecution).mockResolvedValue({ ...result, status:'Indeterminate', workspaceSyncStatus:'unknown' });
  render(tree()); await screen.findByText(/Files may already have been committed/);
  expect(screen.getByText(/File commit outcome unknown/)).toBeInTheDocument();
  expect(screen.queryByText(/Files not committed/)).not.toBeInTheDocument();
});
it('explains initialization recovery without losing the diagnostic or reset action', async () => {
  vi.mocked(getSessionSandbox).mockResolvedValue({ ...env, state:'Failed', message:'Environment initialization failed. Correct and verify the profile script, then reset before retrying.' });
  render(tree()); await screen.findByText('Recovery guidance');
  expect(screen.getByText(/Correct and verify the profile script/)).toBeInTheDocument();
  expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeEnabled();
});
it('explains expiry and retained files while leaving a terminated generation stopped', async () => {
  vi.mocked(getSessionSandbox).mockResolvedValue({ ...env, state:'Terminated' });
  render(tree()); await screen.findByText('Environment ended');
  expect(screen.getByText(/Saved Workspace files and Artifacts are retained/)).toBeInTheDocument();
  expect(screen.getByRole('button', { name:'Reset sandbox' })).toBeDisabled();
});
it('allows reset to clear a retained profile choice after clean termination', async () => {
  vi.mocked(getSessionSandbox).mockResolvedValue({ ...env, state:'Terminated', selectedProfileId:'workbench', cleanupReason:'idleExpired' });
  render(tree()); await screen.findByText('Environment ended');
  fireEvent.click(screen.getByRole('button', { name:'Reset sandbox' }));
  const modal = await resetDialog();
  expect(modal).toHaveTextContent('Reset clears the Session profile choice');
  fireEvent.click(within(modal).getByRole('button', { name:'Reset sandbox' }));
  await waitFor(() => expect(resetSessionSandbox).toHaveBeenCalledWith('s1','g1','e1'));
});
it('retains initialization output and permits explicit reset after uncertain setup was cleaned up',async()=>{
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,state:'Terminated',initialization:{state:'Failed',profileId:'workbench',profileRevision:1,instanceRevision:3,startedAt:'2026-10-14T00:00:00Z',finishedAt:'2026-10-14T00:00:01Z',exitCode:7,stdout:'Hello, world!',stderr:'setup failed',truncated:false,durationMs:1000,diagnosticId:null,phase:'Instance'}});
  render(tree());await screen.findByText(/Scripts are never repeated automatically/);
  expect(screen.getByRole('button',{name:'Reset sandbox'})).toBeEnabled();
  const output=screen.getByRole('button',{name:'Initialization standard output'});expect(output).toHaveAttribute('aria-expanded','false');
  fireEvent.click(output);await screen.findByText('Hello, world!');
});

it('accepts a confirmed recovery while a normal status poll is in flight',async()=>{
  render(tree());await screen.findByText('test failed');
  fireEvent.click(screen.getByRole('button',{name:'Reset sandbox'}));const modal=await resetDialog();
  let finish!:(value:SandboxEnvironment)=>void;
  vi.mocked(getSessionSandbox).mockImplementationOnce(()=>new Promise(resolve=>{finish=resolve;}));
  vi.useFakeTimers();
  try {
    await act(async()=>{await vi.advanceTimersByTimeAsync(2000);});
    await act(async()=>{fireEvent.click(within(modal).getByRole('button',{name:'Reset sandbox'}));});
    expect(resetSessionSandbox).toHaveBeenCalledWith('s1','g1','e1');
    await act(async()=>finish({...env,state:'Terminated'}));
  } finally {vi.useRealTimers();}
});

it('rejects a recovery confirmation when another execution replaces its evidence',async()=>{
  render(tree());await screen.findByText('test failed');
  fireEvent.click(screen.getByRole('button',{name:'Reset sandbox'}));const modal=await resetDialog();
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,lastExecutionId:'e2'});
  fireEvent.click(screen.getByRole('button',{name:'Inspect status'}));await waitFor(()=>expect(getSandboxExecution).toHaveBeenCalledWith('s1','e2',expect.any(AbortSignal)));
  await act(async()=>{fireEvent.click(within(modal).getByRole('button',{name:'Reset sandbox'}));});
  expect(resetSessionSandbox).not.toHaveBeenCalled();
});

it('retains evidence after failed reset and requires a successful inspection before retrying',async()=>{
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,state:'NeedsResync'});
  vi.mocked(getSandboxExecution).mockResolvedValue({...result,status:'Indeterminate',workspaceSyncStatus:'unknown'});
  vi.mocked(resetSessionSandbox).mockRejectedValueOnce(new Error('cleanup pending'));
  render(tree());await screen.findByText('test failed');
  fireEvent.click(screen.getByRole('button',{name:'Reset sandbox'}));
  fireEvent.click(within(await resetDialog()).getByRole('button',{name:'Reset sandbox'}));
  await screen.findByText(/Sandbox recovery was not confirmed/);
  expect(screen.getByText('test failed')).toBeInTheDocument();
  expect(screen.getByRole('button',{name:'Reset sandbox'})).toBeDisabled();
  fireEvent.click(screen.getByRole('button',{name:'Inspect status'}));
  await waitFor(()=>expect(screen.queryByText(/Sandbox recovery was not confirmed/)).not.toBeInTheDocument());
  expect(screen.getByRole('button',{name:'Reset sandbox'})).toBeEnabled();
});

it('explains known clean cancellation with retained files and a new independent command',async()=>{
  vi.mocked(getSessionSandbox).mockResolvedValue({...env,state:'Terminated',cleanupReason:'cancelled',profileName:'Workbench'});
  vi.mocked(getSandboxExecution).mockResolvedValue({...result,status:'Canceled',workspaceSyncStatus:'notCommitted',failureCategory:'cancellation',cancellationReason:'userStop',terminationConfirmed:true});
  render(tree());await screen.findByText('Stopped by user');
  expect(screen.getByText(/Review the retained execution receipt before starting a new authorized command/)).toBeInTheDocument();
  expect(screen.getByText('Last selected profile')).toBeInTheDocument();
  expect(screen.getByRole('button',{name:'Reset sandbox'})).toBeDisabled();
});
