import { describe, expect, it } from 'vitest';
import { longTextFile } from './longText';

describe('explicit long text attachment', () => {
  it('preserves exact UTF-8 bytes, whitespace, CRLF, code fences and Unicode', async () => {
    const original = ' \r\n```text\r\n@literal /literal 😀 é\n```\n' + '字'.repeat(8_001) + '  ';
    const file = longTextFile(original, [{ kind: 'text', text: original }], new Date('2026-10-26T00:00:00Z'));
    expect(file.type).toBe('text/plain');
    expect(file.size).toBe(new TextEncoder().encode(original).byteLength);
    const bytes = await new Promise<ArrayBuffer>((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result as ArrayBuffer); reader.onerror = reject; reader.readAsArrayBuffer(file); });
    expect(new TextDecoder().decode(bytes)).toBe(original);
  });
  it('keeps the 8k direct-text boundary and rejects mixed chips and oversized bytes', () => {
    expect(() => longTextFile('x'.repeat(8_000), null)).toThrow('fits');
    expect(() => longTextFile('x'.repeat(8_001), [{ kind: 'invocation', skillKey: 'definition:test', label: 'Test' } as never])).toThrow('separately');
    expect(() => longTextFile('字'.repeat(9_000_000), null)).toThrow('25 MiB');
  });
});
