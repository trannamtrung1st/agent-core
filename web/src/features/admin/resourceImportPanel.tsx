import { useState } from "react";
import { Alert, App, Button, Typography } from "antd";
import { ResourceFilePicker, ResourceImportPreview } from "./ResourceImportFields";
import {
  bindAdminDraftResources,
  uploadAdminDraftResourceContent,
  type AdminBindDraftResourceItem
} from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";
import { showAdminFailure } from "./adminFailure";
import {
  createPreviewItem,
  resourceBatchLimitProblem,
  resourcePreviewProblem,
  type ResourcePreviewItem
} from "./resourcePreview";

export function ResourceImportPanel({
  draftId,
  expectedRevision,
  existingPaths,
  existingCount,
  existingBytes,
  disabled,
  onBusyChange,
  onBound,
  onError
}: {
  draftId: string;
  expectedRevision: number;
  existingPaths: string[];
  existingCount: number;
  existingBytes: number;
  disabled: boolean;
  onBusyChange?: (busy: boolean) => void;
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

  const bindResources = async () => {
    if (disabled || binding || blocked) {
      return;
    }
    setBinding(true);
    onBusyChange?.(true);
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
      onBusyChange?.(false);
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
      onBusyChange?.(false);
    }
  };

  return (
    <section className="admin-draft-form-section" aria-label="Import resources">
      <Typography.Title level={5}>Import files or a folder</Typography.Title>
      <Typography.Paragraph type="secondary">
        Preview the path, kind, and size, correct them, then bind the whole set in one draft revision.
      </Typography.Paragraph>
      <ResourceFilePicker disabled={disabled || binding} onFiles={addFiles} onError={onError} />
      {items.length > 0 && <ResourceImportPreview items={items} existingPaths={existingPaths} disabled={disabled || binding} onChange={setItems} />}
      {limitProblem ? <Alert type="warning" showIcon title={limitProblem} /> : null}
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
