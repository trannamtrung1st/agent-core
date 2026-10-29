// Stateless decoding shared by SignalR JSON and MessagePack audio payloads.
export function bytesOf(data: unknown): Uint8Array {
  if (data instanceof Uint8Array) {
    return data;
  }

  if (data instanceof ArrayBuffer) {
    return new Uint8Array(data);
  }

  if (ArrayBuffer.isView(data)) {
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }

  if (typeof data === "string") {
    return bytesFromBase64(data);
  }

  if (data && typeof data === "object" && "data" in (data as { data?: unknown }) && Array.isArray((data as { data: unknown }).data)) {
    return Uint8Array.from((data as { data: number[] }).data);
  }

  if (Array.isArray(data)) {
    return Uint8Array.from(data as number[]);
  }

  return new Uint8Array();
}

function bytesFromBase64(value: string): Uint8Array {
  if (value.length === 0 || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(value)) {
    return new Uint8Array();
  }

  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }

  return bytes;
}
