import { DeleteOutlined, DownloadOutlined, FolderOpenOutlined } from '@ant-design/icons';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { AdminErrorNotice } from './adminFailure';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  App,
  Button,
  Breadcrumb,
  Empty,
  Flex,
  Spin,
  Table,
  Tooltip,
  Typography,
  theme,
} from 'antd';
import { confirmAction } from '../../app/confirmAction';
import {
  deleteAgentWorkspaceItem,
  downloadAgentWorkspaceItem,
  listAgentWorkspace,
  type AgentWorkspaceItem,
  type AgentWorkspacePage,
} from '../../services/agentWorkspace';
import { artifactKind, artifactSize } from '../../services/artifacts';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import {
  AdminCollectionToolbar,
  useAdminCollectionSearch,
} from './AdminCollectionToolbar';

import { FilePreviewButton } from '../filePreview/FilePreview';

export function InstanceWorkspaceSection({
  instanceId,
  archived,
}: {
  instanceId: string;
  archived: boolean;
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [prefix, setPrefix] = useState('/home');
  const [page, setPage] = useState<AgentWorkspacePage | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const identity = useRef(instanceId);
  identity.current = instanceId;
  const order = useRef(0);
  const download = useRef<AbortController | null>(null);
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const reload = useCallback(
    async (afterPath?: string) => {
      const generation = ++order.current;
      setLoading(true);
      setError(null);
      try {
        const result = await listAgentWorkspace(instanceId, afterPath, prefix);
        if (generation === order.current)
          setPage((previous) =>
            afterPath && previous
              ? { ...result, items: [...new Map([...previous.items, ...result.items].map(item => [item.logicalPath, item])).values()] }
              : result,
          );
      } catch (reason) {
        if (generation === order.current)
          setError(
            describeAdminError(
              reason,
              'Unable to load workspace. Retry to reload the workspace.',
            ),
          );
      } finally {
        if (generation === order.current) setLoading(false);
      }
    },
    [instanceId, prefix],
  );
  useEffect(() => { setPrefix('/home'); }, [instanceId]);
  useEffect(() => {
    setPage(null);
    setBusy(null);
    setSearch('');
    void reload();
    return () => {
      order.current++;
      download.current?.abort();
    };
  }, [reload, setSearch]);

  async function remove(item: AgentWorkspaceItem) {
    if (busy || archived) return;
    const generation = ++order.current;
    setBusy(item.logicalPath);
    setError(null);
    try {
      await deleteAgentWorkspaceItem(instanceId, item);
      if (generation === order.current) await reload();
    } catch (reason) {
      if (generation === order.current)
        setError(
          describeAdminError(
            reason,
            'The current path could not be deleted. Reload before trying again.',
          ),
        );
    } finally {
      if (identity.current === instanceId) setBusy(null);
    }
  }
  async function save(item: AgentWorkspaceItem) {
    if (busy) return;
    const generation = order.current;
    const controller = new AbortController();
    download.current = controller;
    setBusy(item.logicalPath);
    setError(null);
    try {
      await downloadAgentWorkspaceItem(instanceId, item, controller.signal);
    } catch (reason) {
      if (!controller.signal.aborted && generation === order.current)
        setError(describeAdminError(reason, 'Download failed. Try again.'));
    } finally {
      if (generation === order.current) setBusy(null);
    }
  }
  const items =
    page?.items.filter((i) =>
      `${i.logicalPath} ${i.contentType}`
        .toLocaleLowerCase()
        .includes(search.toLocaleLowerCase()),
    ) ?? [];
  return (
    <AgentConfigurationPanel
      title="Agent Workspace"
      label="Agent Workspace"
      description="/home · Live files shared by this Instance’s Sessions. Changes are visible immediately."
      bodyGap="section"
    >
      {archived ? (
        <Alert
          type="info"
          showIcon
          title="Archived workspace is read-only"
          description="Files remain available to download."
        />
      ) : null}
      <Flex justify="space-between" align="center" wrap gap={token.paddingXS}>
        <Typography.Text type="secondary">
          {page
            ? `${page.usedBytes === null ? 'Usage unknown' : artifactSize(page.usedBytes)} · ${artifactSize(page.maxInstanceBytes)} sampled budget${page.totalItems === null ? '' : ` · ${page.totalItems} items`}`
            : ''}
        </Typography.Text>
        <Button disabled={loading || !!busy} onClick={() => void reload()}>
          Reload workspace
        </Button>
      </Flex>
      {error ? (
        <Alert
          type="error"
          showIcon
          title={
            <AdminErrorNotice
              message={error.message}
              diagnosticId={error.diagnosticId}
              showDetailsLabel
            />
          }
          action={
            <Button onClick={() => void reload()}>Retry workspace</Button>
          }
        />
      ) : null}
      {loading && !page ? <Spin aria-label="Loading workspace" /> : null}
      <Breadcrumb aria-label="Workspace directory" items={prefix.split('/').filter(Boolean).map((part, index, parts) => ({
        title: index === parts.length - 1 ? part : <Button type="text" size="small" style={{paddingInline: token.paddingXS}} onClick={() => setPrefix('/' + parts.slice(0, index + 1).join('/'))}>{part}</Button>,
      }))} />
      {page?.usageEnforcement === 'unknown' && <Alert type="warning" showIcon title="Storage usage could not be measured" description="Reload to recheck. Current paths remain available; storage limits use sampled observations." />}
      {page ? (
        <>
          <AdminCollectionToolbar
            value={search}
            onChange={setSearch}
            label="workspace items"
          />
          <Typography.Text type="secondary">Search filters loaded paths in this folder. Reload to observe current files.</Typography.Text>
          <Table<AgentWorkspaceItem>
            className="admin-collection-table"
            size="small"
            rowKey="logicalPath"
            loading={loading}
            dataSource={items}
            pagination={pagination}
            scroll={{ x: 740 }}
            locale={{
              emptyText: (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={
                    search ? 'No matching items' : 'No workspace items yet'
                  }
                />
              ),
            }}
            columns={[
              {
                title: 'Path',
                dataIndex: 'logicalPath',
                ellipsis: true,
                width: 260,
                render: (value: string) => <span title={value}>{value}</span>,
              },
              {
                title: 'Type',
                dataIndex: 'contentType',
                width: 100,
                render: (value: string, item) => (
                  <span title={value}>
                    {item.directory ? 'Folder' : item.kind === 'symlink' ? 'Symbolic link' : item.kind === 'special' ? 'Special file' : artifactKind(value)}
                  </span>
                ),
              },
              {
                title: 'Size',
                dataIndex: 'byteSize',
                align: 'right',
                width: 90,
                render: artifactSize,
              },
              {
                title: 'Updated',
                dataIndex: 'updatedAt',
                width: 165,
                render: (value: string) => (
                  <time dateTime={value}>
                    {new Date(value).toLocaleString()}
                  </time>
                ),
              },
              {
                title: 'Actions',
                width: 152,
                render: (_, item) => (
                  <Flex className="admin-table-actions" gap={token.paddingXS}>
                    {item.directory && <Tooltip title="Open folder"><Button icon={<FolderOpenOutlined aria-hidden />} aria-label={`Open ${item.logicalPath}`} disabled={loading || !!busy} onClick={() => setPrefix(item.logicalPath)} /></Tooltip>}
                    {!item.directory && item.kind !== 'special' && (
                      <FilePreviewButton
                        iconOnly
                        file={{
                          kind: 'home',
                          instanceId,
                          path: item.logicalPath,
                          metadata: {
                            displayName: item.logicalPath.split('/').pop()!,
                            contentType: item.contentType,
                            byteSize: item.byteSize,
                          },
                        }}
                      />
                    )}
                    <Tooltip title="Download">
                      <Button
                        icon={<DownloadOutlined aria-hidden />}
                        aria-label={`Download ${item.logicalPath}`}
                        disabled={!!busy || item.directory || item.kind === 'special'}
                        onClick={() => void save(item)}
                      />
                    </Tooltip>
                    <Tooltip title="Delete">
                      <Button
                        icon={<DeleteOutlined aria-hidden />}
                        danger
                        aria-label={`Delete ${item.logicalPath}`}
                        disabled={archived || !!busy || loading}
                        onClick={() =>
                          confirmAction(modal, {
                            title: item.directory
                              ? 'Delete empty folder?'
                              : 'Delete workspace file?',
                            content: `Delete ${item.logicalPath} from this agent's workspace. Existing conversation Artifacts are kept.`,
                            okText: item.directory
                              ? 'Delete folder'
                              : 'Delete file',
                            danger: true,
                            onOk: () => remove(item),
                          })
                        }
                      />
                    </Tooltip>
                  </Flex>
                ),
              },
            ]}
          />
          {page.nextPath ? (
            <Button
              disabled={loading || !!busy}
              onClick={() => void reload(page.nextPath!)}
            >
              Load more items
            </Button>
          ) : null}
        </>
      ) : null}
    </AgentConfigurationPanel>
  );
}
