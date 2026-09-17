import type { ReactNode } from "react";
import { Empty, Spin } from "antd";
import type { HistoryEntry } from "../../state/sessionStore";
import { AgentActivity } from "./AgentActivity";
import type { AgentActivityState } from "./activityState";
import { ChatMessage } from "./ChatMessage";

export function Conversation({
  agentName,
  sessionId,
  entries,
  connection = "ready",
  activity,
  empty
}: {
  agentName: string;
  sessionId: string | null;
  entries: HistoryEntry[];
  connection?: string;
  activity: AgentActivityState;
  empty?: ReactNode;
}) {
  const loading = connection === "connecting" || connection === "reconnecting";

  return (
    <section className="conversation-window" aria-label="Conversation">
      <Spin spinning={loading} description="Loading conversation">
        <ol className="conversation-list">
          {entries.length === 0 ? (
            <li className="chat-message chat-message-empty">
              {empty ?? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Send a message or start voice." />
              )}
            </li>
          ) : (
            entries.map((entry) => (
              <ChatMessage
                key={entry.entryId}
                entry={entry}
                agentName={agentName}
                sessionId={sessionId}
              />
            ))
          )}
        </ol>
        <AgentActivity activity={activity} />
      </Spin>
    </section>
  );
}
