import { sessionPath } from "../../app/sessionRoute";
import { followAppLink } from "../../app/appLink";
import { Flex, Typography } from "antd";
import type { AutomationTarget, AutomationDelivery } from "../../services/api";

export function ConversationDestination({ sessionId, currentSessionId }: { sessionId: string; currentSessionId?: string }) {
  const path = sessionPath(sessionId);
  return <Typography.Link href={path} onClick={followAppLink}>
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

export function CompletionDeliveryStatus({ delivery }: { delivery: { status: string; targetSessionId: string | null; parentAgentRunId?: string | null; reason: string | null } }) {
  const label = ({ notRequested: "Not requested", pending: "Ready", admitted: "Pending · message queued",
    delivered: "Reported", failed: "Could not report", skipped: "Not reported" } as Record<string, string>)[delivery.status] ?? "Pending";
  return <Flex vertical gap="small"><Typography.Text type="secondary">Completion: {label}
    {delivery.targetSessionId ? <> · <ConversationDestination sessionId={delivery.targetSessionId} /></> : null}
    {delivery.reason ? ` · ${delivery.reason.replaceAll("-", " ")}. Result remains in Background work.` : null}
  </Typography.Text>
  </Flex>;
}
