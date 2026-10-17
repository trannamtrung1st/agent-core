import { IdentityCompletionError, identityRequest } from './identity';
import { tenantAgentApi } from './tenantAgentConfiguration';
import type { InstanceResource } from './instanceConfiguration';

export type ResourceWitness = { securityRevision: number; instanceRevision: number };
export type ResourceSaved = { resource: InstanceResource | null; securityRevision: number; instanceRevision: number };
export type ResourceUpload = { key: string; file: File; logicalPath: string; kind: number; mediaType: string };
export class ResourceAcknowledgedError extends Error {
  constructor() { super('This resource was saved, but its new revision could not be read. Reload latest resources before continuing. Saved files will not be submitted again.'); }
}
export async function resourceFileContent(file: File, signal: AbortSignal): Promise<string> {
  if (file.size < 1 || file.size > 8 * 1024 * 1024) throw new Error('Choose a non-empty file up to 8 MiB.');
  const bytes = new Uint8Array(await file.arrayBuffer()); signal.throwIfAborted();
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 8192) binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(binary);
}
export async function saveTenantResource(tenant: string, id: string, witness: ResourceWitness, input: Record<string, unknown>, signal: AbortSignal): Promise<ResourceSaved> {
  let saved: ResourceSaved;
  try { saved = await identityRequest<ResourceSaved>(tenantAgentApi(tenant, id, '/resources'), { method: 'POST', signal,
    body: { expectedRevision: witness.securityRevision, expectedInstanceRevision: witness.instanceRevision, input } }); }
  catch (error) { if (error instanceof IdentityCompletionError) throw new ResourceAcknowledgedError(); throw error; }
  if (!saved || saved.securityRevision !== witness.securityRevision + 1 || saved.instanceRevision !== witness.instanceRevision + 1
    || typeof saved.resource?.key !== 'string' || !saved.resource.key.startsWith('instance:')) throw new ResourceAcknowledgedError();
  return saved;
}
/** Each confirmed file is retired before the next write; a missing acknowledgement witness stops the batch. */
export async function importTenantResources(tenant: string, id: string, witness: ResourceWitness, uploads: ResourceUpload[], enabled: boolean,
  signal: AbortSignal, onSaved: (upload: ResourceUpload, saved?: ResourceSaved) => void): Promise<void> {
  let current = witness;
  for (const item of uploads) {
    signal.throwIfAborted();
    const content = await resourceFileContent(item.file, signal);
    try {
      const saved = await saveTenantResource(tenant, id, current, { logicalPath: item.logicalPath.trim(), kind: item.kind, mediaType: item.mediaType, content, enabled }, signal);
      signal.throwIfAborted(); onSaved(item, saved); current = saved;
    } catch (error) {
      if (!signal.aborted && error instanceof ResourceAcknowledgedError) onSaved(item);
      throw error;
    }
  }
}
