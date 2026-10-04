import { useEffect, useState } from "react";
import { Alert, App, Button, Empty, Flex, Form, Input, Modal, Spin, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import {
  createEventSource,
  listEventSources,
  revokeEventSource,
  rotateEventSource,
  type AdminEventSource
} from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";

export function webhookUrl(sourceKey: string): string {
  return `${window.location.origin}/api/v1/hooks/${sourceKey}`;
}

export function EventSourcesSection() {
  const { token } = theme.useToken();
  const { message, modal } = App.useApp();
  const [sources, setSources] = useState<AdminEventSource[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const [credential, setCredential] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError(null);
    void listEventSources()
      .then((items) => {
        if (current) {
          setSources(items);
        }
      })
      .catch((reason: unknown) => {
        if (current) {
          setError(describeAdminError(reason, "Unable to load event sources.").message);
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
  }, []);

  async function copyText(value: string, success: string) {
    try {
      await navigator.clipboard.writeText(value);
      message.success(success);
    } catch {
      setError("Copy failed. Select the value and copy it manually.");
    }
  }

  async function create() {
    setBusy(true);
    setError(null);
    try {
      const issued = await createEventSource(displayName.trim());
      setSources((current) => [
        {
          sourceId: issued.sourceId,
          displayName: displayName.trim(),
          kind: "Webhook",
          sourceKey: issued.sourceKey,
          status: issued.status,
          revision: 1
        },
        ...current.filter((item) => item.sourceId !== issued.sourceId)
      ]);
      setDisplayName("");
      setCredential(issued.token);
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The event source could not be created.").message);
    } finally {
      setBusy(false);
    }
  }

  async function rotate(source: AdminEventSource) {
    setBusy(true);
    setError(null);
    try {
      const issued = await rotateEventSource(source.sourceId);
      setSources((current) =>
        current.map((item) =>
          item.sourceId === source.sourceId
            ? { ...item, sourceKey: issued.sourceKey, status: issued.status, revision: item.revision + 1 }
            : item
        )
      );
      setCredential(issued.token);
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The credential could not be rotated.").message);
    } finally {
      setBusy(false);
    }
  }

  async function revoke(source: AdminEventSource) {
    setBusy(true);
    setError(null);
    try {
      const saved = await revokeEventSource(source.sourceId);
      setSources((current) => current.map((item) => (item.sourceId === saved.sourceId ? saved : item)));
      message.success("Event source revoked.");
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The event source could not be revoked.").message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="admin-definition-panel" aria-label="Event sources">
      <div className="admin-definition-panel-heading">
        <Typography.Title level={4}>Event sources</Typography.Title>
        <Typography.Text type="secondary">
          A source credential proves who sent an event. It does not grant an agent a browser or application connection.
        </Typography.Text>
      </div>
      <div className="admin-definition-panel-body">
        <Flex vertical gap={token.paddingSM}>
          {loading ? <Spin aria-label="Loading event sources" /> : null}
          {error ? <Alert type="error" showIcon title={error} /> : null}
          {!loading ? (
            <>
              <Form
                layout="vertical"
                onFinish={() => {
                  void create();
                }}
              >
                <Form.Item label="Display name">
                  <Input
                    aria-label="Event source name"
                    value={displayName}
                    disabled={busy}
                    onChange={(event) => setDisplayName(event.target.value)}
                  />
                </Form.Item>
                <Button type="primary" htmlType="submit" disabled={busy || displayName.trim().length === 0}>
                  Create event source
                </Button>
              </Form>
              {sources.length === 0 ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No event sources yet." />
              ) : (
                sources.map((source) => (
                  <Flex key={source.sourceId} vertical gap={token.paddingXS} aria-label={source.displayName}>
                    <Typography.Text strong>{source.displayName}</Typography.Text>
                    <Typography.Text type="secondary">{source.kind} · {source.status}</Typography.Text>
                    <Flex align="center" gap={token.paddingXS} wrap="wrap">
                      <Typography.Text aria-label="Source key">{source.sourceKey}</Typography.Text>
                      <Button
                        type="link"
                        size="small"
                        disabled={busy}
                        aria-label={`Copy webhook URL for ${source.displayName}`}
                        onClick={() => void copyText(webhookUrl(source.sourceKey), "Webhook URL copied.")}
                      >
                        Copy webhook URL
                      </Button>
                    </Flex>
                    <Flex gap={token.paddingXS} wrap="wrap">
                      <Button
                        disabled={busy}
                        aria-label={`Rotate credential for ${source.displayName}`}
                        onClick={() =>
                          confirmAction(modal, {
                            title: "Rotate this credential?",
                            content: "The current credential stops working immediately. Copy the new one before you leave.",
                            okText: "Rotate credential",
                            cancelText: "Keep",
                            danger: true,
                            onOk: () => rotate(source)
                          })
                        }
                      >
                        Rotate credential
                      </Button>
                      {source.status === "Active" ? (
                        <Button
                          danger
                          disabled={busy}
                          aria-label={`Revoke ${source.displayName}`}
                          onClick={() =>
                            confirmAction(modal, {
                              title: "Revoke this event source?",
                              content: "New events are rejected. Existing events and occurrences stay.",
                              okText: "Revoke source",
                              cancelText: "Keep",
                              danger: true,
                              onOk: () => revoke(source)
                            })
                          }
                        >
                          Revoke source
                        </Button>
                      ) : null}
                    </Flex>
                  </Flex>
                ))
              )}
            </>
          ) : null}
          <Modal
            open={credential !== null}
            title="Copy this credential"
            okText="Done"
            cancelButtonProps={{ style: { display: "none" } }}
            destroyOnHidden
            onOk={() => setCredential(null)}
            onCancel={() => setCredential(null)}
          >
            <Flex vertical gap={token.paddingXS}>
              <Typography.Paragraph>
                Copy this credential now. It authenticates the emitter and will not be shown again.
              </Typography.Paragraph>
              <Input.TextArea readOnly aria-label="Event source credential" value={credential ?? ""} autoSize />
              <Button
                aria-label="Copy event source credential"
                onClick={() => void copyText(credential ?? "", "Event source credential copied.")}
              >
                Copy credential
              </Button>
            </Flex>
          </Modal>
        </Flex>
      </div>
    </section>
  );
}
