import { useRef, useState, type DragEvent, type ReactNode } from "react";
import { Button, Flex, Dropdown, Tooltip, Alert, theme } from "antd";
import {
  AudioOutlined,
  AudioFilled,
  AudioMutedOutlined,
  DeleteOutlined,
  DownOutlined,
  PaperClipOutlined,
  PlusOutlined,
  PhoneFilled,
  PhoneOutlined,
  RightOutlined,
  SendOutlined,
  StopOutlined,
  UnorderedListOutlined
} from "@ant-design/icons";
import { StructuredMessageEditor, type PickerRequest } from "./StructuredMessageEditor";
import { displayPart, type MessagePart } from "../../services/messageParts";
import {
  composerSteerEnabled,
  queueComposerFiles,
  removeComposerFile,
  retryComposerFile,
  removeQueuedSend,
  steerQueuedSend
} from "../../services/realtime";
import type { PendingSendItem } from "../../state/sessionStore";
import type { PendingAttachment } from "../../services/attachments";
import { PendingAttachmentView } from "./AttachmentPreview";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";

const QUEUE_COLLAPSED_VISIBLE = 2;
const QUEUE_EXPANDED_MAX = 6;

function queuePreview(item: PendingSendItem): string {
  if (item.text.trim().length > 0) {
    return item.text.trim();
  }

  if (item.attachments.length > 0) {
    return item.attachments.map((file) => file.displayName).join(", ");
  }

  return "Empty message";
}

function VoiceModeControl({
  pendingVoice,
  voiceModeActive,
  onVoice,
  onCancelVoice
}: {
  pendingVoice: boolean;
  voiceModeActive: boolean;
  onVoice: () => void;
  onCancelVoice: () => void;
}) {
  if (pendingVoice) {
    return (
      <Button aria-label="Cancel voice" onClick={onCancelVoice}>
        Cancel
      </Button>
    );
  }

  return (
    <Tooltip title={voiceModeActive ? "Turn off voice" : "Voice"}>
      <Button
        type={voiceModeActive ? "primary" : "text"}
        className="composer-icon"
        aria-label="Voice"
        aria-pressed={voiceModeActive}
        icon={voiceModeActive ? <PhoneFilled /> : <PhoneOutlined />}
        onClick={voiceModeActive ? onCancelVoice : onVoice}
      />
    </Tooltip>
  );
}

function MicrophoneControl({
  voiceInputLive,
  voiceInputBlocked,
  voiceInputHeldForAgentOutput,
  captureNeedsResume,
  muted,
  onVoice,
  onMute,
  onResumeCapture
}: {
  voiceInputLive: boolean;
  voiceInputBlocked: boolean;
  voiceInputHeldForAgentOutput: boolean;
  captureNeedsResume: boolean;
  muted: boolean;
  onVoice: () => void;
  onMute: (muted: boolean) => void;
  onResumeCapture: () => void;
}) {
  if (captureNeedsResume) {
    return (
      <Tooltip title="Resume microphone">
        <Button
          type="text"
          className="composer-icon"
          aria-label="Resume microphone"
          icon={<AudioOutlined />}
          onClick={onResumeCapture}
        />
      </Tooltip>
    );
  }

  if (voiceInputBlocked) {
    return (
      <Tooltip title="Retry microphone">
        <Button
          type="text"
          className="composer-icon"
          aria-label="Retry voice input"
          icon={<AudioOutlined />}
          onClick={onVoice}
        />
      </Tooltip>
    );
  }

  if (voiceInputHeldForAgentOutput) {
    const heldTooltip = "Microphone resumes after the agent finishes speaking";
    if (muted) {
      return (
        <Tooltip title={heldTooltip}>
          <Button
            type="text"
            className="composer-icon"
            aria-label="Unmute"
            icon={<AudioMutedOutlined />}
            disabled
          />
        </Tooltip>
      );
    }
    return (
      <Tooltip title={heldTooltip}>
        <Button
          type="text"
          className="composer-icon"
          aria-label="Mute"
          icon={<AudioMutedOutlined />}
          disabled
        />
      </Tooltip>
    );
  }

  if (muted) {
    return (
      <Tooltip title="Unmute">
        <Button
          type="text"
          className="composer-icon"
          aria-label="Unmute"
          icon={<AudioMutedOutlined />}
          onClick={() => onMute(false)}
        />
      </Tooltip>
    );
  }

  if (voiceInputLive) {
    return (
      <Tooltip title="Listening — click to mute">
        <Button
          type="text"
          className="composer-icon composer-voice-live"
          aria-label="Mute"
          icon={<AudioFilled />}
          onClick={() => onMute(true)}
        />
      </Tooltip>
    );
  }

  return (
    <Tooltip title="Starting microphone…">
      <Button
        type="text"
        className="composer-icon"
        aria-label="Voice input inactive"
        icon={<AudioOutlined />}
        disabled
      />
    </Tooltip>
  );
}

export function Composer({
  draft, parts, instanceId, onPartsChange,
  canSend,
  canStop,
  sendLabel,
  pendingSendQueue,
  ready,
  error,
  sessionId = null,
  pendingAttachments,
  voiceAvailable,
  pendingVoice,
  voiceModeActive,
  voiceInputLive,
  voiceInputBlocked,
  voiceInputHeldForAgentOutput,
  captureNeedsResume,
  muted,
  canRetry,
  placeholder,
  onDraftChange,
  onSend,
  onStop,
  onVoice,
  onCancelVoice,
  onMute,
  onResumeCapture,
  onRetry,
  modelControls,
  imageIncompatibilityMessage
}: {
  draft: string;
  parts?: MessagePart[] | null;
  instanceId?: string | null;
  onPartsChange?: (parts: MessagePart[]) => void;
  canSend: boolean;
  canStop: boolean;
  sendLabel: string;
  pendingSendQueue: PendingSendItem[];
  ready: boolean;
  error: SessionErrorView | string | null;
  sessionId?: string | null;
  pendingAttachments: PendingAttachment[];
  voiceAvailable: boolean;
  pendingVoice: boolean;
  voiceModeActive: boolean;
  voiceInputLive: boolean;
  voiceInputBlocked: boolean;
  voiceInputHeldForAgentOutput: boolean;
  captureNeedsResume: boolean;
  muted: boolean;
  canRetry: boolean;
  placeholder: string;
  onDraftChange: (value: string) => void;
  onSend: (behavior?: "interrupt") => void;
  onStop: () => void;
  onVoice: () => void;
  onCancelVoice: () => void;
  onMute: (muted: boolean) => void;
  onResumeCapture: () => void;
  onRetry: () => void;
  modelControls?: ReactNode;
  imageIncompatibilityMessage?: string | null;
}) {
  const { token } = theme.useToken();
  const fileInput = useRef<HTMLInputElement>(null);
  const [pickerRequest, setPickerRequest] = useState<PickerRequest>(null);
  const [editorValid, setEditorValid] = useState(true);
  const [queueExpanded, setQueueExpanded] = useState(false);

  function focusMessage() {
    document.querySelector<HTMLElement>('[aria-label="Message"]')?.focus();
  }

  function takeFiles(list: FileList | File[] | null) {
    if (!list || !ready) {
      return;
    }

    const files = Array.from(list);
    if (files.length === 0) {
      return;
    }

    void queueComposerFiles(files);
  }

  function onDrop(event: DragEvent<HTMLFormElement>) {
    event.preventDefault();
    takeFiles(event.dataTransfer.files);
  }

  const queueNeedsScroll = pendingSendQueue.length > QUEUE_EXPANDED_MAX;
  const visibleQueue = queueExpanded
    ? pendingSendQueue
    : pendingSendQueue.slice(0, QUEUE_COLLAPSED_VISIBLE);
  const hiddenCount = Math.max(0, pendingSendQueue.length - visibleQueue.length);

  return (
    <Flex vertical gap={token.paddingXS} className="dock">
      {error ? <SessionFailureAlert error={error} sessionId={sessionId} /> : null}
      {imageIncompatibilityMessage ? (
        <Alert type="warning" showIcon message={imageIncompatibilityMessage} />
      ) : null}

      {pendingSendQueue.length > 0 ? (
        <section className="pending-send-queue" aria-label="Queued messages">
          <Flex justify="space-between" align="center" className="pending-send-queue-header">
            {pendingSendQueue.length > QUEUE_COLLAPSED_VISIBLE ? (
              <button
                type="button"
                className="pending-send-queue-toggle"
                aria-expanded={queueExpanded}
                aria-label={queueExpanded ? "Collapse queued messages" : "Expand queued messages"}
                onClick={() => setQueueExpanded((value) => !value)}
              >
                <span>Queued · {pendingSendQueue.length}</span>
                {queueExpanded ? (
                  <DownOutlined aria-hidden="true" />
                ) : (
                  <RightOutlined aria-hidden="true" />
                )}
              </button>
            ) : (
              <span className="pending-send-queue-label">Queued · {pendingSendQueue.length}</span>
            )}
          </Flex>
          <ul
            className={`pending-send-queue-list${queueNeedsScroll && queueExpanded ? " pending-send-queue-list-scroll" : ""}`}
          >
            {visibleQueue.map((item) => {
              const position = pendingSendQueue.findIndex((queued) => queued.localId === item.localId) + 1;
              const steerEnabled = composerSteerEnabled(item.localId);
              return (
                <li key={item.localId} className="pending-send-queue-item">
                  <UnorderedListOutlined className="pending-send-queue-icon" aria-hidden="true" />
                  <div className="pending-send-queue-body">
                    <span className="pending-send-queue-text" title={queuePreview(item)}>
                      {item.parts ? item.parts.map((part,i) => part.kind === "text" ? part.text : <span key={i} className={`composer-chip composer-chip-${part.kind}`}>{displayPart(part)}</span>) : queuePreview(item)}
                    </span>
                    {item.attachments.length > 0 ? (
                      <span className="pending-send-queue-attachments">
                        {item.attachments.map((file) => file.displayName).join(", ")}
                      </span>
                    ) : null}
                    {item.dispatching ? (
                      <span className="pending-send-queue-status">Sending…</span>
                    ) : item.error ? (
                      <span className="pending-send-queue-status pending-send-queue-status-error">{item.error}</span>
                    ) : null}
                  </div>
                  <Flex gap={token.paddingXS} align="center" className="pending-send-queue-actions">
                    <Tooltip title="Steer">
                      <Button
                        type="text"
                        size="small"
                        disabled={!steerEnabled}
                        aria-label={`Steer queued message ${position}`}
                        onClick={() => void steerQueuedSend(item.localId)}
                      >
                        Steer
                      </Button>
                    </Tooltip>
                    <Tooltip title="Remove">
                      <Button
                        type="text"
                        size="small"
                        disabled={item.dispatching}
                        aria-label={`Remove queued message ${position}`}
                        icon={<DeleteOutlined />}
                        onClick={() => void removeQueuedSend(item.localId)}
                      />
                    </Tooltip>
                  </Flex>
                </li>
              );
            })}
          </ul>
          {!queueExpanded && hiddenCount > 0 ? (
            <span className="pending-send-queue-more">{hiddenCount} more queued</span>
          ) : null}
        </section>
      ) : null}

      <form
        className="composer composer-shell"
        onSubmit={(event) => {
          event.preventDefault();
          if(editorValid) onSend();
        }}
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
      >
        {pendingAttachments.length > 0 ? (
          <ul className="attach-list" aria-label="Pending attachments">
            {pendingAttachments.map((item) => (
              <PendingAttachmentView
                key={item.localId}
                item={item}
                onRetry={() => void retryComposerFile(item.localId)}
                onRemove={() => void removeComposerFile(item.localId)}
              />
            ))}
          </ul>
        ) : null}
        <StructuredMessageEditor draft={draft} parts={parts} instanceId={instanceId} ready={ready} placeholder={placeholder} canSend={canSend && editorValid} onValidationChange={setEditorValid} onChange={onDraftChange} onPartsChange={onPartsChange} onSend={onSend} onFiles={takeFiles} pickerRequest={pickerRequest}/>
        <input
          ref={fileInput}
          className="attach-input"
          type="file"
          multiple
          aria-hidden="true"
          tabIndex={-1}
          onChange={(event) => {
            takeFiles(event.target.files);
            event.target.value = "";
          }}
        />
        <Flex justify="space-between" align="center" gap={token.paddingXS} wrap="wrap" className="composer-toolbar">
          <Flex gap={token.paddingXS} align="center" wrap="wrap" className="composer-toolbar-start">
            {modelControls}
            <Dropdown trigger={["click"]} menu={{items:[{key:"attach",label:"Attach file",icon:<PaperClipOutlined/>},{key:"invocation",label:"Use Skill"},{key:"homeFile",label:"Reference resource"}],onClick:({key})=>{if(key==="attach") fileInput.current?.click();else setPickerRequest({category:key as "invocation"|"homeFile",nonce:Date.now()});}}}>
              <Button type="text" className="composer-icon" aria-label="Add content" disabled={!ready} icon={<PlusOutlined/>}/>
            </Dropdown>
            {voiceAvailable ? (
              <>
                <VoiceModeControl
                  pendingVoice={pendingVoice}
                  voiceModeActive={voiceModeActive}
                  onVoice={onVoice}
                  onCancelVoice={onCancelVoice}
                />
                {voiceModeActive && !pendingVoice ? (
                  <MicrophoneControl
                    voiceInputLive={voiceInputLive}
                    voiceInputBlocked={voiceInputBlocked}
                    voiceInputHeldForAgentOutput={voiceInputHeldForAgentOutput}
                    captureNeedsResume={captureNeedsResume}
                    muted={muted}
                    onVoice={onVoice}
                    onMute={onMute}
                    onResumeCapture={onResumeCapture}
                  />
                ) : null}
              </>
            ) : null}
          </Flex>
          <Flex gap={token.paddingXS} align="center" className="composer-toolbar-end">
            {canRetry ? (
              <Button aria-label="Retry" onClick={onRetry}>
                Retry
              </Button>
            ) : null}
            {canStop && canSend ? (
              <Tooltip title="Stop">
                <Button
                  htmlType="button"
                  type="text"
                  className="composer-icon composer-stop"
                  aria-label="Stop"
                  icon={<StopOutlined />}
                  onClick={() => {
                    onStop();
                    focusMessage();
                  }}
                />
              </Tooltip>
            ) : null}
            {canStop && !canSend ? (
              <Tooltip title="Stop">
                <Button
                  htmlType="button"
                  className="composer-icon composer-stop"
                  aria-label="Stop"
                  icon={<StopOutlined />}
                  onClick={() => {
                    onStop();
                    focusMessage();
                  }}
                />
              </Tooltip>
            ) : (
              <Tooltip title={`${sendLabel} (Enter) · Steer immediately (⌘Enter / Ctrl+Enter)`}>
                <Button
                  type="primary"
                  htmlType="submit"
                  className="composer-icon composer-send"
                  aria-label={sendLabel}
                  disabled={!canSend || !editorValid}
                  icon={<SendOutlined />}
                />
              </Tooltip>
            )}
          </Flex>
        </Flex>
      </form>
    </Flex>
  );
}
