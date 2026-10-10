import { Button, Flex, Typography, theme } from 'antd';
import { DeleteOutlined } from '@ant-design/icons';

/** Resource collections share selection feedback, spacing and destructive actions. */
export function ResourceSelectionToolbar({ count, disabled, busy, onClear, onDelete }: {
  count: number; disabled: boolean; busy: boolean; onClear: () => void; onDelete: () => void;
}) {
  const { token } = theme.useToken();
  return <Flex wrap align="center" gap={token.paddingXS}>
    <Typography.Text type="secondary" role="status">{count ? `${count} selected` : 'Select resources to delete in bulk'}</Typography.Text>
    <Button danger aria-label="Delete selected" icon={<DeleteOutlined />} disabled={disabled || !count} loading={busy} onClick={onDelete}>Delete selected</Button>
    {count > 0 && <Button type="text" disabled={disabled} onClick={onClear}>Clear selection</Button>}
  </Flex>;
}

export function ResourceDeletionContent({ notice, paths }: { notice: string; paths: string[] }) {
  const { token } = theme.useToken();
  return <Flex vertical gap={token.paddingXS}>
    <Typography.Text>{notice}</Typography.Text>
    <Flex vertical gap={token.paddingXS} style={{ maxHeight: 'min(40dvh, 20rem)', overflowY: 'auto' }} tabIndex={0} aria-label="Resources to delete">
      {paths.map(path => <Typography.Text key={path} style={{ overflowWrap: 'anywhere' }}>{path}</Typography.Text>)}
    </Flex>
  </Flex>;
}
