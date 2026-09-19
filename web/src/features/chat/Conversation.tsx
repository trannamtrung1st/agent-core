import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, Empty, Flex, Typography } from "antd";
import type { HistoryEntry } from "../../state/sessionStore";
import { AgentActivity } from "./AgentActivity";
import type { AgentActivityState } from "./activityState";
import { ChatMessage } from "./ChatMessage";
import { formatChatTime, statusLabel } from "./chatTime";

const REPLY_SPACE_RATIO = 0.5;

export function Conversation({
  agentName,
  sessionId,
  entries,
  liveUserTranscript = null,
  activity,
  voiceAvailable = true,
  sttTransport = null,
  empty,
  hasOlder = false,
  olderLoading = false,
  onLoadOlder
}: {
  agentName: string;
  sessionId: string | null;
  entries: HistoryEntry[];
  liveUserTranscript?: string | null;
  activity: AgentActivityState;
  voiceAvailable?: boolean;
  sttTransport?: "serverAudio" | "clientTranscript" | null;
  empty?: ReactNode;
  hasOlder?: boolean;
  olderLoading?: boolean;
  onLoadOlder?: () => void;
}) {
  const windowRef = useRef<HTMLElement>(null);
  const spacerRef = useRef<HTMLDivElement>(null);
  const scrolledFor = useRef<string | null>(null);
  const [replySpace, setReplySpace] = useState(0);
  const placeholderEntry = entries.find((entry) => isStreamingPlaceholder(entry, sessionId));
  const suppressPlaceholder = activity.kind !== "idle" && placeholderEntry != null;
  const visibleEntries = suppressPlaceholder
    ? entries.filter((entry) => entry !== placeholderEntry)
    : entries;
  const lastUserIndex = lastUserEntryIndex(visibleEntries);
  const lastUserId = lastUserIndex >= 0 ? visibleEntries[lastUserIndex]?.entryId ?? null : null;
  const lastEntryId = visibleEntries.at(-1)?.entryId ?? null;
  const oldestEntryId = visibleEntries[0]?.entryId ?? null;
  const scrollAnchorKey =
    visibleEntries.length === 0
      ? null
      : `${sessionId ?? ""}:${lastUserId ?? ""}:${lastEntryId ?? ""}`;
  const sessionRef = useRef<string | null>(null);
  const oldestRef = useRef<string | null>(null);
  const lastEntryRef = useRef<string | null>(null);
  const scrollHeightRef = useRef(0);
  const scrollTopRef = useRef(0);

  useLayoutEffect(() => {
    const root = windowRef.current;
    const spacer = spacerRef.current;
    const scroll = root?.closest(".conversation-scroll");
    if (!root || !scroll || scrollAnchorKey == null) {
      scrolledFor.current = null;
      setReplySpace(0);
      sessionRef.current = sessionId;
      oldestRef.current = oldestEntryId;
      lastEntryRef.current = lastEntryId;
      return;
    }

    const scrollToLatest = (): boolean => {
      if (scroll.clientHeight <= 0) {
        return false;
      }

      const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      const target = spacer ?? root;
      target.scrollIntoView?.({
        block: "end",
        inline: "nearest",
        behavior: reduceMotion ? "auto" : "smooth"
      });
      return true;
    };

    const sessionChanged = sessionRef.current !== sessionId;
    const prepended = !sessionChanged
      && oldestEntryId != null
      && oldestEntryId !== oldestRef.current
      && lastEntryId === lastEntryRef.current;
    const appended = !sessionChanged && lastEntryId !== lastEntryRef.current;

    if (prepended) {
      const delta = scroll.scrollHeight - scrollHeightRef.current;
      scroll.scrollTop = scrollTopRef.current + delta;
      scrolledFor.current = scrollAnchorKey;
    } else {
      const maybeScrollToLatest = (): void => {
        if (!sessionChanged && !appended && scrolledFor.current === scrollAnchorKey) {
          return;
        }

        if (scrollToLatest()) {
          scrolledFor.current = scrollAnchorKey;
        }
      };

      const measure = (): HTMLElement | null => {
        const scrollHeight = scroll.clientHeight;
        if (scrollHeight <= 0) {
          setReplySpace(0);
          return null;
        }

        const anchor = root.querySelector<HTMLElement>("[data-turn-anchor='true']");
        if (!anchor) {
          setReplySpace(0);
          return null;
        }

        const paddingBottom = Number.parseFloat(getComputedStyle(root).paddingBottom);
        const padding = Number.isFinite(paddingBottom) ? paddingBottom : 0;
        const rootBox = root.getBoundingClientRect();
        const anchorBox = anchor.getBoundingClientRect();
        const spacerHeight = spacer?.getBoundingClientRect().height ?? 0;
        const safeSpacerHeight = Number.isFinite(spacerHeight) ? spacerHeight : 0;

        if (!Number.isFinite(rootBox.bottom) || !Number.isFinite(anchorBox.bottom)) {
          setReplySpace(0);
          return anchor;
        }

        const below = rootBox.bottom - padding - anchorBox.bottom;
        const following = Math.max(0, (Number.isFinite(below) ? below : 0) - safeSpacerHeight);
        const budget = Number.isFinite(scrollHeight) ? scrollHeight * REPLY_SPACE_RATIO : 0;
        const raw = budget - following;
        const next = Number.isFinite(raw) ? Math.max(0, Math.round(raw)) : 0;
        setReplySpace((current) => (current === next ? current : next));
        return anchor;
      };

      const runMeasure = (): void => {
        measure();
        maybeScrollToLatest();
      };

      runMeasure();

      const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(runMeasure);
      observer?.observe(scroll);
      observer?.observe(root);
      window.addEventListener("resize", runMeasure);
      sessionRef.current = sessionId;
      oldestRef.current = oldestEntryId;
      lastEntryRef.current = lastEntryId;
      scrollHeightRef.current = scroll.scrollHeight;
      scrollTopRef.current = scroll.scrollTop;
      return () => {
        observer?.disconnect();
        window.removeEventListener("resize", runMeasure);
      };
    }

    sessionRef.current = sessionId;
    oldestRef.current = oldestEntryId;
    lastEntryRef.current = lastEntryId;
    scrollHeightRef.current = scroll.scrollHeight;
    scrollTopRef.current = scroll.scrollTop;
    return undefined;
  }, [activity, entries, lastUserId, lastEntryId, oldestEntryId, liveUserTranscript, scrollAnchorKey, sessionId]);

  const safeReplySpace = Number.isFinite(replySpace) ? Math.max(0, replySpace) : 0;
  const emptyHint = voiceAvailable
    ? (sttTransport === "clientTranscript"
      ? "Send a message or start voice. Agent Core does not send PCM to backend speech recognition. The browser vendor may use a cloud recognizer. Choose another speech provider for backend-controlled or local recognition."
      : "Send a message or start voice.")
    : "Send a message.";

  return (
    <section ref={windowRef} className="conversation-window" aria-label="Conversation">
      {hasOlder || olderLoading ? (
        <Flex justify="center" className="conversation-history-pager">
          <Button
            size="small"
            htmlType="button"
            loading={olderLoading}
            disabled={olderLoading || !hasOlder}
            aria-label="Load earlier messages"
            onClick={() => onLoadOlder?.()}
          >
            Load earlier messages
          </Button>
        </Flex>
      ) : null}
      <ol className="conversation-list">
        {entries.length === 0 ? (
          <li className="chat-message chat-message-empty">
            {empty ?? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyHint} />
            )}
          </li>
        ) : (
          visibleEntries.map((entry, index) => (
            <ChatMessage
              key={entry.entryId}
              entry={entry}
              agentName={agentName}
              sessionId={sessionId}
              turnAnchor={index === lastUserIndex && !liveUserTranscript}
            />
          ))
        )}
        {liveUserTranscript ? (
          <li
            data-role="user"
            data-testid="live-user-transcript"
            className="chat-message chat-message-user chat-message-live-transcript"
          >
            <Flex align="baseline" gap={8} className="chat-message-meta">
              <Typography.Text type="secondary" className="chat-message-speaker">
                You
              </Typography.Text>
            </Flex>
            <div className="user-bubble user-bubble-live">
              <Typography.Paragraph className="user-bubble-text">{liveUserTranscript}</Typography.Paragraph>
            </div>
          </li>
        ) : null}
      </ol>
      {activity.kind !== "idle" ? (
        <div className="agent-turn-activity">
          {placeholderEntry ? (
            <AssistantMeta
              agentName={agentName}
              createdAt={placeholderEntry.createdAt}
            />
          ) : null}
          <AgentActivity activity={activity} />
        </div>
      ) : null}
      {lastUserId ? (
        <div
          ref={spacerRef}
          className="conversation-reply-space"
          data-testid="conversation-reply-space"
          aria-hidden="true"
          style={{ height: safeReplySpace }}
        />
      ) : null}
    </section>
  );
}

function entryHasVisibleBody(entry: HistoryEntry, sessionId: string | null): boolean {
  const hasFiles = Boolean(entry.attachments?.length && sessionId);
  const hasBlocks = Boolean(entry.blocks?.length);
  return Boolean(entry.text) || hasBlocks || hasFiles;
}

function isStreamingPlaceholder(entry: HistoryEntry, sessionId: string | null): boolean {
  return (
    entry.role === "assistant" &&
    entry.status === "streaming" &&
    !entryHasVisibleBody(entry, sessionId) &&
    statusLabel(entry.status) == null
  );
}

function AssistantMeta({ agentName, createdAt }: { agentName: string; createdAt: string }) {
  const timeLabel = formatChatTime(createdAt);
  const speaker = agentName || "Agent";

  return (
    <Flex align="baseline" gap={8} className="chat-message-meta">
      <Typography.Text type="secondary" className="chat-message-speaker">
        {speaker}
      </Typography.Text>
      {timeLabel ? (
        <Typography.Text type="secondary" className="chat-message-time">
          <time dateTime={createdAt}>{timeLabel}</time>
        </Typography.Text>
      ) : null}
    </Flex>
  );
}

function lastUserEntryIndex(entries: HistoryEntry[]): number {
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    if (entries[index]?.role === "user") {
      return index;
    }
  }

  return -1;
}
