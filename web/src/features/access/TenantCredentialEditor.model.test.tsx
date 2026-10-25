import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { TenantCredentialEditor } from './TenantCredentialEditor';
import { identityRequest, IdentityError } from '../../services/identity';
import type { TenantCredential } from '../../services/tenantCredentials';

vi.mock('../../services/identity', async original => ({
  ...await original<typeof import('../../services/identity')>(), identityRequest: vi.fn()
}));
const tenant = '11111111-1111-4111-8111-111111111111';
const saved: TenantCredential = { tenantId: tenant, credential: {
  credentialId: '22222222-2222-4222-8222-222222222222', displayName: 'Native provider key', kind: 'ApiKey',
  purpose: 'ModelInference', status: 'Active', metadata: {}, allowedOrigins: [], bindingCount: 0, revision: 1,
  createdAtUtc: '2026-10-25T00:00:00Z', updatedAtUtc: '2026-10-25T00:00:00Z'
} };
beforeEach(() => { vi.mocked(identityRequest).mockReset(); });
async function submit() {
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Native provider key' } });
  fireEvent.change(screen.getByLabelText('Protected value'), { target: { value: 'offline-fixture-private-key' } });
  fireEvent.click(screen.getByRole('button', { name: /^Save credential$/ }));
}
describe('protected model-inference credential authoring', () => {
  it('uses the existing owner with fixed API-key kind, no origins, and a fresh tenant revision', async () => {
    vi.mocked(identityRequest).mockImplementation(async path => path.endsWith('/access/me') ? { revision: 17 } as never : saved as never);
    const committed = vi.fn();
    render(<App><TenantCredentialEditor tenant={tenant} organization="Test organization" initialPurpose="ModelInference" onClose={vi.fn()} onSaved={committed} /></App>);
    expect(screen.queryByLabelText('Allowed origins')).not.toBeInTheDocument();
    await submit();
    await waitFor(() => expect(committed).toHaveBeenCalledWith(saved));
    const call = vi.mocked(identityRequest).mock.calls.find(([, options]) => options?.method === 'POST')!;
    expect(call[0]).toBe(`/api/v2/tenants/${tenant}/credentials`);
    expect(call[1]?.body).toMatchObject({ purpose: 'ModelInference', kind: 'ApiKey', allowedOrigins: [], expectedRevision: 17, protectedValue: 'offline-fixture-private-key' });
    expect(JSON.stringify(saved)).not.toContain('offline-fixture-private-key');
  });

  it('clears protected input and retains safe edits after a refused save', async () => {
    vi.mocked(identityRequest).mockImplementation(async path => {
      if (path.endsWith('/access/me')) return { revision: 17 } as never;
      throw new IdentityError(409, 'stale_revision');
    });
    render(<App><TenantCredentialEditor tenant={tenant} organization="Test organization" initialPurpose="ModelInference" onClose={vi.fn()} onSaved={vi.fn()} /></App>);
    await submit();
    await waitFor(() => expect(screen.getByText(/Your draft is retained/)).toBeInTheDocument());
    expect(screen.getByLabelText('Protected value')).toHaveValue('');
    expect(screen.getByLabelText('Display name')).toHaveValue('Native provider key');
    expect(screen.queryByLabelText('Allowed origins')).not.toBeInTheDocument();
  });
});
