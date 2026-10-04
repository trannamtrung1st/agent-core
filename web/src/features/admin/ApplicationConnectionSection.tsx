import { useEffect, useState } from "react";
import { Alert, App, Button, Flex, Form, Input, Spin, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import {
  connectApplication,
  getApplicationConnection,
  openApplicationBrowser,
  reauthenticateApplication,
  resetApplicationProfile,
  revokeApplication,
  type ApplicationConnection
} from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";

const detailCopy: Record<string, string> = {
  sign_in_required: "Sign in is required in the application browser.",
  login_wall: "The application is asking for sign-in again.",
  human_verification: "A person needs to finish a verification check.",
  browser_unavailable: "The browser is unavailable.",
  profile_reset: "The saved sign-in was cleared.",
  page_unknown: "The page could not be recognized."
};

export function connectionStatusLabel(status: string | null | undefined): string {
  switch (status) {
    case "Connecting":
      return "Connecting / sign-in required";
    case "Connected":
      return "Connected";
    case "NeedsReauthentication":
      return "Needs reauthentication";
    case "Unavailable":
      return "Unavailable";
    default:
      return "Not connected";
  }
}

export function ApplicationConnectionSection({ instanceId }: { instanceId: string }) {
  const { token } = theme.useToken();
  const { message, modal } = App.useApp();
  const [connection, setConnection] = useState<ApplicationConnection | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError(null);
    void getApplicationConnection(instanceId)
      .then((next) => {
        if (current) {
          setConnection(next);
        }
      })
      .catch((reason: unknown) => {
        if (current) {
          setConnection(null);
          setError(describeAdminError(reason, "Unable to load the application connection.").message);
        }
      })
      .finally(() => {
        if (current) {
          setLoading(false);
        }
      });
    return () => {
      current = false;
    };
  }, [instanceId]);

  async function run(action: () => Promise<ApplicationConnection>, success: string) {
    setBusy(true);
    setError(null);
    try {
      setConnection(await action());
      message.success(success);
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The application connection could not be updated.").message);
    } finally {
      setBusy(false);
    }
  }

  const status = connection?.status ?? null;
  const detail = connection?.statusDetail ? detailCopy[connection.statusDetail] : null;
  const canReauthenticate = status === "NeedsReauthentication" || status === "Connecting" || status === "Unavailable";

  return (
    <section className="admin-definition-panel" aria-label="Application connection">
      <div className="admin-definition-panel-heading">
        <Typography.Title level={4}>Application connection</Typography.Title>
        <Typography.Text type="secondary">
          Connect the supported nopCommerce application to this agent. Authentication stays in this agent&apos;s browser profile.
        </Typography.Text>
      </div>
      <div className="admin-definition-panel-body">
        <Flex vertical gap={token.paddingSM}>
          {loading ? <Spin aria-label="Loading application connection" /> : null}
          {error ? <Alert type="error" showIcon title={error} /> : null}
          {!loading ? (
            <>
              <Typography.Text strong>{connectionStatusLabel(status)}</Typography.Text>
              <Flex vertical>
                <Typography.Text type="secondary">Application type</Typography.Text>
                <Typography.Text>nopCommerce</Typography.Text>
              </Flex>
              {connection ? (
                <Flex vertical>
                  <Typography.Text>{connection.displayName}</Typography.Text>
                  <Typography.Text type="secondary">{connection.baseUrl}</Typography.Text>
                </Flex>
              ) : null}
              {detail ? <Typography.Text>{detail}</Typography.Text> : null}
              {status === null || status === "NotConnected" ? (
                <Form
                  layout="vertical"
                  onFinish={() =>
                    void run(
                      () => connectApplication(instanceId, displayName.trim(), baseUrl.trim()),
                      "Opening the application sign-in."
                    )
                  }
                >
                  <Form.Item label="Display name">
                    <Input
                      aria-label="Display name"
                      value={displayName}
                      disabled={busy}
                      onChange={(event) => setDisplayName(event.target.value)}
                    />
                  </Form.Item>
                  <Form.Item label="Base URL">
                    <Input
                      aria-label="Base URL"
                      value={baseUrl}
                      disabled={busy}
                      onChange={(event) => setBaseUrl(event.target.value)}
                    />
                  </Form.Item>
                  <Button type="primary" htmlType="submit" disabled={busy || displayName.trim().length === 0 || baseUrl.trim().length === 0}>
                    Connect
                  </Button>
                </Form>
              ) : null}
              <Flex gap={token.paddingXS} wrap="wrap">
                {canReauthenticate ? (
                  <Button
                    disabled={busy}
                    aria-label="Reauthenticate"
                    onClick={() => void run(() => reauthenticateApplication(instanceId), "Opening the application sign-in.")}
                  >
                    Reauthenticate
                  </Button>
                ) : null}
                {connection ? (
                  <Button
                    disabled={busy}
                    aria-label="Open browser"
                    onClick={() => void run(() => openApplicationBrowser(instanceId), "Opened the application browser.")}
                  >
                    Open browser
                  </Button>
                ) : null}
                {connection && status !== "NotConnected" ? (
                  <Button
                    danger
                    disabled={busy}
                    aria-label="Revoke connection"
                    onClick={() =>
                      confirmAction(modal, {
                        title: "Revoke this connection?",
                        content: "The browser profile stays. The application cannot be used until you connect again.",
                        okText: "Revoke",
                        cancelText: "Keep",
                        danger: true,
                        onOk: () => run(() => revokeApplication(instanceId), "Connection revoked.")
                      })
                    }
                  >
                    Revoke
                  </Button>
                ) : null}
                {connection ? (
                  <Button
                    danger
                    disabled={busy}
                    aria-label="Reset profile"
                    onClick={() =>
                      confirmAction(modal, {
                        title: "Reset the browser profile?",
                        content: "This clears the saved sign-in for this agent only.",
                        okText: "Reset profile",
                        cancelText: "Keep",
                        danger: true,
                        onOk: () => run(() => resetApplicationProfile(instanceId), "Profile reset.")
                      })
                    }
                  >
                    Reset profile
                  </Button>
                ) : null}
              </Flex>
            </>
          ) : null}
        </Flex>
      </div>
    </section>
  );
}
