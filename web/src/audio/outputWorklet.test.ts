// @ts-nocheck
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { describe, expect, it } from "vitest";

function loadProcessor(): new () => {
  port: { onmessage: ((event: { data: unknown }) => void) | null; postMessage: (data: unknown) => void; messages: unknown[] };
  process: (inputs: unknown, outputs: Float32Array[][]) => boolean;
} {
  const code = fs.readFileSync(path.resolve(process.cwd(), "public/worklets/output-processor.js"), "utf8");
  const context: vm.Context = {
    sampleRate: 24000,
    AudioWorkletProcessor: class {
      port = {
        onmessage: null as ((event: { data: unknown }) => void) | null,
        messages: [] as unknown[],
        postMessage(data: unknown) {
          this.messages.push(data);
        }
      };
    },
    registerProcessor() {
      /* worklet registration is a no-op in unit tests */
    },
    Float32Array,
    globalThis: {} as { AgentCoreOutputProcessor?: unknown }
  };
  context.globalThis = context;
  vm.createContext(context);
  vm.runInContext(code, context);
  return context.globalThis.AgentCoreOutputProcessor as new () => {
    port: { onmessage: ((event: { data: unknown }) => void) | null; postMessage: (data: unknown) => void; messages: unknown[] };
    process: (inputs: unknown, outputs: Float32Array[][]) => boolean;
  };
}

function channel(processor: { process: (inputs: unknown, outputs: Float32Array[][]) => boolean }, frames = 128): Float32Array {
  const output = new Float32Array(frames);
  processor.process([], [[output]]);
  return output;
}

describe("output worklet response lifecycle", () => {
  it("rejects a second response until the first drains or is flushed", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(256).fill(0.4) } });
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r2", pcm: new Float32Array(128).fill(0.9) } });
    channel(processor, 128);
    const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
      responseId?: string;
      queued?: number;
    };
    expect(snapshot.responseId).toBe("r1");
    expect(snapshot.queued).toBe(128);
  });

  it("closes after the final sample is consumed and then admits a new response with reset counters", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({
      data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(64).fill(0.3), isFinal: true }
    });
    channel(processor, 128);
    const complete = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as {
      responseId?: string;
      consumed?: number;
    };
    expect(complete.responseId).toBe("r1");
    expect(complete.consumed).toBe(64);
    processor.port.messages.length = 0;
    processor.port.onmessage?.({
      data: { type: "enqueue", responseId: "r2", pcm: new Float32Array(128).fill(0.2), isFinal: true }
    });
    channel(processor, 128);
    const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
      consumed?: number;
      responseId?: string | null;
      closed?: boolean;
    };
    expect(snapshot.consumed).toBe(128);
    const complete2 = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as {
      responseId?: string;
    };
    expect(complete2.responseId).toBe("r2");
  });

  it("flush resets consumed so later acknowledgements stay in the new response range", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(256).fill(0.4) } });
    channel(processor, 128);
    processor.port.onmessage?.({ data: { type: "flush", responseId: "r1" } });
    const flushed = processor.port.messages.find((item) => (item as { type?: string }).type === "flushed") as {
      consumed?: number;
      epoch?: number;
    };
    expect(flushed.consumed).toBe(0);
    expect(flushed.epoch).toBe(1);
    processor.port.messages.length = 0;
    processor.port.onmessage?.({
      data: { type: "enqueue", responseId: "r2", pcm: new Float32Array(128).fill(0.1), isFinal: true }
    });
    channel(processor, 128);
    const complete = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as {
      consumed?: number;
      responseId?: string;
    };
    expect(complete.responseId).toBe("r2");
    expect(complete.consumed).toBe(128);
  });
});
