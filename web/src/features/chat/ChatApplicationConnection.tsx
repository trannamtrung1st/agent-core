import { useEffect, useState } from "react";
import { Button, Flex, Typography, theme } from "antd";
import { LinkOutlined } from "@ant-design/icons";
import { adminInstancePath, navigateToAppPath, rememberChatUrl } from "../../app/appRoute";
import { getSession } from "../../services/api";
import { getApplicationConnection, type ApplicationConnection } from "../../services/adminApi";
import { suspendLiveSessionForNavigation } from "../../services/realtime";

export function chatConnectionStatusLabel(status: string): string {
  switch (status) {
    case "Connecting":
      return "Connecting";
    case "Connected":
      return "Connected";
    case "NeedsReauthentication":
      return "Needs reauthentication";
    case "Unavailable":
      return "Unavailable";
    case "NotConnected":
      return "Not connected";
    default:
      return status;
  }
}

export function ChatApplicationConnection({ sessionId }: { sessionId: string }) {
  const { token } = theme.useToken();
  const [connection, setConnection] = useState<ApplicationConnection | null>(null);
  const [instanceId, setInstanceId] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setConnection(null);
    setInstanceId(null);
    void getSession(sessionId)
      .then(async (session) => {
        if (!current || !session.agentInstanceId) {
          return;
        }
        const next = await getApplicationConnection(session.agentInstanceId);
        if (!current || !next) {
          return;
        }
        setInstanceId(session.agentInstanceId);
        setConnection(next);
      })
      .catch(() => {
        if (current) {
          setConnection(null);
          setInstanceId(null);
        }
      });
    return () => {
      current = false;
    };
  }, [sessionId]);

  if (!connection || !instanceId) {
    return null;
  }

  const status = chatConnectionStatusLabel(connection.status);
  const visible = `${connection.displayName} · ${status}`;

  return (
    <Flex align="center" gap={token.paddingXS} className="chat-application-connection">
      <LinkOutlined aria-hidden className="chat-application-connection-icon" />
      <Typography.Text
        className="chat-application-connection-label"
        ellipsis={{ tooltip: visible }}
        aria-label={`Application connection: ${connection.displayName}, ${status}`}
      >
        {visible}
      </Typography.Text>
      <Button
        type="link"
        size="small"
        className="chat-application-connection-manage"
        aria-label="Manage application connection"
        onClick={() => {
          rememberChatUrl(window.location.pathname);
          void suspendLiveSessionForNavigation().then(() => {
            navigateToAppPath(adminInstancePath(instanceId));
          });
        }}
      >
        Manage
      </Button>
    </Flex>
  );
}
