import { useId, useState } from "react";
import { Button, Flex, Grid, Input, Popover, Select, Table, Tooltip, Typography, Upload, theme } from "antd";
import { DeleteOutlined, FolderOpenOutlined, InboxOutlined, InfoCircleOutlined, UploadOutlined } from "@ant-design/icons";
import {
  RESOURCE_FILE_HELP, RESOURCE_KINDS, inferResourceKind, readDroppedResourceFiles, resourceMediaType,
  resourcePathProblem, resourceFileProblem, resourceKindProblem,
  type ResourceKindName, type ResourcePreviewItem
} from "./resourcePreview";

export function ResourceFilePicker({ disabled, onFiles, onError }: {
  disabled: boolean;
  onFiles: (files: File[]) => void;
  onError: (message: string) => void;
}) {
  const { token } = theme.useToken();
  return <Flex vertical gap={token.paddingXS}>
    <Flex gap={token.paddingXS} wrap>
      <Upload multiple showUploadList={false} disabled={disabled} beforeUpload={file => { onFiles([file]); return false; }}>
        <Button aria-label="Choose resource files" icon={<UploadOutlined />} disabled={disabled}>Choose files</Button>
      </Upload>
      <Upload directory multiple showUploadList={false} disabled={disabled} beforeUpload={file => { onFiles([file]); return false; }}>
        <Button aria-label="Choose resource folder" icon={<FolderOpenOutlined />} disabled={disabled}>Choose folder</Button>
      </Upload>
    </Flex>
    <div className="admin-resource-drop" aria-label="Drop resource files" aria-disabled={disabled}
      onDragOver={event => event.preventDefault()}
      onDrop={event => {
        event.preventDefault();
        if (!disabled) void readDroppedResourceFiles(event.dataTransfer).then(onFiles).catch(() => onError("Unable to read the dropped files. Try Choose files or Choose folder."));
      }}>
      <InboxOutlined />
      <Typography.Text>Drop files or a folder</Typography.Text>
      <Typography.Text type="secondary">The selected folder is the package root. Paths inside it are preserved.</Typography.Text>
    </div>
    <Flex gap={token.paddingXS} wrap align="center">
      <Typography.Text type="secondary">Up to 8 MiB per file.</Typography.Text>
      <Popover trigger="click" title="Supported resource files" content={<Typography.Paragraph style={{ maxWidth: 280, margin: 0 }}>{RESOURCE_FILE_HELP}</Typography.Paragraph>}>
        <Button type="text" size="small" icon={<InfoCircleOutlined />}>Supported file types</Button>
      </Popover>
    </Flex>
    <Typography.Text type="secondary">Folders such as knowledge, references or templates suggest the kind.</Typography.Text>
  </Flex>;
}

export function ResourceImportPreview({ items, existingPaths, scope = "draft", disabled, onChange }: {
  items: ResourcePreviewItem[];
  existingPaths: string[];
  scope?: string;
  disabled: boolean;
  onChange: (update: (current: ResourcePreviewItem[]) => ResourcePreviewItem[]) => void;
}) {
  const { token } = theme.useToken();
  const compact = !Grid.useBreakpoint().md;
  const id = useId();
  const [touched, setTouched] = useState<Record<string, boolean>>({});
  const touch = (item: ResourcePreviewItem, field: "path" | "kind") =>
    setTouched(current => ({ ...current, [`${item.key}:${field}`]: true }));
  const updateItem = (key: string, patch: Partial<ResourcePreviewItem>) =>
    onChange(current => current.map(item => item.key === key ? { ...item, ...patch, uploadError: undefined } : item));
  const siblingPaths = items.map(item => item.logicalPath);
  const state = (item: ResourcePreviewItem, index: number) => ({
    path: resourcePathProblem(item.logicalPath, siblingPaths, existingPaths, scope),
    kind: resourceKindProblem(item),
    file: resourceFileProblem(item),
    pathTouched: !!touched[`${item.key}:path`],
    kindTouched: !!touched[`${item.key}:kind`],
    pathId: `${id}-path-${index}`,
    kindId: `${id}-kind-${index}`
  });
  const pathField = (item: ResourcePreviewItem, index: number) => {
    const issue = state(item, index);
    const error = issue.pathTouched ? issue.path : null;
    return <Flex vertical gap={token.paddingXS}>
      <Input aria-label={`Imported resource path ${index + 1}`} value={item.logicalPath} disabled={disabled}
        aria-invalid={!!error} aria-describedby={error ? issue.pathId : undefined} status={error ? "error" : undefined}
        onBlur={() => touch(item, "path")}
        onChange={event => {
          const logicalPath = event.target.value;
          updateItem(item.key, { logicalPath, kind: item.kind || inferResourceKind(logicalPath) || "", mediaType: resourceMediaType(item.file, logicalPath) });
        }} />
      {error && <Typography.Text id={issue.pathId} type="danger">{error}</Typography.Text>}
    </Flex>;
  };
  const kindField = (item: ResourcePreviewItem, index: number) => {
    const issue = state(item, index);
    const error = issue.kindTouched ? issue.kind : null;
    return <Flex vertical gap={token.paddingXS} style={{ minWidth: 0 }}>
      <Select aria-label={`Imported resource kind ${index + 1}`} value={RESOURCE_KINDS.find(kind => kind === item.kind)}
        aria-invalid={!!error} aria-describedby={error ? issue.kindId : undefined} status={error ? "error" : undefined}
        placeholder="Choose kind" disabled={disabled} style={{ width: "100%" }} onBlur={() => touch(item, "kind")}
        options={RESOURCE_KINDS.map(value => ({ value, label: value === "StaticAsset" ? "Static asset" : value === "EvalFixture" ? "Evaluation fixture" : value }))}
        onChange={(kind: ResourceKindName) => { touch(item, "kind"); updateItem(item.key, { kind }); }} />
      {error && <Typography.Text id={issue.kindId} type="danger">{error}</Typography.Text>}
    </Flex>;
  };
  const size = (item: ResourcePreviewItem) => <Tooltip title={`${item.byteLength.toLocaleString()} bytes`}>
    <Typography.Text type="secondary" style={{ whiteSpace: "nowrap" }}>{formatResourceSize(item.byteLength)}</Typography.Text>
  </Tooltip>;
  const status = (item: ResourcePreviewItem, index: number) => {
    const issue = state(item, index);
    const label = item.uploadError || issue.file || (issue.path ? "Review path" : issue.kind ? "Choose kind" : "Ready");
    return <Typography.Text type={item.uploadError ? "danger" : "secondary"} style={{ overflowWrap: "anywhere", fontSize: token.fontSizeSM }}>{label}</Typography.Text>;
  };
  const remove = (item: ResourcePreviewItem, index: number) => <Tooltip title={`Remove ${item.file.name}`}>
    <Button type="text" danger icon={<DeleteOutlined />} aria-label={`Remove imported resource ${index + 1}`} disabled={disabled}
      onClick={() => onChange(current => current.filter(candidate => candidate.key !== item.key))} />
  </Tooltip>;
  return <Table<ResourcePreviewItem> className="admin-resource-preview" size="small" rowKey="key" tableLayout="fixed"
    pagination={false} showHeader={!compact} dataSource={items}
    onRow={() => ({ className: "admin-resource-preview-row" })}
    columns={compact ? [{ key: "resource", render: (_, item, index) => <Flex vertical gap={token.paddingXS}>
      {pathField(item, index)}
      <Flex gap={token.paddingXS} align="center"><div style={{ flex: 1, minWidth: 0 }}>{kindField(item, index)}</div>{size(item)}</Flex>
      <Flex justify="space-between" align="center" gap={token.paddingXS}>{status(item, index)}{remove(item, index)}</Flex>
    </Flex> }] : [
      { title: "Path", key: "path", render: (_, item, index) => pathField(item, index) },
      { title: "Kind", key: "kind", width: 180, render: (_, item, index) => kindField(item, index) },
      { title: "Size", key: "size", width: 90, align: "right", render: (_, item) => size(item) },
      { title: "Status", key: "status", width: 140, render: (_, item, index) => status(item, index) },
      { title: "", key: "remove", width: 48, render: (_, item, index) => remove(item, index) }
    ]} />;
}

function formatResourceSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}
