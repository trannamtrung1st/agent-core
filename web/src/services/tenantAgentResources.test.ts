import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf } from './identity';
import { importTenantResources, ResourceAcknowledgedError, type ResourceUpload } from './tenantAgentResources';

const fetchMock = vi.fn<typeof fetch>(); const signal = () => new AbortController().signal;
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
const upload = (name: string): ResourceUpload => { const file = new File([name], name, { type: 'text/plain' }); Object.defineProperty(file, 'arrayBuffer', { value: async () => new TextEncoder().encode(name).buffer }); return { key: name, file, logicalPath: `references/${name}`, kind: 2, mediaType: 'text/plain' }; };
let writes: any[], status: number, malformed: boolean;
beforeEach(() => {
  resetIdentityCsrf(); writes = []; status = 200; malformed = false; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return json({ token: 'csrf' });
    expect(String(path)).toBe('/api/v2/tenants/tenant/agents/agent/resources');
    const body = JSON.parse(String(options?.body)); writes.push(body);
    if (writes.length === 2 && status !== 200) return json({ reasonCode: 'stale_revision' }, status);
    return malformed ? new Response('{', { status: 200 }) : json({ resource: { key: 'instance:local' }, securityRevision: body.expectedRevision + 1, instanceRevision: body.expectedInstanceRevision + 1 });
  });
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });
it('uploads actual staged bytes sequentially using each confirmed write witness', async () => {
  const retired: string[] = []; const items = [upload('one.txt'), upload('two.txt')];
  await importTenantResources('tenant', 'agent', { securityRevision: 7, instanceRevision: 4 }, items, false, signal(), item => retired.push(item.key));
  expect(retired).toEqual(['one.txt', 'two.txt']); expect(writes).toHaveLength(2);
  expect(writes[0]).toMatchObject({ expectedRevision: 7, expectedInstanceRevision: 4, input: { content: btoa('one.txt'), enabled: false } });
  expect(writes[1]).toMatchObject({ expectedRevision: 8, expectedInstanceRevision: 5, input: { logicalPath: 'references/two.txt', content: btoa('two.txt') } });
  expect(fetchMock.mock.calls.every(([, options]) => options?.credentials === 'same-origin' && options?.cache === 'no-store')).toBe(true);
});
it('retires only confirmed files on a partial failure and resumes the remainder only with explicit fresh facts', async () => {
  status = 409; const items = [upload('one.txt'), upload('two.txt'), upload('three.txt')], retired: string[] = [];
  await expect(importTenantResources('tenant', 'agent', { securityRevision: 7, instanceRevision: 4 }, items, true, signal(), item => retired.push(item.key))).rejects.toMatchObject({ status: 409 });
  expect(retired).toEqual(['one.txt']); expect(writes).toHaveLength(2); status = 200;
  await importTenantResources('tenant', 'agent', { securityRevision: 12, instanceRevision: 9 }, items.filter(item => !retired.includes(item.key)), true, signal(), item => retired.push(item.key));
  expect(writes[2]).toMatchObject({ expectedRevision: 12, expectedInstanceRevision: 9, input: { logicalPath: 'references/two.txt' } });
  expect(writes.filter(write => write.input.logicalPath === 'references/one.txt')).toHaveLength(1); expect(retired).toEqual(['one.txt', 'two.txt', 'three.txt']);
});
it('retires an acknowledged malformed result and stops before submitting any successor file', async () => {
  malformed = true; const retired = vi.fn();
  await expect(importTenantResources('tenant', 'agent', { securityRevision: 7, instanceRevision: 4 }, [upload('one.txt'), upload('two.txt')], true, signal(), retired)).rejects.toBeInstanceOf(ResourceAcknowledgedError);
  expect(writes).toHaveLength(1); expect(retired).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ key: 'one.txt' }));
});
it('does not publish a late acknowledged file or start its successor after the mounted owner retires', async () => {
  let finish!: (response: Response) => void;
  fetchMock.mockImplementation(async path => String(path).endsWith('/csrf') ? json({ token: 'csrf' }) : new Promise(resolve => { finish = resolve; }));
  const controller = new AbortController(); const retired = vi.fn();
  const run = importTenantResources('tenant', 'agent', { securityRevision: 7, instanceRevision: 4 }, [upload('one.txt'), upload('two.txt')], true, controller.signal, retired);
  await vi.waitFor(() => expect(finish).toBeTypeOf('function')); controller.abort(); finish(json({ resource: { key: 'instance:local' }, securityRevision: 8, instanceRevision: 5 }));
  await expect(run).rejects.toMatchObject({ name: 'AbortError' }); expect(retired).not.toHaveBeenCalled(); expect(fetchMock).toHaveBeenCalledTimes(2);
});
