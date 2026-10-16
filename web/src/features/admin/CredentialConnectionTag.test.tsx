import { render, screen } from '@testing-library/react';
import { CredentialConnectionTag } from './CredentialConnectionTag';
import type { SystemCredential } from '../../services/adminApi';
const account: SystemCredential = { credentialId: 'fixture', displayName: 'Fixture', kind: 'Token', status: 'Disabled', metadata: {}, allowedOrigins: [], revision: 1,
  createdAtUtc: '', updatedAtUtc: '', bindingCount: 0, emailAccount: { preset: 'Gmail', emailAddress: 'fixture@example.test', imapUsername: '', smtpUsername: '', imapEndpoint: null, smtpEndpoint: null, connectionStatus: 'Connected' } };
it('can display connection state independently from disabled credential lifecycle while preserving legacy defaults', () => {
  const view = render(<CredentialConnectionTag account={account} />); expect(screen.getByText('Disabled')).toBeVisible();
  view.rerender(<CredentialConnectionTag account={account} showCredentialStatus={false} />);
  expect(screen.getByText('Connected')).toBeVisible(); expect(screen.queryByText('Disabled')).toBeNull();
});
