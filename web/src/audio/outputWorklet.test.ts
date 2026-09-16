// @ts-nocheck
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { describe, expect, it } from "vitest";

function loadProcessor(sampleRate = 24000): new () => {
  port: { onmessage: ((event: { data: unknown }) => void) | null; postMessage: (data: unknown) => void; messages: unknown[] };
  process: (inputs: unknown, outputs: Float32Array[][]) => boolean;
} {
  const code = fs.readFileSync(path.resolve(process.cwd(), "public/worklets/output-processor.js"), "utf8");
  const context: vm.Context = {
    sampleRate,
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

  it("resolves overlapping flush requests by response id and epoch", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(128).fill(0.4) } });
    channel(processor, 128);
    processor.port.messages.length = 0;
    processor.port.onmessage?.({ data: { type: "flush", responseId: "r1", epoch: 0 } });
    processor.port.onmessage?.({ data: { type: "flush", responseId: "r2", epoch: 0 } });
    channel(processor, 128);
    const flushed = processor.port.messages.filter((item) => (item as { type?: string }).type === "flushed") as Array<{
      responseId?: string;
      consumed?: number;
      epoch?: number;
    }>;
    expect(flushed).toHaveLength(1);
    expect(flushed[0].responseId).toBe("r1");
    expect(flushed[0].consumed).toBe(128);
    expect(flushed[0].epoch).toBe(0);
    processor.port.messages.length = 0;
    channel(processor, 128);
    const second = processor.port.messages.filter((item) => (item as { type?: string }).type === "flushed") as Array<{
      responseId?: string;
    }>;
    expect(second).toHaveLength(1);
    expect(second[0].responseId).toBe("r2");
    processor.port.messages.length = 0;
    processor.port.onmessage?.({
      data: { type: "enqueue", responseId: "r3", pcm: new Float32Array(128).fill(0.1), isFinal: true }
    });
    channel(processor, 128);
    const complete = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as {
      responseId?: string;
    };
    expect(complete.responseId).toBe("r3");
  });

  it("flush reports consumed captured before reset", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(128).fill(0.4) } });
    channel(processor, 128);
    processor.port.messages.length = 0;
    processor.port.onmessage?.({ data: { type: "flush", responseId: "r1" } });
    expect(processor.port.messages.some((item) => (item as { type?: string }).type === "flushed")).toBe(false);
    channel(processor, 128);
    const flushed = processor.port.messages.find((item) => (item as { type?: string }).type === "flushed") as {
      consumed?: number;
      epoch?: number;
    };
    expect(flushed.consumed).toBe(128);
    expect(flushed.epoch).toBe(0);
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

  it("includes a render quantum between flush request and acknowledgement in consumed", () => {
    const Processor = loadProcessor();
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(256).fill(0.4) } });
    channel(processor, 128);
    processor.port.messages.length = 0;
    processor.port.onmessage?.({ data: { type: "flush", responseId: "r1" } });
    expect(processor.port.messages.some((item) => (item as { type?: string }).type === "flushed")).toBe(false);
    channel(processor, 128);
    const flushed = processor.port.messages.find((item) => (item as { type?: string }).type === "flushed") as {
      consumed?: number;
    };
    expect(flushed.consumed).toBe(256);
  });

    it("rejects a second overflowing frame without advancing consumed past queued audio", () => {
      const Processor = loadProcessor();
      const processor = new Processor();
      const max = 24000 * 2;
      processor.port.onmessage?.({
        data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(max - 128).fill(0.2) }
      });
      processor.port.messages.length = 0;
      processor.port.onmessage?.({
        data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(256).fill(0.4) }
      });
      const overflow = processor.port.messages.find((item) => (item as { type?: string }).type === "overflow") as {
        queued?: number;
      };
      expect(overflow.queued).toBe(max - 128);
      channel(processor, 128);
      const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
        consumed?: number;
        queued?: number;
      };
      expect(snapshot.queued).toBe(max - 256);
      expect(snapshot.consumed).toBe(128);
      expect(processor.port.messages.some((item) => (item as { type?: string }).type === "complete")).toBe(false);
    });

    it("reports canonical consumed samples when rendering at 48 kHz", () => {
    const Processor = loadProcessor(48000);
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(240).fill(0.2) } });
    channel(processor, 128);
    const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
      consumed?: number;
    };
    expect(snapshot.consumed).toBe(64);
  });

  it("keeps canonical continuity across chunks at 44.1 kHz", () => {
    const Processor = loadProcessor(44100);
    const processor = new Processor();
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(480).fill(0.2) } });
    channel(processor, 128);
    processor.port.onmessage?.({ data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(480).fill(0.2) } });
    channel(processor, 128);
    const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
      consumed?: number;
    };
    expect(snapshot.consumed).toBe(Math.floor((256 * 24000) / 44100));
  });

  it("consumes a final 480-sample frame exactly at 44.1 kHz and 48 kHz", () => {
    for (const rate of [44100, 48000]) {
      const Processor = loadProcessor(rate);
      const processor = new Processor();
      processor.port.onmessage?.({
        data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(480).fill(0.2), isFinal: true }
      });
      let consumed = 0;
      for (let attempt = 0; attempt < 16; attempt += 1) {
        channel(processor, 128);
        const snapshot = processor.port.messages.filter((item) => (item as { type?: string }).type === "snapshot").at(-1) as {
          consumed?: number;
        };
        consumed = snapshot.consumed ?? 0;
        if (processor.port.messages.some((item) => (item as { type?: string }).type === "complete")) {
          break;
        }
      }

      expect(consumed).toBe(480);
      const complete = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as {
        consumed?: number;
      };
      expect(complete.consumed).toBe(480);
    }
  });

  it("completes short final buffers at 44.1 kHz and 48 kHz including leftover resampler tails", () => {
    for (const rate of [44100, 48000]) {
      for (const length of [1, 65, 479, 481]) {
        const Processor = loadProcessor(rate);
        const processor = new Processor();
        processor.port.onmessage?.({
          data: { type: "enqueue", responseId: "r1", pcm: new Float32Array(length).fill(0.2), isFinal: true }
        });
        let complete: { type?: string; queued?: number; consumed?: number } | undefined;
        for (let attempt = 0; attempt < 64; attempt += 1) {
          channel(processor, 128);
          complete = processor.port.messages.find((item) => (item as { type?: string }).type === "complete") as
            | { type?: string; queued?: number; consumed?: number }
            | undefined;
          if (complete) {
            break;
          }
        }

        expect(complete, `complete missing for ${length} samples at ${rate} Hz`).toBeTruthy();
        expect(complete?.queued ?? 0).toBe(0);
        expect(complete?.consumed).toBe(length);
      }
    }
  });
});
