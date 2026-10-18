import { DeleteOutlined, DownloadOutlined, FolderOpenOutlined } from '@ant-design/icons';
import { Button, Empty, Flex, Grid, Table, Tooltip, Typography, theme, type TableProps } from 'antd';
import type { AgentWorkspaceItem } from '../../services/agentWorkspace';
import { artifactKind, artifactSize } from '../../services/artifacts';
import { FilePreviewButton } from '../filePreview/FilePreview';
import type { FileRef } from '../filePreview/fileSource';

/** Current-file presentation shared by Instance home and Session working inspectors. */
export function WorkspaceFileTable({ items, loading, busy, canDelete = true, deletionDisabled = false, compact = false, emptyText, pagination,
  previewFile, onOpen, onDownload, onRemove, onReadLost }: {
  items: AgentWorkspaceItem[]; loading: boolean; busy: boolean; canDelete?: boolean; deletionDisabled?: boolean; compact?: boolean; emptyText: string;
  pagination?: TableProps<AgentWorkspaceItem>['pagination']; previewFile: (item: AgentWorkspaceItem) => FileRef;
  onOpen: (path: string) => void; onDownload: (item: AgentWorkspaceItem) => void; onRemove: (item: AgentWorkspaceItem) => void; onReadLost?: () => void;
}) {
  const { token } = theme.useToken(); const screens = Grid.useBreakpoint(); const wide = !!screens.md && !compact;
  const kind = (item: AgentWorkspaceItem) => item.directory ? 'Folder' : item.kind === 'symlink' ? 'Symbolic link' : item.kind === 'special' ? 'Special file' : artifactKind(item.contentType);
  return <Table<AgentWorkspaceItem> className="admin-collection-table" size="small" rowKey="logicalPath" loading={loading}
    dataSource={items} pagination={pagination} scroll={wide ? { x: 740 } : undefined}
    locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyText} /> }} columns={[
      { title: wide ? 'Path' : 'File', width: wide ? 260 : undefined, render: (_, item) => <Flex vertical gap={token.paddingXXS}>
        <span title={item.logicalPath} style={{ overflowWrap: 'anywhere' }}>{item.logicalPath}</span>
        {!wide && <Typography.Text type="secondary"><span>{kind(item)}</span> · {artifactSize(item.byteSize)}</Typography.Text>}
      </Flex> },
      ...(wide ? [
        { title: 'Type', width: 100, render: (_: unknown, item: AgentWorkspaceItem) => <span title={item.contentType}>{kind(item)}</span> },
        { title: 'Size', width: 90, render: (_: unknown, item: AgentWorkspaceItem) => artifactSize(item.byteSize) },
        { title: 'Updated', width: 165, render: (_: unknown, item: AgentWorkspaceItem) => <time dateTime={item.updatedAt}>{new Date(item.updatedAt).toLocaleString()}</time> },
      ] : []),
      { title: 'Actions', width: wide ? 152 : 120, render: (_, item) => <Flex className="admin-table-actions" gap={token.paddingXS}>
        {item.directory && <Tooltip title="Open folder"><Button icon={<FolderOpenOutlined aria-hidden />} aria-label={`Open ${item.logicalPath}`} disabled={loading || busy} onClick={() => onOpen(item.logicalPath)} /></Tooltip>}
        {!item.directory && item.kind !== 'special' && <FilePreviewButton iconOnly file={previewFile(item)} disabled={loading || busy} onReadLost={onReadLost} />}
        <Tooltip title="Download"><Button icon={<DownloadOutlined aria-hidden />} aria-label={`Download ${item.logicalPath}`} disabled={loading || busy || item.directory || item.kind === 'special'} onClick={() => onDownload(item)} /></Tooltip>
        {canDelete && <Tooltip title="Delete"><Button danger icon={<DeleteOutlined aria-hidden />} aria-label={`Delete ${item.logicalPath}`} disabled={deletionDisabled || busy || loading} onClick={() => onRemove(item)} /></Tooltip>}
      </Flex> },
    ]} />;
}
