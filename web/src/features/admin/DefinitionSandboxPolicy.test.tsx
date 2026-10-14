import { useState } from 'react';
import { ConfigProvider } from 'antd';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { DefinitionSandboxPolicy } from './DefinitionSandboxPolicy';
import { getSandboxPlatform, type SandboxPlatform } from '../../services/sandboxes';
import type { DefinitionCandidate } from './definitionCandidate';
vi.mock('../../services/sandboxes', () => ({ getSandboxPlatform: vi.fn() }));
const profile = { id: 'fixture', revision: 2, name: 'Fixture', description: '', enabled: true, imageReference: 'sha256:' + 'a'.repeat(64), initializationScript: 'echo Profile', cpu: 1, memoryMiB: 1024, pids: 64, maxCommandSeconds: 120, idleTtlSeconds: 1800, maxLifetimeSeconds: 7200, storageBytes: 262144000, lastVerifiedAt: null, verificationReason: null };
const platform: SandboxPlatform = { state: 'Ready', message: 'Authenticated.', profiles: [profile, { ...profile, id: 'other', name: 'Other' }], allowedImagePrefixes: [], localImageIdsAllowed: true, securityScope: '', storageScope: '' };
const candidate: DefinitionCandidate = { sandboxPolicy: { defaultProfileId: 'fixture', allowedProfileIds: ['fixture', 'other'] } };
const tree = (readOnly = false, onChange = vi.fn()) => <ConfigProvider theme={{ token: { motion: false } }}><DefinitionSandboxPolicy candidate={candidate} readOnly={readOnly} onChange={onChange} /></ConfigProvider>;
beforeEach(() => vi.mocked(getSandboxPlatform).mockReset().mockResolvedValue(platform));
it('preserves grants and fences both selectors when inventory fails, then recovers on retry', async () => {
  vi.mocked(getSandboxPlatform).mockRejectedValueOnce(new Error('private error'));
  const change = vi.fn(); render(tree(false, change));
  await screen.findByText('Profile inventory unavailable');
  expect(screen.getByRole('combobox', { name: 'Allowed sandbox profiles' })).toBeDisabled();
  expect(screen.getByRole('combobox', { name: 'Default sandbox profile' })).toBeDisabled();
  expect(change).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Retry profiles' }));
  await screen.findByText('Fixture · revision 2');
  expect(screen.getByRole('combobox', { name: 'Default sandbox profile' })).not.toBeDisabled();
  expect(screen.queryByText('private error')).not.toBeInTheDocument();
});
it('keeps the default within remaining allowed grants and clears policy when the final grant is removed', async () => {
  function Editor() {
    const [value, setValue] = useState(candidate);
    return <ConfigProvider theme={{ token: { motion: false } }}><DefinitionSandboxPolicy candidate={value} onChange={setValue} /><output>{JSON.stringify(value.sandboxPolicy)}</output></ConfigProvider>;
  }
  render(<Editor />); await screen.findByText('Fixture · revision 2');
  const remove = (name: string) => {
    const item = Array.from(document.querySelectorAll('.ant-select-selection-item')).find(element => element.getAttribute('title') === name)!;
    fireEvent.mouseDown(item.querySelector('.ant-select-selection-item-remove')!);
    fireEvent.click(item.querySelector('.ant-select-selection-item-remove')!);
  };
  remove('Fixture'); await screen.findByText('{"allowedProfileIds":["other"],"defaultProfileId":"other"}');
  remove('Other'); expect(screen.getByText('null')).toBeInTheDocument();
});
it('inspects immutable published policy without edit or verification controls', async () => {
  render(tree(true)); await screen.findByText('Fixture · revision 2');
  expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /Verify/ })).not.toBeInTheDocument();
  expect(screen.getByText('Fixture, Other')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Manage Global Sandbox Profiles' })).toHaveAttribute('href', '/admin/platform/sandboxes');
});
it('ignores a late failed inventory response after a different owner is mounted', async () => {
  let reject!: (reason: Error) => void;
  vi.mocked(getSandboxPlatform).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
  const view = render(tree()); await waitFor(() => expect(getSandboxPlatform).toHaveBeenCalledTimes(1));
  view.unmount(); render(tree(true)); await screen.findByText('Fixture · revision 2');
  await act(async () => reject(new Error('late')));
  expect(screen.queryByText('Profile inventory unavailable')).not.toBeInTheDocument();
});
