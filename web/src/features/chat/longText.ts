import type { MessagePart } from '../../services/messageParts';

export const LONG_TEXT_MESSAGE = 'Please work with the attached text.';
export const MAX_DIRECT_TEXT = 8_000;
// Canonical attachment intake limit; the server remains authoritative.
export const MAX_LONG_TEXT_BYTES = 25 * 1024 * 1024;

export function longTextFile(text: string, parts: readonly MessagePart[] | null | undefined, now = new Date()): File {
  if (text.length <= MAX_DIRECT_TEXT) throw new Error('This message fits in the composer.');
  if (parts?.some(part => part.kind !== 'text')) throw new Error('Attach the large literal text separately and keep your @ references and / Skills selected.');
  const bytes = new TextEncoder().encode(text);
  if (bytes.byteLength > MAX_LONG_TEXT_BYTES) throw new Error('The original text exceeds the 25 MiB attachment limit. Shorten it before attaching.');
  return new File([bytes], `message-${now.toISOString().replace(/[:.]/g, '-')}.txt`, { type: 'text/plain' });
}
