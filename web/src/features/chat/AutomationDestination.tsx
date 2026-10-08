import { Button, Flex, Typography } from "antd";
import { navigateToAppPath } from "../../app/appRoute";

import type { AutomationTarget, AutomationDelivery } from "../../services/api";

export function ConversationDestination({ sessionId, currentSessionId }: { sessionId: string; currentSessionId?: string }) {
  const path = `/c/${encodeURIComponent(sessionId)}`;
  return <Typography.Link href={path} onClick={event => { event.preventDefault(); navigateToAppPath(path); }}>
    {sessionId === currentSessionId ? "This conversation" : `Conversation ${sessionId.slice(0, 8)}`}
  </Typography.Link>;
}

export function AutomationDestination({ target, delivery, currentSessionId }: {
  target: AutomationTarget; delivery: AutomationDelivery; currentSessionId?: string;
}) {
  return <>
    <Typography.Text type="secondary">Destination: {target.kind === "existingSession" && target.sessionId
      ? <ConversationDestination sessionId={target.sessionId} currentSessionId={currentSessionId} /> : "Background work"}</Typography.Text>
    {delivery.kind === "toSession" && delivery.sessionId ? <Typography.Text type="secondary">Reports to: <ConversationDestination sessionId={delivery.sessionId} currentSessionId={currentSessionId} /></Typography.Text> : null}
  </>;
}

export function CompletionDeliveryStatus({ delivery, onInspect }: { delivery: { status: string; targetSessionId: string | null; parentAgentRunId?: string | null; reason: string | null }; onInspect?: (runId: string) => void }) {
  const label = ({ notRequested: "Not requested", pending: "Ready", claimed: "In use", handled: "Handled in conversation", admitted: "Pending · message queued",
    delivered: "Reported", failed: "Could not report", skipped: "Not reported" } as Record<string, string>)[delivery.status] ?? "Pending";
  return <Flex vertical gap="small"><Typography.Text type="secondary">Completion: {label}
    {delivery.targetSessionId ? <> · <ConversationDestination sessionId={delivery.targetSessionId} /></> : null}
    {delivery.reason ? ` · ${delivery.reason.replaceAll("-", " ")}. Result remains in Background work.` : null}
  </Typography.Text>
    {delivery.status === "handled" && delivery.parentAgentRunId && onInspect ? <Button type="link" style={{ alignSelf: "flex-start" }} onClick={() => onInspect(delivery.parentAgentRunId!)}>View handling run</Button> : null}
  </Flex>;
}
