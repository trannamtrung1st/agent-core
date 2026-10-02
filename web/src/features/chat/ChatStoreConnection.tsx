import { useEffect, useState } from "react";
import { Button, Typography } from "antd";
import { adminInstancePath, navigateToAppPath, rememberChatUrl } from "../../app/appRoute";
import { getSession } from "../../services/api";
import { getApplicationConnection } from "../../services/adminApi";
import { suspendLiveSessionForNavigation } from "../../services/realtime";
import { connectionStatusLabel } from "../admin/ApplicationConnectionSection";

export function ChatStoreConnection({ sessionId }: { sessionId: string }) {
  const [label, setLabel] = useState<string | null>(null);
  const [instanceId, setInstanceId] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setLabel(null);
    setInstanceId(null);
    void getSession(sessionId)
      .then(async (session) => {
        if (!current || !session.agentInstanceId) {
          return;
        }
        const connection = await getApplicationConnection(session.agentInstanceId);
        if (!current) {
          return;
        }
        setInstanceId(session.agentInstanceId);
        setLabel(connectionStatusLabel(connection?.status));
      })
      .catch(() => {
        if (current) {
          setLabel(null);
        }
      });
    return () => {
      current = false;
    };
  }, [sessionId]);

  if (!label || !instanceId) {
    return null;
  }

  return (
    <Typography.Text className="chat-store-connection" aria-label="Store connection">
      Store {label}
      <Button
        type="link"
        size="small"
        aria-label="Manage store connection"
        onClick={() => {
          rememberChatUrl(window.location.pathname);
          void suspendLiveSessionForNavigation().then(() => {
            navigateToAppPath(adminInstancePath(instanceId));
          });
        }}
      >
        Manage
      </Button>
    </Typography.Text>
  );
}
