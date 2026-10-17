import { Alert, Button, Descriptions, Flex, Form, Input, Select, Switch, Typography, Upload, theme } from 'antd';
import { UploadOutlined } from '@ant-design/icons';
import type { InstanceResource } from '../../services/instanceConfiguration';
import { RESOURCE_FILE_HELP, RESOURCE_KINDS } from './resourcePreview';
import { useAdminDetailLayout } from './useAdminDetailLayout';

export type InstanceResourceDraft = { logicalPath: string; kind: number; enabled: boolean; file: File | null };
export const RESOURCE_COPY_DESCRIPTION = 'Create an independent local file with source provenance. The inherited source keeps its current enabled choice, and future source changes do not update this copy.';
export function InstanceResourceFields({ resource, value, disabled, onChange, problem }: { resource: InstanceResource; value: InstanceResourceDraft; disabled: boolean; onChange: (patch: Partial<InstanceResourceDraft>) => void; problem?: string | null }) {
  const { token } = theme.useToken();
  return <Form layout="vertical"><Flex vertical gap={token.padding}>
    <Typography.Text type="secondary">Update the path, kind or availability. Changes apply to the next Run.</Typography.Text>
    <Form.Item label="Logical path" style={{ margin: 0 }} extra="A relative path, such as references/guide.pdf."><Input aria-label="Resource logical path" value={value.logicalPath} disabled={disabled} onChange={event => onChange({ logicalPath: event.target.value })} /></Form.Item>
    <Flex gap={token.padding} wrap>
      <Form.Item label="Kind" style={{ margin: 0, flex: '1 1 220px' }}><Select aria-label="Resource kind" value={value.kind} options={RESOURCE_KINDS.slice(0, 4).map((name, index) => ({ value: index + 1, label: name === 'StaticAsset' ? 'Static asset' : name }))} disabled={disabled} onChange={kind => onChange({ kind })} /></Form.Item>
      <Form.Item label="Availability" style={{ margin: 0 }}><Flex align="center" gap={token.paddingXS} style={{ minHeight: token.controlHeight }}><Switch aria-label="Instance resource enabled" checked={value.enabled} disabled={disabled} onChange={enabled => onChange({ enabled })} /><Typography.Text>{value.enabled ? 'Enabled' : 'Disabled'}</Typography.Text></Flex></Form.Item>
    </Flex>
    <Flex vertical gap={token.paddingXS}>
      <Typography.Text strong>Content</Typography.Text><Typography.Text className="admin-instance-id">{resource.logicalPath.split('/').at(-1)} · {resource.byteLength.toLocaleString()} bytes</Typography.Text>
      <Typography.Text type="secondary">{value.file ? 'The selected file will replace the current content.' : 'Current content is kept unless you choose a replacement.'}</Typography.Text>
      <Upload.Dragger className="admin-resource-upload" maxCount={1} showUploadList={false} disabled={disabled} beforeUpload={file => { onChange({ file }); return false; }}>
        <p className="ant-upload-drag-icon"><UploadOutlined /></p><p className="ant-upload-text">Choose a replacement file</p><p className="ant-upload-hint">Or drop one file here · up to 8 MiB</p>
      </Upload.Dragger>
      {value.file && <Flex gap={token.paddingXS} align="center" justify="space-between"><Typography.Text className="admin-instance-id">{value.file.name} · {value.file.size.toLocaleString()} bytes</Typography.Text><Button type="text" disabled={disabled} onClick={() => onChange({ file: null })}>Keep current file</Button></Flex>}
      <Typography.Text type="secondary">Knowledge requires a supported text or data format. {RESOURCE_FILE_HELP}</Typography.Text>
    </Flex>
    {problem && <Alert type="error" showIcon title={problem} />}
  </Flex></Form>;
}
export function InstanceResourceInspector({ resource }: { resource: InstanceResource }) {
  const detailLayout = useAdminDetailLayout();
  const text = (value: string) => <Typography.Text className="admin-instance-id">{value}</Typography.Text>;
  return <Descriptions {...detailLayout} column={1} size="small" className="admin-effective-details" items={[
    { key: 'identity', label: 'Resource key', children: text(resource.key) },
    { key: 'path', label: 'Run path', children: text(resource.virtualPath) },
    { key: 'hash', label: 'Content SHA-256', children: <Typography.Text copyable className="admin-instance-id">{resource.contentSha256}</Typography.Text> },
    { key: 'revision', label: 'Revision', children: resource.revision },
    { key: 'dependencies', label: 'Enabled Skill dependencies', children: text(resource.dependencies.join(', ') || 'None') },
    ...(resource.sourceDefinitionId ? [{ key: 'source', label: 'Copy provenance', children: text(`Definition ${resource.sourceDefinitionId} · v${resource.sourceDefinitionVersion} · resource ${resource.sourceDefinitionResourceId ?? 'recorded source'}`) }] : [])
  ]} />;
}
