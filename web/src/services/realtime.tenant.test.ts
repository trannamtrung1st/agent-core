import { afterEach, beforeEach, expect, it, vi } from 'vitest';
const tenant = vi.hoisted(() => ({ witness: vi.fn(), interruption: vi.fn() }));
vi.mock('./tenantChat', async importOriginal => ({
  ...await importOriginal<typeof import('./tenantChat')>(),
  chatTenantId: '11111111-1111-1111-1111-111111111111',
  tenantTextWitness: tenant.witness,
  tenantInterruptWitness: tenant.interruption
}));
import { emptySession, useSessionStore } from '../state/sessionStore';
import { realtimeTestHooks, sendDraft, reportCommittedEntries, steerQueuedSend } from './realtime';
const hooks = realtimeTestHooks!;
beforeEach(() => {
  tenant.witness.mockReset(); tenant.interruption.mockReset(); hooks.resetOutput();
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

it('acknowledges only the bounded rendered block list with the current tenant attachment', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  reportCommittedEntries([{ role: 'assistant', responseId: 'response-one', text: 'Published.', status: 'completed', blocks: [{ blockId: 'b1' }] }]);
  await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith('ResponseReceived', expect.objectContaining({
    attachmentId: 'attachment-one', responseId: 'response-one', payload: { textEndExclusive: 10, blockIds: ['b1'] }
  })));
});

it('sends file-only tenant input with exact selected IDs and fresh witnesses while omitting legacy behavior and rich parts', async () => {
  tenant.witness.mockResolvedValue({ expectedRevision: 5, expectedSessionRevision: 8 });
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  useSessionStore.setState({ draft: '', pendingAttachments: [{ localId: 'local', attachmentId: 'file-one', displayName: 'notes.txt', contentType: 'text/plain', byteSize: 4, status: 'ready', progress: 100, error: null }] });
  await sendDraft();
  expect(invoke).toHaveBeenCalledWith('SendText', expect.objectContaining({ payload: { text: '', attachmentIds: ['file-one'], expectedRevision: 5, expectedSessionRevision: 8 } }));
  expect(useSessionStore.getState().pendingAttachments).toEqual([]);
});

 it('keeps the conversation, draft and selected files after child file access is refused', async () => {
   tenant.witness.mockResolvedValue({ expectedRevision: 5, expectedSessionRevision: 8 });
   const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { category: 'Authorization', code: 'attachment_unavailable', message: 'Remove the unavailable file.' } }); hooks.setConnection({ invoke } as never);
   const file = { localId: 'local', attachmentId: 'file-one', displayName: 'notes.txt', contentType: 'text/plain', byteSize: 4, status: 'ready' as const, progress: 100, error: null };
   useSessionStore.setState({ draft: 'Keep this draft', pendingAttachments: [file] }); await sendDraft();
   expect(useSessionStore.getState()).toMatchObject({ sessionId: 'session-one', connection: 'ready', draft: 'Keep this draft', pendingAttachments: [file] });
 });

it('steers with the captured current Run and exact fresh cancellation witnesses', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  useSessionStore.setState({ agentRunId: 'run-one', liveResponseId: 'response-one' });
  tenant.interruption.mockResolvedValue({ expectedRevision: 7, expectedSessionRevision: 9, interruptedRunId: 'run-one', expectedRunRevision: 3 });
  await sendDraft('interrupt');
  expect(tenant.interruption).toHaveBeenCalledWith('session-one', 'run-one');
  expect(invoke).toHaveBeenCalledWith('SendText', expect.objectContaining({ payload: {
    text: 'Prepared text', behavior: 'interrupt', interruptedRunId: 'run-one', expectedRunRevision: 3, expectedRevision: 7, expectedSessionRevision: 9
  } }));
});

it('refuses a prepared steer if a different Run becomes current before dispatch', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  useSessionStore.setState({ agentRunId: 'run-one', liveResponseId: 'response-one' });
  let resolve!: (value: unknown) => void; tenant.interruption.mockImplementation(() => new Promise(done => { resolve = done; }));
  const pending = sendDraft('interrupt'); await vi.waitFor(() => expect(tenant.interruption).toHaveBeenCalled());
  useSessionStore.setState({ agentRunId: 'run-two', liveResponseId: 'response-two' });
  resolve({ expectedRevision: 7, expectedSessionRevision: 9, interruptedRunId: 'run-one', expectedRunRevision: 3 }); await pending;
  expect(invoke).not.toHaveBeenCalled(); expect(useSessionStore.getState().draft).toBe('Prepared text');
  expect(useSessionStore.getState().connection).toBe('ready');
});

it('keeps the queued draft and files when independent interruption permission is refused', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: false, error: { category: 'Authorization', code: 'interruption_unavailable', message: 'This interruption is unavailable.' } });
  hooks.setConnection({ invoke } as never); useSessionStore.setState({ agentRunId: 'run-one', liveResponseId: 'response-one', draft: '' });
  tenant.interruption.mockResolvedValue({ expectedRevision: 7, expectedSessionRevision: 9, interruptedRunId: 'run-one', expectedRunRevision: 3 });
  const item = { localId: 'queued-one', eventId: 'first-event', text: 'A different objective', attachmentIds: [], attachments: [], createdAt: 1, dispatching: false, error: null };
  useSessionStore.setState({ pendingSendQueue: [item] }); await steerQueuedSend(item.localId);
  expect(useSessionStore.getState()).toMatchObject({ sessionId: 'session-one', connection: 'ready', pendingSendQueue: [{ text: item.text, dispatching: false, error: 'This interruption is unavailable.' }] });
  expect(useSessionStore.getState().pendingSendQueue[0].eventId).not.toBe(item.eventId);
});


it('binds client transcript evidence to the captured tenant input stream before a queued send', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  useSessionStore.setState({ mode: 'voice', sttTransport: 'clientTranscript', streamId: 'stream-one' });
  hooks.sendSpeechEvidence({ kind: 'final', utteranceId: 'utterance-one', text: 'A spoken objective', confidence: 0.9 });
  useSessionStore.setState({ streamId: 'stream-two' });
  await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith('SpeechEvidence', expect.objectContaining({
    payload: { streamId: 'stream-one', kind: 'final', utteranceId: 'utterance-one', text: 'A spoken objective', confidence: 0.9 }
  })));
});

it('reports speech coordinates independently of longer rendered display text', async () => {
  const invoke = vi.fn().mockResolvedValue({ accepted: true }); hooks.setConnection({ invoke } as never);
  useSessionStore.setState({ entries: [{ entryId: 'entry-one', sequence: 1, role: 'assistant', responseId: 'response-one',
    sourceEventId: null, deliveryMode: 'voice', createdAt: '2026-10-21T00:00:00Z', text: 'A much longer visual explanation and a table.', speechText: 'A brief reply.', status: 'streaming', heardTextEndExclusive: 0, receivedTextEndExclusive: 0 }] });
  await hooks.sendPlayback('PlaybackProgress', 'playback.progress', 'response-one', 480);
  expect(invoke).toHaveBeenCalledWith('PlaybackProgress', expect.objectContaining({ payload: { consumedSamples: 480, textEndExclusive: 14 } }));
});
