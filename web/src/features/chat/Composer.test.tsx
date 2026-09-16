import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queueComposerFiles } from "../../services/realtime";
import { Composer } from "./Composer";

vi.mock("../../services/realtime", () => ({
  queueComposerFiles: vi.fn().mockResolvedValue(undefined),
  removeComposerFile: vi.fn(),
  retryComposerFile: vi.fn()
}));

function emptyComposerProps() {
  return {
    draft: "",
    canSend: false,
    ready: true,
    error: null,
    pendingAttachments: [],
    voiceAvailable: false,
    pendingVoice: false,
    voiceLive: false,
    muted: false,
    canRetry: false,
    onDraftChange: vi.fn(),
    onSend: vi.fn(),
    onVoice: vi.fn(),
    onCancelVoice: vi.fn(),
    onMute: vi.fn(),
    onRetry: vi.fn(),
    onEnd: vi.fn()
  };
}

function asFileList(files: File[]): FileList {
  return {
    length: files.length,
    item: (index: number) => files[index] ?? null,
    [Symbol.iterator]: function* () {
      yield* files;
    },
    ...Object.fromEntries(files.map((file, index) => [index, file]))
  } as unknown as FileList;
}

describe("Composer attachment staging", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("stages dropped files through the same pending-attachment queue as the picker", () => {
    const file = new File(["hello"], "notes.txt", { type: "text/plain" });
    render(<Composer {...emptyComposerProps()} />);
    const form = document.querySelector("form.composer");
    expect(form).not.toBeNull();
    fireEvent.drop(form!, {
      dataTransfer: { files: asFileList([file]) }
    });
    expect(queueComposerFiles).toHaveBeenCalledTimes(1);
    const staged = vi.mocked(queueComposerFiles).mock.calls[0][0];
    expect(staged).toHaveLength(1);
    expect(staged[0].name).toBe("notes.txt");
  });

  it("stages pasted images through the same pending-attachment queue as the picker", () => {
    const image = new File([new Uint8Array([137, 80, 78, 71])], "clip.png", { type: "image/png" });
    render(<Composer {...emptyComposerProps()} />);
    fireEvent.paste(screen.getByPlaceholderText("Type your message here..."), {
      clipboardData: { files: asFileList([image]) }
    });
    expect(queueComposerFiles).toHaveBeenCalledTimes(1);
    const staged = vi.mocked(queueComposerFiles).mock.calls[0][0];
    expect(staged).toHaveLength(1);
    expect(staged[0].name).toBe("clip.png");
    expect(staged[0].type).toBe("image/png");
  });
});
