import { Button, Tooltip, Typography } from 'antd';
import { DownloadOutlined, FileOutlined } from '@ant-design/icons';
import type { FileRef } from '../filePreview/fileSource';
import { FilePreviewButton } from '../filePreview/FilePreview';

/** Shared file presentation for sent attachments and generated artifacts. */
export function ChatFileCard({
  displayName,
  description,
  fileRef,
  downloading = false,
  downloadFailed = false,
  metadataFailed = false,
  onDownload,
  onRetryMetadata,
  onReadLost,
}: {
  displayName?: string;
  description?: string;
  fileRef?: FileRef;
  downloading?: boolean;
  downloadFailed?: boolean;
  metadataFailed?: boolean;
  onDownload: () => void;
  onRetryMetadata?: () => void;
  onReadLost?: () => void;
}) {
  return (
    <div
      className="file-chip artifact-card"
      role="group"
      aria-label={displayName ?? 'File'}
      aria-busy={downloading || (!displayName && !metadataFailed)}
    >
      <FileOutlined aria-hidden />
      <div className="artifact-details">
        {displayName ? (
          <>
            <Tooltip title={displayName} trigger={['hover', 'focus']}>
              <Typography.Text
                className="artifact-filename"
                title={displayName}
                tabIndex={0}
              >
                {displayName}
              </Typography.Text>
            </Tooltip>
            <Typography.Text type="secondary" className="artifact-description">
              {description}
            </Typography.Text>
            {downloadFailed && (
              <Typography.Text type="danger" role="status">
                Download failed. Try again.
              </Typography.Text>
            )}
          </>
        ) : (
          <Typography.Text type="secondary" role="status">
            {metadataFailed ? 'File unavailable' : 'Preparing file…'}
          </Typography.Text>
        )}
      </div>
      <div className="artifact-actions">
        {displayName && fileRef && (
          <FilePreviewButton
            file={fileRef}
            iconOnly
            label={`Preview ${displayName}`}
            onReadLost={onReadLost}
          />
        )}
        {displayName ? (
          <Tooltip
            title={
              downloading
                ? 'Downloading…'
                : downloadFailed
                  ? 'Retry download'
                  : 'Download'
            }
          >
            <Button
              type="text"
              icon={<DownloadOutlined />}
              loading={downloading}
              disabled={downloading}
              aria-label={`${downloadFailed ? 'Retry download' : 'Download'} ${displayName}`}
              onClick={onDownload}
            />
          </Tooltip>
        ) : metadataFailed && onRetryMetadata ? (
          <Button type="text" aria-label="Retry file" onClick={onRetryMetadata}>
            Retry
          </Button>
        ) : null}
      </div>
    </div>
  );
}
