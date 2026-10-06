import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, App, Button, Empty, Flex, Spin, Table, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { deleteAgentWorkspaceItem, downloadAgentWorkspaceItem, listAgentWorkspace,
  type AgentWorkspaceItem, type AgentWorkspacePage } from "../../services/agentWorkspace";
import { artifactKind, artifactSize } from "../../services/artifacts";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";

export function InstanceWorkspaceSection({ instanceId, archived }: { instanceId: string; archived: boolean }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [page, setPage] = useState<AgentWorkspacePage | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState<string | null>(null);
  const identity = useRef(instanceId); identity.current = instanceId;
  const order = useRef(0); const download = useRef<AbortController | null>(null);
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const reload = useCallback(async (afterPath?: string) => {
    const generation = ++order.current; setLoading(true); setError(null);
    try {
      const result = await listAgentWorkspace(instanceId, afterPath);
      if (generation === order.current) setPage(previous => afterPath && previous ? { ...result, items: [...previous.items, ...result.items] } : result);
    } catch (reason) {
      if (generation === order.current) setError(describeAdminError(reason, "Unable to load workspace. Retry to reload the workspace."));
    } finally { if (generation === order.current) setLoading(false); }
  }, [instanceId]);
  useEffect(() => {
    setPage(null); setBusy(null); setSearch(""); void reload();
    return () => { order.current++; download.current?.abort(); };
  }, [reload, setSearch]);

  async function remove(item: AgentWorkspaceItem) {
    if (busy || archived) return;
    const generation = ++order.current; setBusy(item.itemId); setError(null);
    try {
      await deleteAgentWorkspaceItem(instanceId, item);
      if (generation === order.current) await reload();
    } catch (reason) {
      if (generation === order.current) setError(describeAdminError(reason, "The item changed or could not be deleted. Reload before trying again."));
    } finally { if (identity.current === instanceId) setBusy(null); }
  }
  async function save(item: AgentWorkspaceItem) {
    if (busy) return;
    const generation = order.current; const controller = new AbortController(); download.current = controller;
    setBusy(item.itemId); setError(null);
    try { await downloadAgentWorkspaceItem(instanceId, item, controller.signal); }
    catch (reason) { if (!controller.signal.aborted && generation === order.current) setError(describeAdminError(reason, "Download failed. Try again.")); }
    finally { if (generation === order.current) setBusy(null); }
  }
  const items = page?.items.filter(i => `${i.logicalPath} ${i.contentType}`.toLocaleLowerCase().includes(search.toLocaleLowerCase())) ?? [];
  return <section className="admin-definition-panel" aria-label="Agent Workspace">
    <div className="admin-definition-panel-heading">
      <Typography.Title level={4}>Agent Workspace</Typography.Title>
      <Typography.Text type="secondary">/home · Durable across sessions.</Typography.Text>
    </div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {archived ? <Alert type="info" showIcon title="Archived workspace is read-only" description="Files remain available to download." /> : null}
      <Flex justify="space-between" align="center" wrap gap={token.paddingXS}>
        <Typography.Text type="secondary">{page ? `${artifactSize(page.usedBytes)} of ${artifactSize(page.maxInstanceBytes)} · ${page.totalItems} ${page.totalItems === 1 ? "item" : "items"}` : ""}</Typography.Text>
        <Button disabled={loading || !!busy} onClick={() => void reload()}>Reload workspace</Button>
      </Flex>
      {error ? <Alert type="error" showIcon title={error.message} action={<Button onClick={() => void reload()}>Retry workspace</Button>}
        description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null}
      {loading && !page ? <Spin aria-label="Loading workspace" /> : null}
      {page ? <>
        <AdminCollectionToolbar value={search} onChange={setSearch} label="workspace items" />
        <Table<AgentWorkspaceItem> size="small" rowKey="itemId" loading={loading} dataSource={items} pagination={pagination} scroll={{ x: 840 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? "No matching items" : "No workspace items yet"} /> }}
          columns={[
            { title: "Path", dataIndex: "logicalPath", ellipsis: true, width: 260, render: (value: string) => <span title={value}>{value}</span> },
            { title: "Type", dataIndex: "contentType", width: 100, render: (value: string, item) => <span title={value}>{item.directory ? "Folder" : artifactKind(value)}</span> },
            { title: "Size", dataIndex: "byteSize", align: "right", width: 90, render: artifactSize },
            { title: "Updated", dataIndex: "updatedAt", width: 165, render: (value: string) => <time dateTime={value}>{new Date(value).toLocaleString()}</time> },
            { title: "Source", dataIndex: "sourceSessionId", ellipsis: true, width: 140, render: (value: string | null) => value ? <span title={`Conversation ${value}`}>Conversation {value}</span> : "—" },
            { title: "Actions", width: 180, render: (_, item) => <Flex wrap gap={token.paddingXS}>
              <Button aria-label={`Download ${item.logicalPath}`} disabled={!!busy || item.directory} onClick={() => void save(item)}>Download</Button>
              <Button danger aria-label={`Delete ${item.logicalPath}`} disabled={archived || !!busy || loading} onClick={() => confirmAction(modal, {
                title: item.directory ? "Delete empty folder?" : "Delete workspace file?", content: `Delete ${item.logicalPath} from this agent's workspace. Existing conversation Artifacts are kept.`,
                okText: item.directory ? "Delete folder" : "Delete file", danger: true, onOk: () => remove(item)
              })}>Delete</Button>
            </Flex> }
          ]} />
        {page.nextPath ? <Button disabled={loading || !!busy} onClick={() => void reload(page.nextPath!)}>Load more items</Button> : null}
      </> : null}
    </Flex></div>
  </section>;
}
