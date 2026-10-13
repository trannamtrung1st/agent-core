import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Empty,
  Flex,
  Modal,
  Slider,
  Spin,
  Tooltip,
  Typography,
  theme,
} from 'antd';
import { DownloadOutlined, EyeOutlined } from '@ant-design/icons';
import { OwnerCapabilityError } from '../../services/api';
import { readOfficePreview, type OfficePreview } from './officeSource';
import { TextPreview } from './TextPreview';
import { artifactSize } from '../../services/artifacts';
import {
  decodeText,
  formatLimit,
  previewFormat,
  previewLimits,
  validateImageDimensions,
  validateSignature,
  type PreviewFormat,
} from './previewPolicy';
import {
  downloadFile,
  PreviewError,
  readFailure,
  readFile,
  resolveFile,
  sourceLabel,
  type FileMetadata,
  type FileRef,
} from './fileSource';
const CsvPreview = lazy(() =>
  import('./CsvPreview').then((module) => ({ default: module.CsvPreview })),
);
const WordPreview = lazy(() =>
  import('./WordPreview').then((module) => ({ default: module.WordPreview })),
);
const SpreadsheetPreview = lazy(() =>
  import('./SpreadsheetPreview').then((module) => ({
    default: module.SpreadsheetPreview,
  })),
);
type Loaded = {
  metadata: FileMetadata;
  format: PreviewFormat;
  text?: string;
  url?: string;
  blob?: Blob;
  office?: OfficePreview;
};
export function FilePreviewButton({
  file,
  disabled = false,
  iconOnly = false,
  label,
}: {
  file: FileRef;
  disabled?: boolean;
  iconOnly?: boolean;
  label?: string;
}) {
  return (
    <PreviewEntry
      key={JSON.stringify(file)}
      file={file}
      disabled={disabled}
      iconOnly={iconOnly}
      label={label}
    />
  );
}
function PreviewEntry({
  file,
  disabled,
  iconOnly,
  label,
}: {
  file: FileRef;
  disabled: boolean;
  iconOnly: boolean;
  label?: string;
}) {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  return (
    <>
      <Tooltip title={iconOnly ? 'Preview' : undefined}>
        <Button
          type="text"
          icon={<EyeOutlined aria-hidden />}
          disabled={disabled}
          aria-label={
            label ??
            `Preview ${'metadata' in file ? file.metadata.displayName : 'file'}`
          }
          onClick={() => setOpen(true)}
        >
          {iconOnly ? null : 'Preview'}
        </Button>
      </Tooltip>
      <FilePreview file={file} open={open} onClose={() => setOpen(false)} />
    </>
  );
}
export function FilePreview({
  file,
  open = true,
  onClose,
}: {
  file: FileRef;
  open?: boolean;
  onClose: () => void;
}) {
  const { token } = theme.useToken();
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [metadata, setMetadata] = useState<FileMetadata | null>(null);
  const [error, setError] = useState<PreviewError | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [downloadError, setDownloadError] = useState(false);
  const identity = JSON.stringify(file);
  const download = useRef<AbortController | null>(null);
  useEffect(() => {
    setBusy(false);
    setDownloadError(false);
    if (!open) {
      setLoaded(null);
      setMetadata(null);
      setError(null);
      return;
    }
    const controller = new AbortController();
    let url: string | undefined;
    setLoaded(null);
    setError(null);
    setMetadata(null);
    void (async () => {
      const value = await resolveFile(file, controller.signal);
      controller.signal.throwIfAborted();
      setMetadata(value);
      const format = previewFormat(value);
      if (format === 'unsupported') {
        setLoaded({ metadata: value, format });
        return;
      }
      if (format === 'docx' || format === 'xlsx') {
        if (value.byteSize > formatLimit(format))
          throw new PreviewError(
            'preview_too_large',
            'The Office file exceeds 10 MiB. Download the original.',
          );
        const office = await readOfficePreview(file, controller.signal);
        if (
          value.sha256 &&
          value.sha256.toLowerCase() !== office.sha256.toLowerCase()
        )
          throw readFailure(409);
        const blob =
          format === 'docx'
            ? await readFile(
                file,
                { ...value, sha256: office.sha256 },
                formatLimit(format),
                controller.signal,
              )
            : undefined;
        controller.signal.throwIfAborted();
        setLoaded({ metadata: value, format, office, blob });
        return;
      }
      const blob = await readFile(
        file,
        value,
        formatLimit(format),
        controller.signal,
      );
      if (['text', 'markdown', 'csv'].includes(format)) {
        const text = await decodeText(blob, format);
        controller.signal.throwIfAborted();
        setLoaded({ metadata: value, format, text });
      } else {
        await validateSignature(blob, value.contentType);
        if (format === 'image')
          await validateImageDimensions(blob, value.contentType);
        controller.signal.throwIfAborted();
        url = URL.createObjectURL(blob);
        setLoaded({ metadata: value, format, url });
      }
    })().catch((reason) => {
      if (!controller.signal.aborted)
        setError(
          reason instanceof PreviewError
            ? reason
            : reason instanceof OwnerCapabilityError
              ? readFailure(403)
              : new PreviewError(
                  'preview_failed',
                  'The file could not be loaded. Check the connection and retry.',
                ),
        );
    });
    return () => {
      controller.abort();
      download.current?.abort();
      if (url) URL.revokeObjectURL(url);
    };
  }, [identity, attempt, open]);
  async function save() {
    if (busy) return;
    const controller = new AbortController();
    download.current = controller;
    setBusy(true);
    setDownloadError(false);
    try {
      await downloadFile(file, controller.signal);
    } catch {
      if (!controller.signal.aborted) setDownloadError(true);
    } finally {
      if (!controller.signal.aborted) {
        setBusy(false);
        download.current = null;
      }
    }
  }
  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      closable={{ 'aria-label': 'Close file preview' }}
      width="90vw"
      className="file-preview-modal"
      centered
      destroyOnHidden
      title={
        <Typography.Text
          strong
          className="single-line-ellipsis"
          title={metadata?.displayName}
        >
          {metadata?.displayName ?? 'File preview'}
        </Typography.Text>
      }
      styles={{ body: { minWidth: 0 } }}
    >
      <Flex className="file-preview-shell" vertical gap={token.paddingSM}>
        <Flex
          className="file-preview-actions"
          wrap
          justify="space-between"
          align="center"
          gap={token.paddingXS}
        >
          <Flex className="file-preview-metadata" wrap gap={token.paddingXXS}>
            <Typography.Text type="secondary">
              {sourceLabel[file.kind]}
              {file.kind === 'drive' && file.synthetic ? ' (Synthetic)' : ''}
              {metadata ? ` · ${artifactSize(metadata.byteSize)}` : ''}
            </Typography.Text>
            {metadata && (
              <Tooltip title={metadata.contentType}>
                <Typography.Text
                  type="secondary"
                  className="single-line-ellipsis file-preview-mime"
                  title={metadata.contentType}
                >
                  · {metadata.contentType}
                </Typography.Text>
              </Tooltip>
            )}
          </Flex>
          <Flex gap={token.paddingXS}>
            <Button
              icon={<DownloadOutlined />}
              loading={busy}
              disabled={!metadata}
              onClick={() => void save()}
              aria-label={`Download ${metadata?.displayName ?? 'file'}`}
            >
              Download
            </Button>
            <Button onClick={onClose}>Close</Button>
          </Flex>
        </Flex>
        {downloadError && (
          <Alert type="error" showIcon title="Download failed. Try again." />
        )}
        <div className="file-preview-content">
          {error ? (
            <Alert
              type="warning"
              showIcon
              title={error.message}
              action={
                <Button onClick={() => setAttempt((value) => value + 1)}>
                  Retry
                </Button>
              }
            />
          ) : !loaded ? (
            <Spin aria-label="Loading file preview" />
          ) : loaded.format === 'unsupported' ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="Preview is unavailable for this format. Download the original to open it."
            />
          ) : loaded.format === 'docx' ? (
            <Suspense fallback={<Spin aria-label="Loading Word preview" />}>
              <WordPreview
                blob={loaded.blob!}
                emptyBody={loaded.office?.emptyBody}
                onClose={onClose}
              />
            </Suspense>
          ) : loaded.format === 'xlsx' ? (
            <Suspense fallback={<Spin aria-label="Loading Excel preview" />}>
              <SpreadsheetPreview file={file} initial={loaded.office!} />
            </Suspense>
          ) : loaded.format === 'csv' ? (
            <Suspense fallback={<Spin aria-label="Loading grid" />}>
              <CsvPreview
                source={loaded.text!}
                name={loaded.metadata.displayName}
                tabSeparated={
                  loaded.metadata.contentType.split(';')[0].toLowerCase() ===
                  'text/tab-separated-values'
                }
              />
            </Suspense>
          ) : loaded.text !== undefined ? (
            <TextPreview
              key={attempt}
              text={loaded.text}
              name={loaded.metadata.displayName}
              markdown={loaded.format === 'markdown'}
            />
          ) : loaded.format === 'image' ? (
            <ImagePreview
              url={loaded.url!}
              name={loaded.metadata.displayName}
              onError={setError}
            />
          ) : (
            <MediaPreview
              url={loaded.url!}
              type={loaded.metadata.contentType}
              video={loaded.format === 'video'}
              onError={setError}
            />
          )}
        </div>
      </Flex>
    </Modal>
  );
}
function ImagePreview({
  url,
  name,
  onError,
}: {
  url: string;
  name: string;
  onError: (error: PreviewError) => void;
}) {
  const { token } = theme.useToken();
  const [zoom, setZoom] = useState(100);
  const [natural, setNatural] = useState<{
    width: number;
    height: number;
  } | null>(null);
  const canvas = useRef<HTMLDivElement | null>(null);
  const drag = useRef<{
    x: number;
    y: number;
    left: number;
    top: number;
  } | null>(null);
  function fit(size = natural) {
    if (size && canvas.current)
      setZoom(
        Math.min(
          100,
          Math.max(
            1,
            Math.floor(
              100 *
                Math.min(
                  canvas.current.clientWidth / size.width,
                  canvas.current.clientHeight / size.height,
                ),
            ),
          ),
        ),
      );
  }
  return (
    <Flex vertical gap={token.paddingSM}>
      <Flex
        className="file-preview-toolbar"
        wrap
        gap={token.paddingXS}
        align="center"
      >
        <Button disabled={!natural} onClick={() => fit()}>
          Fit image
        </Button>
        <Button disabled={!natural} onClick={() => setZoom(100)}>
          Actual size
        </Button>
        <Typography.Text type="secondary">
          {Math.round(zoom)}%
          {natural ? ` · ${natural.width} × ${natural.height}` : ''}
        </Typography.Text>
      </Flex>
      <Slider
        aria-label="Image zoom"
        min={1}
        max={200}
        step={1}
        value={zoom}
        onChange={setZoom}
      />
      <div
        ref={canvas}
        className="file-preview-image"
        tabIndex={0}
        role="region"
        aria-label="Image canvas. Scroll or drag to pan."
        onPointerDown={(event) => {
          if (event.button !== 0 || !canvas.current) return;
          drag.current = {
            x: event.clientX,
            y: event.clientY,
            left: canvas.current.scrollLeft,
            top: canvas.current.scrollTop,
          };
          event.currentTarget.setPointerCapture(event.pointerId);
        }}
        onPointerMove={(event) => {
          if (drag.current && canvas.current) {
            canvas.current.scrollLeft =
              drag.current.left + drag.current.x - event.clientX;
            canvas.current.scrollTop =
              drag.current.top + drag.current.y - event.clientY;
          }
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
      >
        <img
          src={url}
          alt={name}
          draggable={false}
          style={{
            width: natural ? (natural.width * zoom) / 100 : undefined,
            maxWidth: 'none',
          }}
          onLoad={(event) => {
            const image = event.currentTarget;
            const size = {
              width: image.naturalWidth,
              height: image.naturalHeight,
            };
            if (size.width * size.height > previewLimits.imagePixels)
              onError(
                new PreviewError(
                  'preview_too_large',
                  'This image exceeds 40 million preview pixels. Download the original.',
                ),
              );
            else {
              setNatural(size);
              fit(size);
            }
          }}
          onError={() =>
            onError(
              new PreviewError(
                'preview_corrupt',
                'The image could not be decoded. Download the original.',
              ),
            )
          }
        />
      </div>
    </Flex>
  );
}
function MediaPreview({
  url,
  type,
  video,
  onError,
}: {
  url: string;
  type: string;
  video: boolean;
  onError: (error: PreviewError) => void;
}) {
  const failed = () =>
    onError(
      new PreviewError(
        'file_codec_unsupported',
        'This media codec is unavailable or the file is corrupt. Download the original to play it.',
      ),
    );
  useEffect(() => {
    if (!document.createElement(video ? 'video' : 'audio').canPlayType(type))
      failed();
  }, [type, video]);
  return video ? (
    <video
      className="file-preview-media"
      aria-label="Video player"
      controls
      preload="metadata"
      src={url}
      onError={failed}
    />
  ) : (
    <audio
      className="file-preview-media"
      aria-label="Audio player"
      controls
      preload="metadata"
      src={url}
      onError={failed}
    />
  );
}
