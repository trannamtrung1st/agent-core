import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { processPreview } from './previewWorker';
import { testPolicy } from './previewTestPolicy';
const workers: {terminate: ReturnType<typeof vi.fn>;postMessage: ReturnType<typeof vi.fn>;onmessage: ((event: {data: {value?: unknown}}) => void) | null;onerror: (() => void) | null}[] = [];
beforeEach(() => { workers.length=0; vi.useFakeTimers(); vi.stubGlobal('Worker', vi.fn(function () { const worker={terminate:vi.fn(),postMessage:vi.fn(),onmessage:null,onerror:null}; workers.push(worker); return worker; })); });
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
it('terminates stale processing on cancellation and passes the backend policy to the worker', async () => {
  const controller=new AbortController(); const pending=processPreview('csv','001,=CMD()',',',controller.signal,testPolicy);
  const rejected=expect(pending).rejects.toMatchObject({name:'AbortError'});
  expect(workers[0].postMessage).toHaveBeenCalledWith({kind:'csv',source:'001,=CMD()',language:',',policy:testPolicy});
  controller.abort(); await rejected; expect(workers[0].terminate).toHaveBeenCalledTimes(1);
});
it('uses the configured deadline and never leaves a timed-out worker alive', async () => {
  const pending=processPreview('csv','a,b',',',new AbortController().signal,{...testPolicy,workerTimeoutMs:100});
  const rejected=expect(pending).rejects.toThrow(/timed out/);
  await vi.advanceTimersByTimeAsync(100); await rejected; expect(workers[0].terminate).toHaveBeenCalledTimes(1);
});
