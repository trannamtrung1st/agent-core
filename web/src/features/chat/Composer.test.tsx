import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { attachLongComposerText, queueComposerFiles, removeComposerFile, retryComposerFile, steerQueuedSend } from "../../services/realtime";
import type { PendingSendItem } from "../../state/sessionStore";
import { Composer } from "./Composer";

vi.mock("../../services/realtime", () => ({
  attachLongComposerText: vi.fn().mockResolvedValue(undefined),
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
    captureNeedsResume: false,
    onMute: vi.fn(),
    onResumeCapture: vi.fn(),
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

  it.each([{ metaKey: true }, { ctrlKey: true }])("steers immediately with the primary modifier %j", (modifier) => {
    const onSend = vi.fn();
    render(<Composer {...emptyComposerProps()} canSend canStop sendLabel="Queue" onSend={onSend} draft="Change direction" />);
    fireEvent.keyDown(screen.getByLabelText("Message"), { key: "Enter", ...modifier });
    expect(onSend).toHaveBeenCalledExactlyOnceWith("interrupt");
    fireEvent.keyDown(screen.getByLabelText("Message"), { key: "Enter", shiftKey: true, ...modifier });
    expect(onSend).toHaveBeenCalledTimes(1);
  });

  it("does not submit composing, repeated, or disabled input", () => {
    const onSend = vi.fn();
    const props = { ...emptyComposerProps(), onSend, draft: "Draft" };
    const { rerender } = render(<Composer {...props} canSend />);
    const field = screen.getByLabelText("Message");
    fireEvent.keyDown(field, { key: "Enter", metaKey: true, isComposing: true });
    fireEvent.keyDown(field, { key: "Enter", ctrlKey: true, keyCode: 229 });
    fireEvent.keyDown(field, { key: "Enter", metaKey: true, repeat: true });
    rerender(<Composer {...props} canSend={false} />);
    fireEvent.keyDown(field, { key: "Enter", ctrlKey: true });
    rerender(<Composer {...props} canSend ready={false} />);
    fireEvent.keyDown(field, { key: "Enter", metaKey: true });
    expect(onSend).not.toHaveBeenCalled();
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
    expect(screen.getByRole("button", { name: "Add content" })).toBeDisabled();
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
        canStop pendingSendQueue={[queueItem(1, "A"), queueItem(2, "B")]}
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

  it("offers Resume microphone without turning voice off", () => {
    const onResumeCapture = vi.fn();
    const onCancelVoice = vi.fn();
    render(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        captureNeedsResume
        onResumeCapture={onResumeCapture}
        onCancelVoice={onCancelVoice}
      />
    );
    expect(screen.getByRole("button", { name: /^Voice$/ })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: "Resume microphone" }));
    expect(onResumeCapture).toHaveBeenCalled();
    expect(onCancelVoice).not.toHaveBeenCalled();
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
    const { rerender } = render(
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
    expect(mute).not.toHaveClass("composer-voice-live");
    expect(mute.querySelector(".anticon-audio-muted")).not.toBeNull();
    fireEvent.click(mute);
    expect(onVoice).not.toHaveBeenCalled();
    expect(onMute).not.toHaveBeenCalled();
    rerender(
      <Composer
        {...emptyComposerProps()}
        voiceAvailable
        voiceModeActive
        voiceInputLive
        muted={false}
        onVoice={onVoice}
        onMute={onMute}
      />
    );
    const resumedMute = screen.getByRole("button", { name: "Mute" });
    expect(resumedMute).toBeEnabled();
    expect(resumedMute).toHaveClass("composer-voice-live");
    expect(resumedMute.querySelector(".anticon-audio")).not.toBeNull();
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
    expect(unmute.querySelector(".anticon-audio-muted")).not.toBeNull();
    fireEvent.click(unmute);
    expect(onMute).not.toHaveBeenCalled();
  });
});

 it('offers only file attachment content in the plain tenant composer and accepts pasted files', async () => {
    vi.clearAllMocks();
    render(<Composer {...emptyComposerProps()} fileAttachmentsEnabled />);
    fireEvent.click(screen.getByRole('button', { name: 'Add content' }));
    expect(await screen.findByText('Attach file')).toBeInTheDocument();
    expect(screen.queryByText('Use Skill')).not.toBeInTheDocument(); expect(screen.queryByText('Reference resource')).not.toBeInTheDocument();
    const file = new File(['plain'], 'notes.txt', { type: 'text/plain' });
    fireEvent.paste(screen.getByRole('textbox', { name: 'Message' }), { clipboardData: { files: [file] } });
    expect(queueComposerFiles).toHaveBeenCalledWith([file]); expect(queueComposerFiles).toHaveBeenCalledTimes(1);
 });

it('offers tenant Steer only with current cancellation authority and keeps Ctrl+Enter explicit', () => {
  const onSend = vi.fn();
  const view = render(<Composer {...emptyComposerProps()} canSend draft="New objective" canStop={false} onSend={onSend} pendingSendQueue={[queueItem(1, 'Queued objective')]} />);
  expect(screen.getByRole('button', { name: 'Steer queued message 1' })).toBeDisabled();
  fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message' }), { key: 'Enter', ctrlKey: true }); expect(onSend).not.toHaveBeenCalled();
  view.rerender(<Composer {...emptyComposerProps()} canSend draft="New objective" canStop onSend={onSend} pendingSendQueue={[queueItem(1, 'Queued objective')]} />);
  expect(screen.getByRole('button', { name: 'Steer queued message 1' })).toBeEnabled();
  fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message' }), { key: 'Enter', ctrlKey: true }); expect(onSend).toHaveBeenLastCalledWith('interrupt');
  fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message' }), { key: 'Enter' }); expect(onSend).toHaveBeenLastCalledWith();
});

it('fences Send and form submission until the first selected file has finished conversation intake', async () => {
  let complete!: () => void;
  vi.mocked(queueComposerFiles).mockReturnValueOnce(new Promise(resolve => { complete = resolve; }));
  const onSend = vi.fn();
  render(<Composer {...emptyComposerProps()} fileAttachmentsEnabled canSend draft="Keep my original" onSend={onSend} />);
  const form = document.querySelector('form.composer')!;
  fireEvent.drop(form, { dataTransfer: { files: asFileList([new File(['original'], 'proof.txt', { type: 'text/plain' })]) } });
  expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  fireEvent.submit(form); fireEvent.keyDown(screen.getByRole('textbox', { name: 'Message' }), { key: 'Enter' });
  expect(onSend).not.toHaveBeenCalled();
  await act(async () => { complete(); });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
  expect(onSend).toHaveBeenCalledTimes(1);
});

it('retains an oversized plain draft and offers only deliberate attachment conversion', async () => {
  const text = 'literal @text /text 😀\n'.repeat(500);
  const onSend = vi.fn();
  render(<Composer {...emptyComposerProps()} draft={text} canSend onSend={onSend} />);
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Message' })).toHaveTextContent('literal @text /text'));
  expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  expect(attachLongComposerText).not.toHaveBeenCalled();
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Attach long text' })); });
  expect(attachLongComposerText).toHaveBeenCalledTimes(1); expect(onSend).not.toHaveBeenCalled();
});

it('keeps mixed oversized typed chips and guides the user to attach only literal text', () => {
  render(<Composer {...emptyComposerProps()} draft={'x'.repeat(8_001)} parts={[{ kind: 'text', text: 'x'.repeat(8_001) }, { kind: 'invocation', invocationKind: 'skill', skillKey: 'definition:review', label: 'Review' }]} canSend />);
  expect(screen.queryByRole('button', { name: 'Attach long text' })).toBeNull();
  expect(screen.getByText(/Attach the large literal text separately/)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
});
