import { useEffect, useState } from 'react';
import { Alert, Flex, Slider, Spin, Typography, theme } from 'antd';
import DOMPurify from 'dompurify';
export function WordPreview({
  blob,
  onClose,
  emptyBody = false,
}: {
  blob: Blob;
  onClose: () => void;
  emptyBody?: boolean;
}) {
  const { token } = theme.useToken();
  const [html, setHtml] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [zoom, setZoom] = useState(100);
  useEffect(() => {
    let cancelled = false;
    setHtml(null);
    setFailed(false);
    void (async () => {
      const { renderAsync } = await import('docx-preview');
      // The backend has rejected external relations, macros and alternate HTML chunks.
      // Render detached, use inline local raster data, then sanitize into a scriptless frame.
      const body = document.createElement('div');
      const styles = document.createElement('div');
      await renderAsync(blob, body, styles, {
        inWrapper: false,
        ignoreFonts: true,
        useBase64URL: true,
        renderAltChunks: false,
        renderComments: false,
        experimental: false,
      });
      if (cancelled) return;
      const safe = DOMPurify.sanitize(
        '<div>' + styles.innerHTML + body.innerHTML + '</div>',
        {
          ADD_TAGS: ['style'],
          FORBID_TAGS: [
            'script',
            'iframe',
            'object',
            'embed',
            'form',
            'input',
            'button',
            'svg',
            'math',
          ],
          FORBID_ATTR: ['href', 'srcset', 'target', 'action', 'formaction'],
        },
      );
      const fragment = document.createElement('div');
      fragment.innerHTML = safe;
      for (const image of fragment.querySelectorAll('img'))
        if (
          !/^data:image\/(png|jpeg|gif|webp);base64,/i.test(
            image.getAttribute('src') ?? '',
          )
        )
          image.remove();
      setHtml(fragment.innerHTML);
    })().catch(() => {
      if (!cancelled) setFailed(true);
    });
    return () => {
      cancelled = true;
    };
  }, [blob]);
  const csp =
    "default-src 'none'; script-src 'none'; connect-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";
  return (
    <Flex vertical gap={token.paddingSM} className="file-preview-word">
      {emptyBody && (
        <Typography.Text type="secondary" role="status">
          Empty document body · headers, footers or other content may still
          appear in the original.
        </Typography.Text>
      )}
      <Typography.Text type="secondary">
        Best-effort Word preview · additional Office layout/features may be
        present in the download.
      </Typography.Text>
      <Flex
        className="file-preview-toolbar"
        gap={token.paddingSM}
        align="center"
      >
        <Slider
          style={{ flex: 1 }}
          aria-label="Document zoom"
          min={50}
          max={150}
          step={25}
          value={zoom}
          onChange={setZoom}
        />
        <Typography.Text type="secondary" className="file-preview-zoom-value">
          {zoom}%
        </Typography.Text>
      </Flex>
      {failed ? (
        <Alert
          type="warning"
          showIcon
          title="This document could not be rendered safely. Download the original."
        />
      ) : !html ? (
        <Spin aria-label="Rendering Word document" />
      ) : (
        <iframe
          sandbox="allow-same-origin"
          onLoad={(event) => {
            const frame = event.currentTarget;
            const content = frame.contentDocument;
            content?.addEventListener('keydown', (keyboard) => {
              if (keyboard.key === 'Escape') {
                keyboard.preventDefault();
                onClose();
              } else if (keyboard.key === 'Tab') {
                keyboard.preventDefault();
                const target = keyboard.shiftKey
                  ? frame.parentElement?.querySelector<HTMLElement>(
                      '[role="slider"]',
                    )
                  : frame
                      .closest('[role="dialog"]')
                      ?.querySelector<HTMLElement>(
                        'button[aria-label="Close file preview"]',
                      );
                target?.focus();
              }
            });
          }}
          referrerPolicy="no-referrer"
          title="Word document preview"
          className="file-preview-word-frame"
          srcDoc={`<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="${csp}"><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;padding:${token.padding}px;background:${token.colorBgContainer};zoom:${zoom}%}section.docx{background:${token.colorWhite};margin-inline:auto;max-width:100%;box-sizing:border-box}img{max-width:100%;height:auto}@media(max-width:767px){section.docx{width:100%!important;min-height:auto!important;padding:${token.padding}px!important}}</style></head><body>${html}</body></html>`}
        />
      )}
    </Flex>
  );
}
