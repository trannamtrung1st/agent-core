import { useEffect, useId, useRef, useState } from "react";
import { Alert, App, Button, Drawer, Empty, Flex, Form, Grid, Input, Modal, Spin, Table, Tag, Typography, theme } from "antd";
import type { FilterValue } from "antd/es/table/interface";
import { PlusOutlined } from "@ant-design/icons";
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
import { AdminErrorNotice, AdminRetryAction } from "./adminFailure";

export function webhookUrl(sourceKey: string): string {
  return `${window.location.origin}/api/v1/hooks/${sourceKey}`;
}

export function EventSourcesSection({ onAutomations }: { onAutomations?: () => void } = {}) {
  const { token } = theme.useToken();
  const { message, modal } = App.useApp();
  const [statusFilter, setStatusFilter] = useState<FilterValue | null>(null);
  const [sources, setSources] = useState<AdminEventSource[]>([]);
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [readRequest, setReadRequest] = useState(0);
  const [credentialError, setCredentialError] = useState<string | null>(null);
  const [displayName, setDisplayName] = useState("");
  const nameError = /[\u0000-\u001f\u007f-\u009f]/.test(displayName) ? "Use a name without control characters." : null;
  const [editorOpen, setEditorOpen] = useState(false);
  const [createError, setCreateError] = useState<AdminFailureNotice | null>(null);
  const compact = !Grid.useBreakpoint().md;
  const formId = useId();
  const opener = useRef<HTMLButtonElement>(null);
  const pendingCredential = useRef<string | null>(null);
  const credentialReturnFocus = useRef<HTMLElement | null>(null);
  const nameInput = useRef<import("antd").InputRef>(null);
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
    if (busy || !displayName.trim() || displayName.trim().length > 80 || nameError) return;
    setBusy(true);
    setCreateError(null);
    credentialReturnFocus.current = opener.current;
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
      pendingCredential.current = issued.token;
      setEditorOpen(false);
      setSearch("");
      setStatusFilter(null);
      setCredentialError(null);
    } catch (reason: unknown) {
      setCreateError(describeAdminError(reason, "The event source could not be created."));
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
          A source credential proves who sent an event. It does not grant an agent Browser capabilities or system credential bindings.
        </Typography.Text>
      </div>
      <div className="admin-definition-panel-body">
        <Flex vertical gap={token.paddingSM}>
          {onAutomations ? <Flex gap={token.paddingSM} wrap align="center"><Typography.Text type="secondary">Choose how this agent reacts to an event in Automation.</Typography.Text><Button onClick={onAutomations}>Open automations</Button></Flex> : null}
          {loading ? <Spin aria-label="Loading event sources" /> : null}
          {error ? <Alert type="error" showIcon title={error.message}
            action={<AdminRetryAction onRetry={() => setReadRequest(value => value + 1)} diagnosticId={error.diagnosticId} />} /> : null}
          {!loading && loaded ? (
            <>
              <Flex gap={token.paddingSM} wrap align="center" justify="space-between">
                <div style={{ flex: "1 1 16rem", minWidth: 0 }}>
                  <AdminCollectionToolbar label="event sources" value={search} onChange={setSearch} />
                </div>
                <Button ref={opener} type="primary" icon={<PlusOutlined aria-hidden />} disabled={busy}
                  onClick={() => { setDisplayName(""); setCreateError(null); setEditorOpen(true); }}>
                  New event source
                </Button>
              </Flex>
              <Table
                aria-label="Event sources table" className="admin-collection-table" rowKey="sourceId" size="small"
                dataSource={sources.filter(source => [source.displayName, source.sourceKey, source.kind, source.status]
                  .some(value => value.toLowerCase().includes(search.trim().toLowerCase())))}
                pagination={pagination} scroll={{ x: 1180 }}
                onChange={(_, filters) => setStatusFilter(filters.status ?? null)}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={search.trim() || sources.length > 0 ? "No matches. Clear search or filters to see all results." : "No event sources yet."} /> }}
                columns={[
                  { title: "Name", dataIndex: "displayName", width: 220, ellipsis: true, sorter: (a, b) => a.displayName.localeCompare(b.displayName),
                    render: (name: string) => <Typography.Text strong>{name}</Typography.Text> },
                  { title: "Type", dataIndex: "kind", width: 100 },
                  { title: "Status", key: "status", width: 100, filteredValue: statusFilter,
                    filters: [{ text: "Active", value: "Active" }, { text: "Revoked", value: "Revoked" }],
                    onFilter: (value, source) => source.status === value,
                    render: (_, source) => <Tag color={source.status === "Active" ? "success" : "default"}>{source.status}</Tag> },
                  { title: "Source key", dataIndex: "sourceKey", width: 320,
                    render: (key: string) => <Typography.Text code aria-label="Source key">{key}</Typography.Text> },
                  { title: "Actions", key: "actions", width: 440, render: (_, source) => (
                    <Flex className="admin-table-actions" gap={token.paddingXS} align="center">
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
                        onClick={event => {
                          credentialReturnFocus.current = event.currentTarget;
                          confirmAction(modal, {
                            title: "Rotate this credential?",
                            content: "The current credential stops working immediately. Copy the new one before you leave.",
                            okText: "Rotate credential",
                            cancelText: "Keep",
                            danger: true,
                            onOk: () => rotate(source)
                          });
                        }}
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
          <Drawer open={editorOpen} title="New event source" size={compact ? "100%" : 480}
            getContainer={false} rootStyle={{ position: "fixed" }}
            closable={!busy} maskClosable={!busy} keyboard={!busy}
            focusable={{ trap: editorOpen, focusTriggerAfterClose: false }}
            onClose={() => { if (!busy) setEditorOpen(false); }}
            afterOpenChange={visible => {
              if (visible) {
                const focused = document.activeElement;
                if (focused === document.body || focused?.getAttribute("role") === "dialog") nameInput.current?.focus();
              }
              else if (pendingCredential.current) {
                setCredential(pendingCredential.current);
                pendingCredential.current = null;
              } else opener.current?.focus();
            }}
            styles={{ body: { padding: token.padding }, footer: { padding: token.padding },
              close: compact ? { width: token.controlHeightLG, height: token.controlHeightLG } : undefined }}
            footer={<Flex justify="flex-end" gap={token.paddingXS}>
              <Button size={compact ? "large" : "middle"} disabled={busy} onClick={() => setEditorOpen(false)}>Cancel</Button>
              <Button size={compact ? "large" : "middle"} type="primary" aria-label="Create event source" aria-busy={busy} htmlType="submit" form={formId} loading={busy}
                disabled={busy || !displayName.trim() || !!nameError}>Create event source</Button>
            </Flex>}>
            <Flex vertical gap={token.padding}>
              <Typography.Paragraph type="secondary" style={{ margin: 0 }}>
                A source credential proves who sent an event. It does not grant an agent Browser capabilities or system credential bindings.
              </Typography.Paragraph>
              {createError ? <Alert type="error" showIcon title={<AdminErrorNotice message={createError.message} diagnosticId={createError.diagnosticId} showDetailsLabel />} /> : null}
              <Form id={formId} className="admin-config-form" layout="vertical" onFinish={() => void create()}>
                <Form.Item label="Event source name" validateStatus={nameError ? "error" : undefined} help={nameError}>
                  <Input size={compact ? "large" : "middle"} ref={nameInput} aria-label="Event source name" maxLength={80} placeholder="e.g. Demo Store"
                    value={displayName} disabled={busy} onChange={event => setDisplayName(event.target.value)} />
                </Form.Item>
              </Form>
            </Flex>
          </Drawer>
          <Modal
            open={credential !== null}
            title="Copy this credential"
            className="admin-credential-dialog"
            okText="Done"
            cancelButtonProps={{ style: { display: "none" } }}
            destroyOnHidden
            focusable={{ trap: credential !== null, focusTriggerAfterClose: false }}
            afterClose={() => {
              setDisplayName("");
              (credentialReturnFocus.current?.isConnected ? credentialReturnFocus.current : opener.current)?.focus();
              credentialReturnFocus.current = null;
            }}
            onOk={() => { setCredential(null); setCredentialError(null); }}
            onCancel={() => { setCredential(null); setCredentialError(null); }}
          >
            <Flex vertical gap={token.paddingXS}>
              <Typography.Paragraph>
                Copy this credential now. It authenticates the emitter and will not be shown again.
              </Typography.Paragraph>
              {credentialError ? <Alert type="error" showIcon title={credentialError} /> : null}
              <Input.TextArea readOnly aria-label="Event source credential" value={credential ?? ""} rows={3} />
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
