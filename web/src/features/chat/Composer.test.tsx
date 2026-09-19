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
    voiceInputHeldForAgentOutput: false,
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

  it("renders model controls in the composer toolbar", () => {
    render(
      <Composer
        {...emptyComposerProps()}
        modelControls={<span>Model control</span>}
      />
    );
    expect(screen.getByText("Model control").closest("form.composer")).not.toBeNull();
    expect(screen.getByText("Model control").closest(".composer-toolbar-start")).not.toBeNull();
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

  it("keeps Queue available and shows Stop without labeling Send as Interrupt", () => {
    const onStop = vi.fn();
    render(
      <Composer {...emptyComposerProps()} canSend canStop sendLabel="Queue" onStop={onStop} draft="Next" />
    );
    expect(screen.getByRole("button", { name: "Queue" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: /interrupt/i })).not.toBeInTheDocument();
    const stop = screen.getByRole("button", { name: "Stop" });
    expect(stop).toBeEnabled();
    stop.focus();
    expect(stop).toHaveFocus();
    fireEvent.click(stop);
    expect(onStop).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText("Message")).toHaveFocus();
  });

  it("keeps compact Stop beside Queue when a response is active and the draft has content", () => {
    render(
      <Composer {...emptyComposerProps()} canSend canStop sendLabel="Queue" draft="Next" />
    );
    expect(screen.getByRole("button", { name: "Queue" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
  });

  it("uses Stop as the primary action when a response is active and the draft is empty", () => {
    render(<Composer {...emptyComposerProps()} canStop sendLabel="Queue" />);
    expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Send" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Queue" })).not.toBeInTheDocument();
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

  it("shows Unmute without listening styling when muted in voice mode", () => {
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive
        muted
        onMute={onMute}
      />
    );
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
    const unmute = screen.getByRole("button", { name: "Unmute" });
    expect(unmute).not.toHaveClass("composer-voice-live");
    fireEvent.click(unmute);
    expect(onMute).toHaveBeenCalledWith(false);
  });

  it("shows inactive microphone state when capture is not live and user is not muted", () => {
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive={false}
        muted={false}
        onMute={onMute}
      />
    );
    expect(screen.getByRole("button", { name: "Voice input inactive" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Unmute" })).not.toBeInTheDocument();
    expect(onMute).not.toHaveBeenCalled();
  });

  it("keeps Voice mode separate from Mute while listening", () => {
    const onMute = vi.fn();
    const onCancelVoice = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive
        muted={false}
        onMute={onMute}
        onCancelVoice={onCancelVoice}
      />
    );
    const voice = screen.getByRole("button", { name: /^Voice$/ });
    expect(voice).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(voice);
    expect(onCancelVoice).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Mute" }));
    expect(onMute).toHaveBeenCalledWith(true);
    expect(screen.getByRole("button", { name: "Mute" })).toHaveClass("composer-voice-live");
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
    expect(screen.getByRole("button", { name: /^Voice$/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry voice input" }));
    expect(onVoice).toHaveBeenCalledTimes(1);
  });

  it("disables the microphone while browser STT is held for agent output", () => {
    const onVoice = vi.fn();
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive={false}
        voiceInputHeldForAgentOutput
        muted={false}
        onVoice={onVoice}
        onMute={onMute}
      />
    );
    expect(screen.getByRole("button", { name: /^Voice$/ })).toBeEnabled();
    const mute = screen.getByRole("button", { name: "Mute" });
    expect(mute).toBeDisabled();
    expect(mute).toHaveClass("composer-voice-live");
    fireEvent.click(mute);
    expect(onVoice).not.toHaveBeenCalled();
    expect(onMute).not.toHaveBeenCalled();
  });

  it("keeps Unmute appearance while held for agent output", () => {
    const onMute = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputHeldForAgentOutput
        muted
        onMute={onMute}
      />
    );
    const unmute = screen.getByRole("button", { name: "Unmute" });
    expect(unmute).toBeDisabled();
    expect(unmute).not.toHaveClass("composer-voice-live");
    fireEvent.click(unmute);
    expect(onMute).not.toHaveBeenCalled();
  });
});
