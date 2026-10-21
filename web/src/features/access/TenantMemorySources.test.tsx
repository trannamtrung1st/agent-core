import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { TenantMemorySources } from './TenantMemorySources';
import type { TenantMemoryItem } from '../../services/tenantMemory';
const source: TenantMemoryItem = { memoryId: 'derived', scope: 'identityUser', kind: 'Preference', status: 'Active', subject: 'Combined', content: 'Summary', revision: 1, createdAt: '', updatedAt: '', originAgentId: 'agent', source: 'userExplicit', canForget: true, sourceMemoryIds: ['first', 'second'] };
const response = (item: Partial<TenantMemoryItem>) => new Response(JSON.stringify({ items: [item] }), { status: 200, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>();
beforeEach(() => { vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); fetchMock.mockImplementation(async path => String(path).includes('/first?') ? response({ ...source, memoryId: 'first', status: 'Superseded', subject: 'Original source', content: 'Retained source content' }) : response({ ...source, memoryId: 'second', status: 'Deleted', subject: 'DELETED_SUBJECT', content: 'DELETED_BODY' })); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const show = (item = source) => render(<App><TenantMemorySources tenant="tenant" agent="agent" scope="identityUser" item={item} /></App>);
it('loads exact sources on demand in current scope, retaining Superseded content and hiding Deleted bodies', async () => {
  show(); expect(fetchMock).not.toHaveBeenCalled(); fireEvent.click(screen.getByRole('button', { name: 'View sources' }));
  await screen.findByText('Retained source content'); await screen.findByText('Content removed');
  expect(fetchMock.mock.calls.map(c => String(c[0]))).toEqual(['/api/v2/tenants/tenant/agents/agent/memory/first?scope=identityUser', '/api/v2/tenants/tenant/agents/agent/memory/second?scope=identityUser']);
  expect(screen.queryByText('DELETED_BODY')).not.toBeInTheDocument(); expect(screen.queryByText('DELETED_SUBJECT')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Hide sources' })); expect(screen.queryByText('Retained source content')).not.toBeInTheDocument();
});
it('withholds malformed or oversized lineage without requests', () => {
  const shown = show({ ...source, sourceMemoryIds: ['same', 'same'] }); expect(screen.getByText('Source list unavailable')).toBeInTheDocument();
  shown.rerender(<App><TenantMemorySources tenant="tenant" agent="agent" scope="identityUser" item={{ ...source, sourceMemoryIds: Array.from({ length: 9 }, (_, i) => String(i)) }} /></App>); expect(screen.getByText('Source list unavailable')).toBeInTheDocument(); expect(fetchMock).not.toHaveBeenCalled();
});
it('never substitutes a mismatched source returned by the endpoint', async () => {
  fetchMock.mockResolvedValue(response({ ...source, memoryId: 'different', content: 'WRONG_SOURCE_BODY' })); show({ ...source, sourceMemoryIds: ['first'] }); fireEvent.click(screen.getByRole('button', { name: 'View sources' })); await screen.findByText('Source unavailable'); expect(screen.queryByText('WRONG_SOURCE_BODY')).not.toBeInTheDocument();
});
it('aborts retired tenant source reads and suppresses late private content', async () => {
  let finish!: (r: Response) => void; let signal: AbortSignal | undefined;
  fetchMock.mockImplementationOnce(async (_, opts) => { signal = opts?.signal as AbortSignal; return new Promise(resolve => { finish = resolve; }); });
  const shown = show({ ...source, sourceMemoryIds: ['first'] }); fireEvent.click(screen.getByRole('button', { name: 'View sources' })); await waitFor(() => expect(finish).toBeDefined());
  fetchMock.mockResolvedValue(response({ ...source, memoryId: 'first', status: 'Deleted', content: '' }));
  shown.rerender(<App><TenantMemorySources tenant="other" agent="agent" scope="identityUser" item={{ ...source, sourceMemoryIds: ['first'] }} /></App>); await screen.findByText('Content removed'); expect(signal?.aborted).toBe(true);
  finish(response({ ...source, memoryId: 'first', content: 'RETIRED_PRIVATE_SOURCE' })); await waitFor(() => expect(screen.queryByText('RETIRED_PRIVATE_SOURCE')).not.toBeInTheDocument());
});
it('retains old single-source lineage with an exact Session query', async () => {
  render(<App><TenantMemorySources tenant="tenant" agent="agent" scope="session" session="conversation" item={{ ...source, sourceMemoryIds: undefined, supersedesMemoryId: 'first' }} /></App>);
  fireEvent.click(screen.getByRole('button', { name: 'View sources' })); await screen.findByText('Original source'); expect(String(fetchMock.mock.calls[0][0])).toBe('/api/v2/tenants/tenant/agents/agent/memory/first?scope=session&sessionId=conversation');
});
