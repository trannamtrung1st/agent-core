import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
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
import { readOfficePreview, type OfficePreview } from './officeSource';
import { PreviewRenderBoundary } from './PreviewRenderBoundary';
import { TextPreview } from './TextPreview';
import { artifactSize } from '../../services/artifacts';
import {
  decodeText,
  formatLimit,
  previewFormat,
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
  fileReadLost,
  fileFailureMessage,
  fileTenantId,
} from './fileSource';
import { loadPreviewOptions, mib, type PreviewOptions } from './previewOptions';
const loadCsvPreview = () =>
  import('./CsvPreview').then((module) => ({ default: module.CsvPreview }));
const loadWordPreview = () =>
  import('./WordPreview').then((module) => ({ default: module.WordPreview }));
const loadSpreadsheetPreview = () =>
  import('./SpreadsheetPreview').then((module) => ({
    default: module.SpreadsheetPreview,
  }));
type Loaded = {
  policy?: PreviewOptions;
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
  onReadLost,
}: {
  file: FileRef;
  disabled?: boolean;
  iconOnly?: boolean;
  label?: string;
  onReadLost?: (reason?: unknown) => void;
}) {
  return (
    <PreviewEntry
      key={JSON.stringify(file)}
      file={file}
      disabled={disabled}
      iconOnly={iconOnly}
      label={label}
      onReadLost={onReadLost}
    />
  );
}
function PreviewEntry({
  file,
  disabled,
  iconOnly,
  label,
  onReadLost,
}: {
  file: FileRef;
  disabled: boolean;
  iconOnly: boolean;
  label?: string;
  onReadLost?: (reason?: unknown) => void;
}) {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  return (
    <>
      <Tooltip title={iconOnly ? label : undefined}>
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
      <FilePreview file={file} open={open} onClose={() => setOpen(false)} onReadLost={onReadLost} />
    </>
  );
}
export function FilePreview({
  file,
  open = true,
  onClose,
  onReadLost,
}: {
  file: FileRef;
  open?: boolean;
  onClose: () => void;
  onReadLost?: (reason?: unknown) => void;
}) {
  const { token } = theme.useToken();
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [metadata, setMetadata] = useState<FileMetadata | null>(null);
  const [error, setError] = useState<PreviewError | null>(null);
  const [attempt, setAttempt] = useState(0);
  // A rejected React.lazy loader is cached; Retry needs fresh loader identities.
  const CsvPreview = useMemo(() => lazy(loadCsvPreview), [attempt]);
  const WordPreview = useMemo(() => lazy(loadWordPreview), [attempt]);
  const SpreadsheetPreview = useMemo(() => lazy(loadSpreadsheetPreview), [attempt]);
  const [busy, setBusy] = useState(false);
  const [downloadError, setDownloadError] = useState<string>();
  const identity = JSON.stringify(file);
  const download = useRef<AbortController | null>(null);
  const previewShell = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    setBusy(false);
    setDownloadError(undefined);
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
        setLoaded({ metadata: value, format, policy: undefined });
        return;
      }
      const policy = await loadPreviewOptions(fileTenantId(file), controller.signal);
      controller.signal.throwIfAborted();
      const limit = formatLimit(format, policy);
      if (value.byteSize > limit) throw new PreviewError('preview_too_large', `This file is ${mib(value.byteSize)}; its preview limit is ${mib(limit)}. Download the original.`);
      if (format === 'docx' || format === 'xlsx') {
        const office = await readOfficePreview(file, controller.signal, policy);
        if (
          value.sha256 &&
          value.sha256.toLowerCase() !== office.sha256.toLowerCase()
        )
          throw readFailure(409);
        const blob = (file.kind === 'tenantWorkspace') && format === 'docx'
          ? liveDocumentBlob(office, limit)
          : format === 'docx'
            ? await readFile(
                file,
                { ...value, sha256: office.sha256 },
                limit,
                controller.signal,
              )
            : undefined;
        controller.signal.throwIfAborted();
        setLoaded({ metadata: value, format, policy, office, blob });
        return;
      }
      const blob = await readFile(file, value, limit, controller.signal);
      const currentMetadata = (file.kind === 'tenantWorkspace') ? {...value, byteSize: blob.size} : value;
      if ((file.kind === 'tenantWorkspace')) setMetadata(currentMetadata);
      if (['text', 'markdown', 'csv'].includes(format)) {
        const text = await decodeText(blob, format, policy);
        controller.signal.throwIfAborted();
        setLoaded({ metadata: currentMetadata, format, policy, text });
      } else {
        await validateSignature(blob, value.contentType);
        if (format === 'image')
          await validateImageDimensions(blob, value.contentType, policy);
        controller.signal.throwIfAborted();
        url = URL.createObjectURL(blob);
        setLoaded({ metadata: currentMetadata, format, policy, url });
      }
    })().catch((reason) => {
      if (!controller.signal.aborted) {
        if (fileReadLost(reason)) { setMetadata(null); onReadLost?.(reason); }
        setError(
          reason instanceof PreviewError
            ? reason
            : new PreviewError(
                  'preview_failed',
                  'The file could not be loaded. Check the connection and retry.',
                ),
        );
      }
    });
    return () => {
      controller.abort();
      download.current?.abort();
      if (url) URL.revokeObjectURL(url);
    };
  }, [identity, attempt, open]);
  useEffect(() => {
    if (!open) return;
    // Sheet replacement can temporarily retire the library's focused DOM node.
    // Escape still belongs to the innermost preview while it is open.
    const dismiss = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      const dialog = previewShell.current?.closest('[role="dialog"]');
      if (!dialog?.getClientRects().length) return;
      const focusedDialog = document.activeElement?.closest('[role="dialog"]');
      if (focusedDialog && focusedDialog !== dialog) return;
      event.preventDefault(); event.stopImmediatePropagation(); onClose();
    };
    document.addEventListener('keydown', dismiss, true);
    return () => document.removeEventListener('keydown', dismiss, true);
  }, [open, onClose]);
  async function save() {
    if (busy) return;
    const controller = new AbortController();
    download.current = controller;
    setBusy(true);
    setDownloadError(undefined);
    try {
      await downloadFile(file, controller.signal);
    } catch (reason) {
      if (!controller.signal.aborted) { if (fileReadLost(reason)) { setLoaded(null); setMetadata(null); onReadLost?.(reason); } setDownloadError(fileFailureMessage(reason)); }
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
        <Flex align="center" style={{ minWidth: 0, minHeight: token.controlHeightLG, paddingInlineEnd: token.controlHeightLG + token.paddingXS }}>
        <Typography.Text
          strong
          className="single-line-ellipsis"
          title={metadata?.displayName}
        >
          {metadata?.displayName ?? 'File preview'}
        </Typography.Text>
        </Flex>
      }
      styles={{ body: { minWidth: 0 } }}
    >
      <Flex ref={previewShell} className="file-preview-shell" vertical gap={token.paddingSM}>
        <Flex
          className="file-preview-actions"
          data-feedback-actions
          wrap
          justify="space-between"
          align="center"
          gap={token.paddingXS}
        >
          <Flex className="file-preview-metadata" wrap gap={token.paddingXXS}>
            <Typography.Text type="secondary">
              {sourceLabel[file.kind]}
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
          <Flex wrap gap={token.paddingXS}>
            {(file.kind === 'tenantWorkspace') && <Button disabled={busy} onClick={() => setAttempt(value => value + 1)}>Reload preview</Button>}
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
        {(file.kind === 'tenantWorkspace') && <Typography.Text type="secondary">Live file · current content can change. Reload preview to read this path again.</Typography.Text>}
        {downloadError && (
          <Alert type="error" showIcon title={downloadError} />
        )}
        <div className="file-preview-content" onKeyDownCapture={event => { if (event.key === 'Escape') { event.stopPropagation(); onClose(); } }}>
          <PreviewRenderBoundary key={`${identity}:${attempt}:${open}`} onRetry={() => setAttempt(value => value + 1)}>
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
              <SpreadsheetPreview file={file} initial={loaded.office!} policy={loaded.policy!} onReadLost={onReadLost} />
            </Suspense>
          ) : loaded.format === 'csv' ? (
            <Suspense fallback={<Spin aria-label="Loading grid" />}>
              <CsvPreview
                policy={loaded.policy!}
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
              policy={loaded.policy!}
              key={attempt}
              text={loaded.text}
              name={loaded.metadata.displayName}
              markdown={loaded.format === 'markdown'}
            />
          ) : loaded.format === 'image' ? (
            <ImagePreview
              policy={loaded.policy!}
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
          </PreviewRenderBoundary>
        </div>
      </Flex>
    </Modal>
  );
}
function ImagePreview({
  policy,
  url,
  name,
  onError,
}: {
  policy: PreviewOptions;
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
        ariaLabelForHandle="Image zoom"
        tooltip={{ open: false }}
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
            if (size.width * size.height > policy.image.maxPixels)
              onError(
                new PreviewError(
                  'preview_structure_limit',
                  'This image exceeds the configured preview pixel limit. Download the original.',
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
        'This media codec is unavailable in the browser. Download the original to play it.',
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
      onError={event => {
        const code = event.currentTarget.error?.code;
        onError(code === 3 ? new PreviewError('preview_corrupt', 'The media could not be decoded. Download the original to inspect it.') : code === 2 ? new PreviewError('preview_failed', 'Media loading failed. Retry or download the original.') : new PreviewError('file_codec_unsupported', 'This media codec is unavailable in the browser. Download the original to play it.'));
      }}
    />
  ) : (
    <audio
      className="file-preview-media"
      aria-label="Audio player"
      controls
      preload="metadata"
      src={url}
      onError={event => {
        const code = event.currentTarget.error?.code;
        onError(code === 3 ? new PreviewError('preview_corrupt', 'The media could not be decoded. Download the original to inspect it.') : code === 2 ? new PreviewError('preview_failed', 'Media loading failed. Retry or download the original.') : new PreviewError('file_codec_unsupported', 'This media codec is unavailable in the browser. Download the original to play it.'));
      }}
    />
  );
}

function liveDocumentBlob(office: OfficePreview, limit: number): Blob {
  if (typeof office.sourceBase64 !== 'string' || office.sourceBase64.length > Math.ceil(limit / 3) * 4)
    throw new PreviewError('preview_unavailable', 'The current document could not be loaded with its checked content. Reload preview.');
  const decoded = atob(office.sourceBase64);
  if (decoded.length > limit) throw new PreviewError('preview_too_large', 'The current document exceeds its preview limit. Download the original.');
  return new Blob([Uint8Array.from(decoded, character => character.charCodeAt(0))], {type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'});
}
