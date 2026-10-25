import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf } from '../../services/identity';
import { modelPreset } from '../../test/modelFixtures';
import { SystemModelPresets } from './SystemModelPresets';
const fetchMock = vi.fn<typeof fetch>();
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => {
  resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => String(path).endsWith('/csrf') ? response({ token: 'csrf' }) : response(options?.method === 'PATCH' ? { ...modelPreset, revision: 2 } : [{ ...modelPreset, offeringReferences: 3 }]));
});
it('offers read-only inspection without protected-key controls or mutation actions', async () => {
  render(<App><SystemModelPresets permissions={['system.model-presets.read']} /></App>);
  await screen.findByText(modelPreset.label, { exact: true });
  expect(screen.getByText('3 registrations', { exact: true })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Add model preset' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Inspect preset GPT-6 Luna' }));
  expect(screen.getByLabelText('Preset name')).toBeDisabled();
  expect(screen.queryByRole('button', { name: 'Save preset' })).not.toBeInTheDocument();
  expect(screen.queryByLabelText('Protected value')).not.toBeInTheDocument();
});
it('writes metadata with an exact preset witness and strips read-only capability metadata', async () => {
  render(<App><SystemModelPresets permissions={['system.model-presets.read', 'system.model-presets.manage']} /></App>);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit preset GPT-6 Luna' }));
  fireEvent.change(screen.getByLabelText('Preset name'), { target: { value: 'Reviewed preset label' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save preset' }));
  await waitFor(() => expect(fetchMock.mock.calls.some(([, options]) => options?.method === 'PATCH')).toBe(true));
  const written = fetchMock.mock.calls.find(([, options]) => options?.method === 'PATCH')!;
  expect(String(written[0])).toBe('/api/v2/system/model-presets/' + modelPreset.presetId);
  const body = JSON.parse(String(written[1]?.body));
  expect(body).toMatchObject({ label: 'Reviewed preset label', expectedRevision: 1, enabled: true, archived: false });
  expect(body.capabilities).not.toHaveProperty('source'); expect(body.capabilities).not.toHaveProperty('verifiedAt');
  expect(body).not.toHaveProperty('credentialId');
});
it('drops the platform collection after a denied read', async () => {
  fetchMock.mockResolvedValue(response({ reasonCode: 'permission_denied' }, 403));
  render(<App><SystemModelPresets permissions={['system.model-presets.read']} /></App>);
  await screen.findByRole('alert'); expect(screen.queryByText(modelPreset.label)).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Add model preset' })).not.toBeInTheDocument();
});
