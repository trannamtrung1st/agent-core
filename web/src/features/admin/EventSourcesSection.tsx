import { useEffect, useState } from "react";
import { Alert, App, Button, Empty, Flex, Form, Input, Modal, Spin, Table, Tag, Typography, theme } from "antd";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { confirmAction } from "../../app/confirmAction";
import {
  createEventSource,
  listEventSources,
  revokeEventSource,
  rotateEventSource,
  type AdminEventSource
} from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction } from "./adminFailure";

export function webhookUrl(sourceKey: string): string {
  return `${window.location.origin}/api/v1/hooks/${sourceKey}`;
}

export function EventSourcesSection() {
  const { token } = theme.useToken();
  const { message, modal } = App.useApp();
  const [sources, setSources] = useState<AdminEventSource[]>([]);
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [readRequest, setReadRequest] = useState(0);
  const [credentialError, setCredentialError] = useState<string | null>(null);
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
          setLoaded(true);
        }
      })
      .catch((reason: unknown) => {
        if (current) {
          setError(describeAdminError(reason, "Unable to load event sources."));
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
  }, [readRequest]);

  async function copyText(value: string, success: string, inCredentialDialog = false) {
    if (inCredentialDialog) setCredentialError(null);
    try {
      await navigator.clipboard.writeText(value);
      message.success(success);
    } catch {
      const guidance = "Copy failed. Select the value and copy it manually.";
      if (inCredentialDialog) setCredentialError(guidance);
      else setError({ message: guidance });
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
      setCredentialError(null);
      setCredential(issued.token);
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The event source could not be created."));
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
      setCredentialError(null);
      setCredential(issued.token);
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The credential could not be rotated."));
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
      setError(describeAdminError(reason, "The event source could not be revoked."));
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
          {error ? <Alert type="error" showIcon title={error.message}
            action={<AdminRetryAction onRetry={() => setReadRequest(value => value + 1)} diagnosticId={error.diagnosticId} />} /> : null}
          {!loading && loaded ? (
            <>
              <Form
                className="admin-config-form"
                layout="vertical"
                onFinish={() => {
                  void create();
                }}
              >
                <Form.Item label="Event source name">
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
              <AdminCollectionToolbar label="event sources" value={search} onChange={setSearch} />
              <Table
                aria-label="Event sources table" className="admin-collection-table" rowKey="sourceId" size="small"
                dataSource={sources.filter(source => [source.displayName, source.sourceKey, source.kind, source.status]
                  .some(value => value.toLowerCase().includes(search.trim().toLowerCase())))}
                pagination={pagination} scroll={{ x: 1140 }}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={search.trim() || sources.length > 0 ? "No matches. Clear search or filters to see all results." : "No event sources yet."} /> }}
                columns={[
                  { title: "Name", dataIndex: "displayName", width: 220, ellipsis: true, sorter: (a, b) => a.displayName.localeCompare(b.displayName),
                    render: (name: string) => <Typography.Text strong>{name}</Typography.Text> },
                  { title: "Type", dataIndex: "kind", width: 100 },
                  { title: "Status", key: "status", width: 100,
                    filters: [{ text: "Active", value: "Active" }, { text: "Revoked", value: "Revoked" }],
                    onFilter: (value, source) => source.status === value,
                    render: (_, source) => <Tag>{source.status}</Tag> },
                  { title: "Source key", dataIndex: "sourceKey", width: 320,
                    render: (key: string) => <Typography.Text aria-label="Source key">{key}</Typography.Text> },
                  { title: "Actions", key: "actions", width: 400, render: (_, source) => (
                    <Flex gap={token.paddingXS} align="center">
                      <Button
                        type="link"
                        size="small"
                        disabled={busy}
                        aria-label={`Copy webhook URL for ${source.displayName}`}
                        onClick={() => void copyText(webhookUrl(source.sourceKey), "Webhook URL copied.")}
                      >
                        Copy webhook URL
                      </Button>
                      <Button
                        size="small"
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
                          size="small"
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
                  ) }
                ]}
              />
            </>
          ) : null}
          <Modal
            open={credential !== null}
            title="Copy this credential"
            okText="Done"
            cancelButtonProps={{ style: { display: "none" } }}
            destroyOnHidden
            onOk={() => { setCredential(null); setCredentialError(null); }}
            onCancel={() => { setCredential(null); setCredentialError(null); }}
          >
            <Flex vertical gap={token.paddingXS}>
              <Typography.Paragraph>
                Copy this credential now. It authenticates the emitter and will not be shown again.
              </Typography.Paragraph>
              {credentialError ? <Alert type="error" showIcon title={credentialError} /> : null}
              <Input.TextArea readOnly aria-label="Event source credential" value={credential ?? ""} autoSize />
              <Button
                aria-label="Copy event source credential"
                onClick={() => void copyText(credential ?? "", "Event source credential copied.", true)}
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
