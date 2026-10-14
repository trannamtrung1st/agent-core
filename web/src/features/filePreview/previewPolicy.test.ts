import { testPolicy } from './previewTestPolicy';
import { describe, expect, it } from 'vitest';
import {
  previewFormat,
  formatLimit,
  decodeText,
  validateSignature,
} from './previewPolicy';
import { parseDelimited } from './parseDelimited';
import { Blob } from 'node:buffer';
const blob = (bytes: Uint8Array | string) =>
  new Blob([bytes]) as unknown as globalThis.Blob;
describe('Preview format and limits', () => {
  it('fails closed on active, mismatched and unsupported formats', () => {
    for (const [displayName, contentType] of [
      ['x.svg', 'image/svg+xml'],
      ['x.png', 'text/html'],
      ['x.docx', 'application/zip'],
      ['x.pdf', 'application/pdf'],
      ['x.csv', 'image/png'],
    ])
      expect(previewFormat({ displayName, contentType, byteSize: 1 })).toBe(
        'unsupported',
      );
    expect(
      previewFormat({
        displayName: 'x.html',
        contentType: 'text/html',
        byteSize: 1,
      }),
    ).toBe('text');
    expect(formatLimit('video', testPolicy)).toBe(testPolicy.limits.videoBytes);
  });
  it('decodes UTF BOM source without rewriting CRLF or BOM and rejects invalid encodings/binary text', async () => {
    const source = '\uFEFFa,b\r\n001,"x\r\ny"\r\n';
    expect(await decodeText(blob(source), 'csv', testPolicy)).toBe(source);
    expect(
      await decodeText(
        blob(new Uint8Array([255, 254, 65, 0, 13, 0, 10, 0])),
        'text', testPolicy,
      ),
    ).toBe('\uFEFFA\r\n');
    await expect(
      decodeText(blob(new Uint8Array([255])), 'text', testPolicy),
    ).rejects.toMatchObject({ code: 'preview_encoding' });
    await expect(decodeText(blob('a\0b'), 'text', testPolicy)).rejects.toMatchObject({
      code: 'preview_signature',
    });
  });
  it('checks media/image signatures before creating native URLs', async () => {
    await expect(
      validateSignature(blob('<script>bad()</script>'), 'image/png'),
    ).rejects.toMatchObject({ code: 'preview_signature' });
    await expect(
      validateSignature(blob('RIFF1234WAVE'), 'audio/wav'),
    ).resolves.toBeUndefined();
  });
});
describe('CSV source fidelity', () => {
  it('preserves embedded delimiters, quotes, newlines, empty columns and leading zeros as inert strings', () => {
    const source =
      '\uFEFFid,note,formula,empty\r\n001,"a,b\r\n""quoted""",=HYPERLINK("evil"),\r\n';
    const result = parseDelimited(source, ',', testPolicy.spreadsheet);
    expect(result.rows[1]).toEqual([
      '001',
      'a,b\r\n"quoted"',
      '=HYPERLINK("evil")',
      '',
    ]);
    expect(result.partial).toBe(false);
  });
  it('uses explicit TSV delimiter and rejects malformed quotes/oversized columns', () => {
    expect(parseDelimited('id\tvalue\n0001\ta,b', '\t', testPolicy.spreadsheet).rows[1]).toEqual([
      '0001',
      'a,b',
    ]);
    expect(() => parseDelimited('a,b\n"open', ',', testPolicy.spreadsheet)).toThrow(/malformed/);
    const wide = parseDelimited(Array.from({ length: 257 }, () => 'x').join(','), ',', testPolicy.spreadsheet);
    expect(wide.partial).toBe(true); expect(wide.rows[0]).toHaveLength(256);
  });
  it('routes extensionless resource keys by allowlisted MIME while retaining mismatched-extension fallback', () => {
    expect(
      previewFormat({
        displayName: 'support-order-policy',
        contentType: 'application/json',
        byteSize: 10,
      }),
    ).toBe('text');
    expect(
      previewFormat({
        displayName: 'support-order-policy',
        contentType: 'text/markdown',
        byteSize: 10,
      }),
    ).toBe('markdown');
    expect(
      previewFormat({
        displayName: 'rows',
        contentType: 'text/csv',
        byteSize: 10,
      }),
    ).toBe('csv');
    expect(
      previewFormat({
        displayName: 'photo',
        contentType: 'image/png',
        byteSize: 10,
      }),
    ).toBe('image');
    expect(
      previewFormat({
        displayName: 'report.pdf',
        contentType: 'application/json',
        byteSize: 10,
      }),
    ).toBe('unsupported');
  });
});

describe('Host-policy thresholds', () => {
  it('selects the minimum global/per-format policy for every supported format', () => {
    for (const format of ['text','markdown','csv','docx','xlsx','image','audio','video'] as const) {
      const limit = formatLimit(format, testPolicy);
      expect(limit).toBeGreaterThan(0);
      expect(formatLimit(format, { ...testPolicy, limits: { ...testPolicy.limits, maxBytes: limit - 1 } })).toBe(limit - 1);
    }
  });
});
