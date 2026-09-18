import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  queueComposerFiles,
  removeComposerFile,
  retryComposerFile,
  steerQueuedSend
} from "../../services/realtime";
import type { PendingSendItem } from "../../state/sessionStore";
import { Composer } from "./Composer";

vi.mock("../../services/realtime", () => ({
  queueComposerFiles: vi.fn().mockResolvedValue(undefined),
  removeComposerFile: vi.fn(),
  retryComposerFile: vi.fn(),
  composerSteerEnabled: vi.fn(() => true),
  steerQueuedSend: vi.fn(),
  removeQueuedSend: vi.fn()
}));

function emptyComposerProps() {
  return {
    draft: "",
    canSend: false,
    canStop: false,
    sendLabel: "Send",
    pendingSendQueue: [],
    ready: true,
    error: null,
    pendingAttachments: [],
    voiceAvailable: false,
    pendingVoice: false,
    voiceModeActive: false,
    voiceInputLive: false,
    voiceInputBlocked: false,
    muted: false,
    canRetry: false,
    placeholder: "Message Agent Core...",
    onDraftChange: vi.fn(),
    onSend: vi.fn(),
    onStop: vi.fn(),
    onVoice: vi.fn(),
    onCancelVoice: vi.fn(),
    onMute: vi.fn(),
    onRetry: vi.fn()
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
    fireEvent.paste(screen.getByLabelText("Message"), {
      clipboardData: { files: asFileList([image]) }
    });
    expect(queueComposerFiles).toHaveBeenCalledTimes(1);
    const staged = vi.mocked(queueComposerFiles).mock.calls[0][0];
    expect(staged).toHaveLength(1);
    expect(staged[0].name).toBe("clip.png");
    expect(staged[0].type).toBe("image/png");
  });

  it("sends on Enter and inserts a newline on Shift+Enter", () => {
    const onSend = vi.fn();
    render(<Composer {...emptyComposerProps()} canSend onSend={onSend} draft="Hello" />);
    const field = screen.getByLabelText("Message");
    fireEvent.keyDown(field, { key: "Enter" });
    expect(onSend).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(field, { key: "Enter", shiftKey: true });
    expect(onSend).toHaveBeenCalledTimes(1);
  });

  it("keeps send disabled until ready and surfaces upload errors on the existing queue", () => {
    render(
      <Composer
        {...emptyComposerProps()}
        ready={false}
        canSend={false}
        pendingAttachments={[
          {
            localId: "l1",
            displayName: "notes.txt",
            contentType: "text/plain",
            byteSize: 4,
            status: "error",
            progress: 0,
            attachmentId: null,
            error: "Upload failed"
          }
        ]}
      />
    );
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Attach" })).toBeDisabled();
    expect(screen.getByText("Upload failed")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry notes.txt" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove notes.txt" }));
    expect(retryComposerFile).toHaveBeenCalledWith("l1");
    expect(removeComposerFile).toHaveBeenCalledWith("l1");
  });

  it("keeps Send available and shows Stop without labeling Send as Interrupt", () => {
    const onStop = vi.fn();
    render(<Composer {...emptyComposerProps()} canSend canStop onStop={onStop} draft="Next" />);
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: /interrupt/i })).not.toBeInTheDocument();
    const stop = screen.getByRole("button", { name: "Stop" });
    expect(stop).toBeEnabled();
    stop.focus();
    expect(stop).toHaveFocus();
    fireEvent.click(stop);
    expect(onStop).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("Message")).toHaveFocus();
  });
});

function queueItem(index: number, text: string): PendingSendItem {
  return {
    localId: `q${index}`,
    eventId: `e${index}`,
    text,
    attachmentIds: [],
    attachments: []
  };
}

describe("Composer pending-send queue", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows two rows collapsed and expands to reveal the rest", () => {
    render(
      <Composer
        {...emptyComposerProps()}
        pendingSendQueue={[queueItem(1, "A"), queueItem(2, "B"), queueItem(3, "C")]}
      />
    );
    expect(screen.getByText("A")).toBeInTheDocument();
    expect(screen.getByText("B")).toBeInTheDocument();
    expect(screen.queryByText("C")).not.toBeInTheDocument();
    expect(screen.getByText("1 more queued")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Expand queued messages" }));
    expect(screen.getByText("C")).toBeInTheDocument();
  });

  it("uses a scrollable list when expanded with more than six items", () => {
    const queue = Array.from({ length: 7 }, (_, index) => queueItem(index + 1, `M${index + 1}`));
    render(<Composer {...emptyComposerProps()} pendingSendQueue={queue} />);
    fireEvent.click(screen.getByRole("button", { name: "Expand queued messages" }));
    expect(document.querySelector(".pending-send-queue-list-scroll")).not.toBeNull();
  });

  it("steers a non-head row through the row action", () => {
    render(
      <Composer
        {...emptyComposerProps()}
        pendingSendQueue={[queueItem(1, "A"), queueItem(2, "B")]}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Steer queued message 2" }));
    expect(steerQueuedSend).toHaveBeenCalledWith("q2");
  });
});

describe("Composer voice toolbar", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("shows Unmute when voice mode is active but capture is not live", () => {
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive={false}
        muted
        onMute={onMute}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Unmute" }));
    expect(onMute).toHaveBeenCalledWith(false);
    expect(screen.queryByRole("button", { name: "Voice" })).not.toBeInTheDocument();
  });

  it("shows Mute when voice input is live and not blocked", () => {
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive
        muted={false}
        onMute={onMute}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Mute" }));
    expect(onMute).toHaveBeenCalledWith(true);
  });

  it("shows Retry voice input instead of Mute when browser STT is blocked", () => {
    const onVoice = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive
        voiceInputBlocked
        muted={false}
        onVoice={onVoice}
      />
    );
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry voice input" }));
    expect(onVoice).toHaveBeenCalledTimes(1);
  });
});
