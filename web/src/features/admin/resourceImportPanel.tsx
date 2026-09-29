import { useState } from "react";
import { Alert, App, Button, Flex, Input, Select, Typography, Upload } from "antd";
import { InboxOutlined } from "@ant-design/icons";
import {
  bindAdminDraftResources,
  uploadAdminDraftResourceContent,
  type AdminBindDraftResourceItem
} from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";
import { showAdminFailure } from "./adminFailure";
import {
  RESOURCE_KINDS,
  createPreviewItem,
  inferResourceKind,
  readDroppedResourceFiles,
  resourceBatchLimitProblem,
  resourceMediaType,
  resourcePreviewProblem,
  type ResourceKindName,
  type ResourcePreviewItem
} from "./resourcePreview";

export function ResourceImportPanel({
  draftId,
  expectedRevision,
  existingPaths,
  existingCount,
  existingBytes,
  disabled,
  onBound,
  onError
}: {
  draftId: string;
  expectedRevision: number;
  existingPaths: string[];
  existingCount: number;
  existingBytes: number;
  disabled: boolean;
  onBound: () => Promise<void> | void;
  onError: (message: string | null, diagnosticId?: string | null) => void;
}) {
  const { message } = App.useApp();
  const [items, setItems] = useState<ResourcePreviewItem[]>([]);
  const [binding, setBinding] = useState(false);
  const limitProblem = resourceBatchLimitProblem(items, existingCount, existingBytes);
  const itemProblems = items.map((item) =>
    resourcePreviewProblem(
      item,
      items.map((candidate) => candidate.logicalPath),
      existingPaths
    )
  );
  const blocked = items.length === 0 || limitProblem !== null || itemProblems.some((problem) => problem !== null);

  const addFiles = (files: File[]) => {
    if (files.length === 0) {
      return;
    }
    setItems((current) => [...current, ...files.map(createPreviewItem)]);
  };

  const updateItem = (key: string, patch: Partial<ResourcePreviewItem>) => {
    setItems((current) => current.map((item) => (item.key === key ? { ...item, ...patch } : item)));
  };

  const bindResources = async () => {
    if (blocked) {
      return;
    }
    setBinding(true);
    onError(null);
    const next = [...items];
    let uploadFailed = false;
    let uploadFailure: { message: string; diagnosticId?: string } | null = null;
    for (let index = 0; index < next.length; index += 1) {
      const item = next[index];
      if (item.contentSha256 || !item.mediaType) {
        continue;
      }
      try {
        const stored = await uploadAdminDraftResourceContent(draftId, item.file, item.mediaType);
        next[index] = {
          ...item,
          contentSha256: stored.contentSha256,
          byteLength: stored.byteLength,
          mediaType: stored.mediaType,
          uploadError: undefined
        };
      } catch (error) {
        uploadFailed = true;
        uploadFailure = describeAdminError(error, "Resource upload failed.");
        next[index] = {
          ...item,
          uploadError: uploadFailure.message
        };
      }
    }
    setItems(next);
    if (uploadFailed) {
      message.error("One or more files failed to upload. Fix those items and bind again.");
      if (uploadFailure?.diagnosticId) {
        onError(uploadFailure.message, uploadFailure.diagnosticId);
      }
      setBinding(false);
      return;
    }

    try {
      const payload: AdminBindDraftResourceItem[] = next.map((item) => ({
        logicalPath: item.logicalPath.trim(),
        kind: item.kind,
        mediaType: item.mediaType ?? "",
        contentSha256: item.contentSha256 ?? "",
        byteLength: item.byteLength
      }));
      await bindAdminDraftResources(draftId, expectedRevision, payload);
      setItems([]);
      message.success("Resources bound.");
      await onBound();
    } catch (error) {
      const notice = showAdminFailure(message, error, "Resource bind failed.");
      onError(notice.message, notice.diagnosticId ?? null);
    } finally {
      setBinding(false);
    }
  };

  return (
    <section className="admin-draft-form-section" aria-label="Import resources">
      <Typography.Title level={5}>Import files or a folder</Typography.Title>
      <Typography.Paragraph type="secondary">
        Preview the path, kind, and size, correct them, then bind the whole set in one draft revision.
      </Typography.Paragraph>
      <Flex gap={8} wrap="wrap">
        <Upload
          multiple
          showUploadList={false}
          disabled={disabled || binding}
          beforeUpload={(file) => {
            addFiles([file]);
            return false;
          }}
        >
          <Button aria-label="Choose resource files" disabled={disabled || binding}>
            Choose files
          </Button>
        </Upload>
        <Upload
          directory
          multiple
          showUploadList={false}
          disabled={disabled || binding}
          beforeUpload={(file) => {
            addFiles([file]);
            return false;
          }}
        >
          <Button aria-label="Choose resource folder" disabled={disabled || binding}>
            Choose folder
          </Button>
        </Upload>
      </Flex>
      <Typography.Text type="secondary" className="admin-draft-field-hint">
        The selected folder is the package root. Kind comes from the top-level directory inside it, such as knowledge or templates.
      </Typography.Text>
      <div
        className="admin-resource-drop"
        aria-label="Drop resource files"
        onDragOver={(event) => event.preventDefault()}
        onDrop={(event) => {
          event.preventDefault();
          if (disabled || binding) {
            return;
          }
          void readDroppedResourceFiles(event.dataTransfer).then(addFiles);
        }}
      >
        <InboxOutlined />
        <Typography.Text>Drop files or a folder</Typography.Text>
        <Typography.Text type="secondary">
          Folder drop keeps relative paths when the browser exposes them.
        </Typography.Text>
      </div>
      {items.length > 0 ? (
        <div className="admin-resource-preview">
          {items.map((item, index) => (
            <div key={item.key} className="admin-resource-preview-row">
              <label className="admin-draft-field">
                <Typography.Text>Path</Typography.Text>
                <Input
                  aria-label={`Imported resource path ${index + 1}`}
                  value={item.logicalPath}
                  disabled={disabled || binding}
                  onChange={(event) => {
                    const logicalPath = event.target.value;
                    updateItem(item.key, {
                      logicalPath,
                      kind: item.kind || inferResourceKind(logicalPath) || "",
                      mediaType: resourceMediaType(item.file, logicalPath)
                    });
                  }}
                />
              </label>
              <label className="admin-draft-field">
                <Typography.Text>Kind</Typography.Text>
                <Select
                  aria-label={`Imported resource kind ${index + 1}`}
                  value={RESOURCE_KINDS.find((kind) => kind === item.kind)}
                  placeholder="Choose kind"
                  disabled={disabled || binding}
                  options={RESOURCE_KINDS.map((value) => ({ value, label: value }))}
                  onChange={(kind: ResourceKindName) => updateItem(item.key, { kind })}
                />
              </label>
              <div className="admin-resource-preview-meta">
                <Typography.Text type="secondary">{item.byteLength.toLocaleString()} bytes</Typography.Text>
                {itemProblems[index] ? (
                  <Typography.Text type="danger">{itemProblems[index]}</Typography.Text>
                ) : (
                  <Typography.Text type="secondary">Ready</Typography.Text>
                )}
                <Button
                  type="text"
                  danger
                  aria-label={`Remove imported resource ${index + 1}`}
                  disabled={disabled || binding}
                  onClick={() => setItems((current) => current.filter((candidate) => candidate.key !== item.key))}
                >
                  Remove
                </Button>
              </div>
            </div>
          ))}
        </div>
      ) : null}
      {limitProblem ? <Alert type="error" showIcon title={limitProblem} /> : null}
      <Button
        type="primary"
        aria-label="Bind imported resources"
        disabled={disabled || binding || blocked}
        onClick={() => void bindResources()}
      >
        Bind resources
      </Button>
    </section>
  );
}
