import { afterEach, beforeEach, expect, it, vi } from 'vitest';
const tenant = vi.hoisted(() => ({ witness: vi.fn() }));
vi.mock('./tenantChat', async importOriginal => ({
  ...await importOriginal<typeof import('./tenantChat')>(),
  chatTenantId: '11111111-1111-1111-1111-111111111111',
  tenantTextWitness: tenant.witness
}));
import { emptySession, useSessionStore } from '../state/sessionStore';
import { realtimeTestHooks, sendDraft } from './realtime';
const hooks = realtimeTestHooks!;
beforeEach(() => {
  tenant.witness.mockReset(); hooks.resetOutput();
  useSessionStore.setState({ ...emptySession(), connection: 'ready', sessionId: 'session-one', attachmentId: 'attachment-one', status: 'attached', draft: 'Prepared text', agents: [] });
});
afterEach(() => { hooks.resetOutput(); hooks.setConnection(null); });
it('does not send a prepared protected command across an attachment change', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  let resolve!: (value: unknown) => void;
  tenant.witness.mockImplementation(() => new Promise(done => { resolve = done; }));
  const sending = sendDraft(); await vi.waitFor(() => expect(tenant.witness).toHaveBeenCalled());
  useSessionStore.setState({ attachmentId: 'attachment-two' }); resolve({ expectedRevision: 3, expectedSessionRevision: 4 });
  await sending; expect(invoke).not.toHaveBeenCalled();
  expect(useSessionStore.getState().draft).toBe('Prepared text');
});
it('does not restore an old rejected send into a different conversation', async () => {
  tenant.witness.mockResolvedValue({ expectedRevision: 3, expectedSessionRevision: 4 });
  let resolve!: (value: unknown) => void;
  const invoke = vi.fn(() => new Promise(done => { resolve = done; })); hooks.setConnection({ invoke } as never);
  const sending = sendDraft(); await vi.waitFor(() => expect(invoke).toHaveBeenCalled());
  useSessionStore.setState({ sessionId: 'session-two', attachmentId: 'attachment-two', entries: [], draft: 'New conversation draft' });
  resolve({ accepted: false, error: { category: 'Session', code: 'Conflict', message: 'Not admitted' } }); await sending;
  expect(useSessionStore.getState().draft).toBe('New conversation draft'); expect(useSessionStore.getState().entries).toEqual([]);
});
