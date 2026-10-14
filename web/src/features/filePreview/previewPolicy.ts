import { PreviewError, type FileMetadata } from './fileSource';
import { type PreviewOptions } from './previewOptions';
export type PreviewFormat =
  | 'text'
  | 'markdown'
  | 'csv'
  | 'image'
  | 'audio'
  | 'video'
  | 'docx'
  | 'xlsx'
  | 'unsupported';
const textExtensions = new Set(
  'txt log ini js jsx ts tsx cs py sql json jsonl ndjson yaml yml toml xml css html sh rb go rs java c cpp h'.split(
    ' ',
  ),
);
const images: Record<string, string> = {
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  webp: 'image/webp',
  gif: 'image/gif',
  bmp: 'image/bmp',
  avif: 'image/avif',
};
const media: Record<string, string[]> = {
  mp3: ['audio/mpeg'],
  wav: ['audio/wav', 'audio/x-wav'],
  ogg: ['audio/ogg', 'video/ogg'],
  m4a: ['audio/mp4', 'audio/x-m4a'],
  flac: ['audio/flac', 'audio/x-flac'],
  mp4: ['video/mp4'],
  webm: ['video/webm'],
  mov: ['video/quicktime'],
};
export const extension = (name: string) =>
  name.split('.').at(-1)?.toLowerCase() ?? '';
export function previewFormat(file: FileMetadata): PreviewFormat {
  const noExtension = !file.displayName.split('/').at(-1)!.includes('.');
  const ext = extension(file.displayName);
  const mime = file.contentType.split(';')[0].trim().toLowerCase();
  if (
    ext === 'docx' &&
    mime ===
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
  )
    return 'docx';
  if (
    ext === 'xlsx' &&
    mime === 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
  )
    return 'xlsx';
  if (
    images[ext] === mime ||
    (noExtension && Object.values(images).includes(mime))
  )
    return 'image';
  if (
    media[ext]?.includes(mime) ||
    (noExtension && Object.values(media).some((types) => types.includes(mime)))
  )
    return mime.startsWith('audio/') ? 'audio' : 'video';
  // HTML/XML/code are always escaped text; SVG and unknown binary types stay unsupported.
  const textual =
    mime.startsWith('text/') ||
    [
      'application/json',
      'application/x-ndjson',
      'application/yaml',
      'application/x-yaml',
      'application/toml',
      'application/xml',
      'application/javascript',
      'application/typescript',
    ].includes(mime);
  if (!textual) return 'unsupported';
  if (
    (['csv', 'tsv'].includes(ext) ||
      (noExtension &&
        ['text/csv', 'text/tab-separated-values'].includes(mime))) &&
    ['text/plain', 'text/csv', 'text/tab-separated-values'].includes(mime)
  )
    return 'csv';
  if (
    (['md', 'markdown'].includes(ext) ||
      (noExtension && mime === 'text/markdown')) &&
    ['text/plain', 'text/markdown'].includes(mime)
  )
    return 'markdown';
  return textExtensions.has(ext) || noExtension ? 'text' : 'unsupported';
}
export function formatLimit(format: PreviewFormat, policy: PreviewOptions): number {
  const key = format === 'csv' ? 'delimitedBytes' : format === 'markdown' || format === 'unsupported' ? 'textBytes' : `${format}Bytes` as keyof PreviewOptions['limits'];
  return Math.min(policy.limits.maxBytes, policy.limits[key]);
}
export async function decodeText(
  blob: Blob,
  format: PreviewFormat,
  policy: PreviewOptions,
): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  const encoding =
    bytes[0] === 0xff && bytes[1] === 0xfe
      ? 'utf-16le'
      : bytes[0] === 0xfe && bytes[1] === 0xff
        ? 'utf-16be'
        : 'utf-8';
  let text: string;
  try {
    text = new TextDecoder(encoding, { fatal: true, ignoreBOM: true }).decode(
      bytes,
    );
  } catch {
    throw new PreviewError(
      'preview_encoding',
      'This encoding is unsupported. Download the original to read it.',
    );
  }
  if (text.includes('\0'))
    throw new PreviewError(
      'preview_signature',
      'The content does not match a text file. Download the original.',
    );
  if (format !== 'csv' && text.split('\n').length > policy.text.maxLines)
    throw new PreviewError(
      'preview_structure_limit',
      'This file exceeds the configured preview line limit. Download the original.',
    );
  return text;
}
export async function validateSignature(
  blob: Blob,
  mime: string,
): Promise<void> {
  const bytes = new Uint8Array(await blob.slice(0, 64).arrayBuffer());
  const ascii = (start: number, end: number) =>
    String.fromCharCode(...bytes.slice(start, end));
  const starts = (...values: number[]) =>
    values.every((x, i) => bytes[i] === x);
  const type = mime.split(';')[0].toLowerCase();
  const valid =
    type === 'image/png'
      ? starts(137, 80, 78, 71, 13, 10, 26, 10)
      : type === 'image/jpeg'
        ? starts(255, 216, 255)
        : type === 'image/gif'
          ? ['GIF87a', 'GIF89a'].includes(ascii(0, 6))
          : type === 'image/bmp'
            ? ascii(0, 2) === 'BM'
            : type === 'image/webp'
              ? ascii(0, 4) === 'RIFF' && ascii(8, 12) === 'WEBP'
              : type === 'image/avif'
                ? ascii(4, 8) === 'ftyp' && /avif|avis/.test(ascii(8, 32))
                : ['audio/wav', 'audio/x-wav'].includes(type)
                  ? ascii(0, 4) === 'RIFF' && ascii(8, 12) === 'WAVE'
                  : type.includes('ogg')
                    ? ascii(0, 4) === 'OggS'
                    : type.includes('flac')
                      ? ascii(0, 4) === 'fLaC'
                      : type === 'audio/mpeg'
                        ? ascii(0, 3) === 'ID3' ||
                          (bytes[0] === 255 && (bytes[1] & 0xe0) === 0xe0)
                        : type === 'video/webm'
                          ? starts(0x1a, 0x45, 0xdf, 0xa3)
                          : [
                                'audio/mp4',
                                'audio/x-m4a',
                                'video/mp4',
                                'video/quicktime',
                              ].includes(type)
                            ? ascii(4, 8) === 'ftyp'
                            : false;
  if (!valid)
    throw new PreviewError(
      'preview_signature',
      'The file signature does not match its type. Download the original to inspect it.',
    );
}

// Read dimensions before handing encoded bytes to the browser's native decoder.
export async function validateImageDimensions(
  blob: Blob,
  mime: string,
  policy: PreviewOptions,
): Promise<void> {
  const bytes = new Uint8Array(await blob.slice(0, 256 * 1024).arrayBuffer());
  const view = new DataView(bytes.buffer);
  const type = mime.split(';')[0];
  const text = (at: number, count: number) =>
    String.fromCharCode(...bytes.slice(at, at + count));
  let width = 0;
  let height = 0;
  try {
    if (type === 'image/png') {
      width = view.getUint32(16);
      height = view.getUint32(20);
    } else if (type === 'image/gif') {
      width = view.getUint16(6, true);
      height = view.getUint16(8, true);
    } else if (type === 'image/bmp') {
      width = Math.abs(view.getInt32(18, true));
      height = Math.abs(view.getInt32(22, true));
    } else if (type === 'image/webp') {
      if (text(12, 4) === 'VP8X') {
        width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
        height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
      } else if (text(12, 4) === 'VP8L' && bytes[20] === 0x2f) {
        const packed = view.getUint32(21, true);
        width = 1 + (packed & 0x3fff);
        height = 1 + ((packed >>> 14) & 0x3fff);
      } else if (
        text(12, 4) === 'VP8 ' &&
        bytes[23] === 0x9d &&
        bytes[24] === 1 &&
        bytes[25] === 0x2a
      ) {
        width = view.getUint16(26, true) & 0x3fff;
        height = view.getUint16(28, true) & 0x3fff;
      }
    } else if (type === 'image/jpeg') {
      for (let at = 2; at + 9 < bytes.length; ) {
        if (bytes[at] !== 255) break;
        const marker = bytes[at + 1];
        if (marker === 255) {
          at++;
          continue;
        }
        if (marker === 0xd9 || marker === 0xda) break;
        const length = view.getUint16(at + 2);
        if (length < 2) break;
        if (
          [
            0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd,
            0xce, 0xcf,
          ].includes(marker)
        ) {
          height = view.getUint16(at + 5);
          width = view.getUint16(at + 7);
          break;
        }
        at += 2 + length;
      }
    } else if (type === 'image/avif') {
      for (let at = 4; at + 16 <= bytes.length; at++)
        if (text(at, 4) === 'ispe' && view.getUint32(at - 4) >= 20) {
          width = Math.max(width, view.getUint32(at + 8));
          height = Math.max(height, view.getUint32(at + 12));
        }
    }
  } catch {
    /* malformed or unavailable metadata falls through to a truthful fallback */
  }
  if (!width || !height)
    throw new PreviewError(
      'preview_corrupt',
      'Safe image dimensions could not be read. Download the original.',
    );
  if (
    width * height > policy.image.maxPixels ||
    width > policy.image.maxDimension ||
    height > policy.image.maxDimension
  )
    throw new PreviewError(
      'preview_structure_limit',
      'This image exceeds the preview dimension limit. Download the original.',
    );
}
