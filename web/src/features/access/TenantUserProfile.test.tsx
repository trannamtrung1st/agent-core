import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantUserProfile } from './TenantUserProfile';
const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Private organization', slug: 'private' }, userId: 'user', principalId: 'person', permissions: [], roles: [], revision: 7 };
const fresh = () => ({ profile: { tenantId: 'tenant', principalId: 'person', revision: 0, updatedAt: new Date().toISOString(), preferences: {} as Record<string, { value: string; source: string; updatedAt: string }> }, securityRevision: 7 });
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let saved: ReturnType<typeof fresh>; let failure: number; let commands: Record<string, unknown>[];
beforeEach(() => { saved = fresh(); failure = 0; commands = []; resetIdentityCsrf(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); fetchMock.mockImplementation(async (path, options) => {
  if (String(path).endsWith('/csrf')) return response({ token: 'test-token' });
  if (options?.method === 'PATCH') { const body = JSON.parse(options.body as string) as { values: Record<string, string | null> }; commands.push(body); if (failure) return response({ reasonCode: 'stale_revision' }, failure);
    for (const [key, value] of Object.entries(body.values)) if (value === null) delete saved.profile.preferences[key]; else saved.profile.preferences[key] = { value, source: 'UserSet', updatedAt: new Date().toISOString() };
    saved.profile.revision++; saved.securityRevision++; return response(saved); }
  return response(saved);
}); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantUserProfile context={context} /></App>); await screen.findByLabelText('Preferred name'); };
it('starts empty without seeding and submits only deliberate profile fields with both revisions', async () => {
  await open(); expect(screen.getByLabelText('Preferred name')).toHaveValue(''); expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Preferred name'), { target: { value: 'Customer name' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
  await screen.findByText('Profile saved'); expect(commands).toEqual([{ expectedRevision: 7, expectedProfileRevision: 0, values: { preferredName: 'Customer name' } }]);
  expect(screen.getByLabelText('Preferred name')).toHaveValue('Customer name');
});
it('clears a saved value with null while retaining other saved fields', async () => {
  saved.profile.revision = 2; saved.profile.preferences.language = { value: 'en', source: 'UserSet', updatedAt: new Date().toISOString() }; saved.profile.preferences.locale = { value: 'en-GB', source: 'UserSet', updatedAt: new Date().toISOString() };
  await open(); await waitFor(() => expect(screen.getByLabelText('Language')).toHaveValue('en'));
  fireEvent.change(screen.getByLabelText('Language'), { target: { value: '' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await screen.findByText('Profile saved');
  expect(commands[0]).toEqual({ expectedRevision: 7, expectedProfileRevision: 2, values: { language: null } }); expect(saved.profile.preferences.locale.value).toBe('en-GB');
});
it('retains a conflicting draft, reads current values and requires a deliberate retry with fresh revisions', async () => {
  await open(); fireEvent.change(screen.getByLabelText('Preferred name'), { target: { value: 'Retained draft' } }); failure = 409; fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
  await screen.findByText('Read current saved values before another save'); expect(screen.getByLabelText('Preferred name')).toHaveValue('Retained draft'); expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled();
  failure = 0; saved.profile.revision = 4; saved.securityRevision = 10; saved.profile.preferences.preferredName = { value: 'Other saved value', source: 'UserSet', updatedAt: new Date().toISOString() };
  fireEvent.click(screen.getByRole('button', { name: 'Reload saved profile' })); await screen.findByText('Saved: Other saved value'); expect(screen.getByLabelText('Preferred name')).toHaveValue('Retained draft'); expect(commands).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Save profile' })); await screen.findByText('Profile saved'); expect(commands[1]).toEqual({ expectedRevision: 10, expectedProfileRevision: 4, values: { preferredName: 'Retained draft' } });
});
it('aborts an old organization and cannot render its late private fields', async () => {
  let finish!: (r: Response) => void; let signal: AbortSignal | undefined;
  fetchMock.mockImplementationOnce(async (_, options) => { signal = options?.signal as AbortSignal; return new Promise(resolve => { finish = resolve; }); });
  const shown = render(<App><TenantUserProfile context={context} /></App>); await waitFor(() => expect(finish).toBeDefined());
  shown.rerender(<App><TenantUserProfile context={{ ...context, tenant: { ...context.tenant, tenantId: 'other' } }} /></App>); await screen.findByLabelText('Preferred name'); expect(signal?.aborted).toBe(true);
  const old = fresh(); old.profile.preferences.preferredName = { value: 'RETIRED_PRIVATE_NAME', source: 'UserSet', updatedAt: new Date().toISOString() }; finish(response(old));
  await waitFor(() => expect(screen.getByLabelText('Preferred name')).toHaveValue('')); expect(screen.queryByDisplayValue('RETIRED_PRIVATE_NAME')).not.toBeInTheDocument();
});
it('retains the draft after an unreadable successful response and never automatically repeats it', async () => {
  await open(); fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return response({ token: 'test-token' });
    if (options?.method === 'PATCH') { commands.push(JSON.parse(options.body as string)); return new Response('unreadable success', { status: 200 }); }
    return response(saved);
  });
  fireEvent.change(screen.getByLabelText('Preferred name'), { target: { value: 'Uncertain draft' } }); fireEvent.click(screen.getByRole('button', { name: 'Save profile' }));
  await screen.findByText('Read current saved values before another save'); expect(screen.getByLabelText('Preferred name')).toHaveValue('Uncertain draft'); expect(screen.getByRole('button', { name: 'Save profile' })).toBeDisabled(); expect(commands).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Reload saved profile' })); await screen.findByLabelText('Preferred name'); expect(screen.getByLabelText('Preferred name')).toHaveValue('Uncertain draft'); expect(commands).toHaveLength(1);
});
