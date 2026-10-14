import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { PendingApproval } from '../../state/sessionStore';
import { ApprovalModal } from './ApprovalModal';

const approval: PendingApproval = {
  approvalId: 'approval',
  responseId: 'response',
  operationId: 'operation',
  toolName: 'harness.instructions.update',
  effect: 'write',
  summary: 'Save instructions.update for future conversations',
  details: {
    Change: "First line\n\n<script>alert('untrusted')</script>\nLast line",
    'Applies to': 'Future conversations; this Session stays pinned.',
    'Policy revision': '2',
  },
  expiresAt: '2099-01-01T00:00:00Z',
};

describe('ApprovalModal', () => {
  it('shows an unconfirmed decision inside the dialog and keeps rejection retryable', () => {
    const reject = vi.fn();
    render(
      <ApprovalModal
        approval={{ ...approval, decisionError: 'Connection lost.' }}
        onApprove={vi.fn()}
        onReject={reject}
      />,
    );
    const dialog = screen.getByRole('dialog');
    expect(
      within(dialog).getByText(/Connection lost\. Retry a decision/),
    ).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
    expect(reject).toHaveBeenCalledOnce();
  });

  it.each(['automation.preferences.patch', 'credentials.binding.remove'])(
    'reviews %s as an exact Instance configuration change',
    (toolName) => {
      render(
        <ApprovalModal
          approval={{
            ...approval,
            toolName,
            details: {
              Target: 'This Instance',
              Before: 'Current preference',
              After: 'Requested preference',
              'Instance revision': '7',
              'Applies to': 'Next new Run',
            },
          }}
          onApprove={vi.fn()}
          onReject={vi.fn()}
        />,
      );
      const dialog = screen.getByRole('dialog', {
        name: 'Apply configuration to this Instance?',
      });
      expect(
        within(dialog).getByRole('button', { name: 'Apply to this Instance' }),
      ).toBeInTheDocument();
      expect(
        within(dialog).getByText('Requested preference'),
      ).toBeInTheDocument();
      expect(
        within(dialog).getByText('Instance revision:'),
      ).toBeInTheDocument();
    },
  );

  it('keeps the complete plain-text proposal in a keyboard-accessible review region', () => {
    const approve = vi.fn();
    const reject = vi.fn();
    render(
      <ApprovalModal
        approval={approval}
        onApprove={approve}
        onReject={reject}
      />,
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Save this harness change?',
    });
    const details = within(dialog).getByRole('region', {
      name: 'Approval details',
    });
    expect(details).toHaveAttribute('tabindex', '0');
    expect(
      within(dialog).getByText(approval.details['Applies to']),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('Policy revision:')).toBeInTheDocument();
    expect(details.querySelector('script')).toBeNull();
    expect(details.querySelector('.chat-approval-proposal')?.textContent).toBe(
      approval.details.Change,
    );
    fireEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
    expect(approve).toHaveBeenCalledOnce();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
    expect(reject).toHaveBeenCalledOnce();
  });

  it('supports an empty-details sensitive approval and removes a cleared request', async () => {
    const view = render(
      <ApprovalModal
        approval={{ ...approval, toolName: 'email.send', details: {} }}
        onApprove={vi.fn()}
        onReject={vi.fn()}
      />,
    );
    expect(
      await screen.findByRole('dialog', { name: 'Approve sensitive action' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'Approval details' }),
    ).toBeNull();
    view.rerender(
      <ApprovalModal approval={null} onApprove={vi.fn()} onReject={vi.fn()} />,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('preserves complete email text and identifies empty recipient fields', () => {
    const body = 'Hello\n\n<b>This stays plain text</b>\nLast line';
    render(
      <ApprovalModal
        approval={{
          ...approval,
          toolName: 'email.send',
          details: {
            To: 'recipient@example.test',
            Cc: '',
            Bcc: '',
            Subject: 'Hello',
            Body: body,
          },
        }}
        onApprove={vi.fn()}
        onReject={vi.fn()}
      />,
    );
    const details = screen.getByRole('region', { name: 'Approval details' });
    expect(
      within(details).getByText('recipient@example.test'),
    ).toBeInTheDocument();
    expect(within(details).getAllByLabelText('Not provided')).toHaveLength(2);
    expect(details.querySelector('b')).toBeNull();
    expect(within(details).getByText(/This stays plain text/).textContent).toBe(
      body,
    );
  });

  it('starts a replacement approval at its opening line after a previous proposal was scrolled', () => {
    const view = render(
      <ApprovalModal
        approval={approval}
        onApprove={vi.fn()}
        onReject={vi.fn()}
      />,
    );
    const oldRegion = screen.getByRole('region', { name: 'Approval details' });
    oldRegion.scrollTop = 400;
    view.rerender(
      <ApprovalModal
        approval={{
          ...approval,
          approvalId: 'replacement',
          details: { Change: 'New proposal opening line' },
        }}
        onApprove={vi.fn()}
        onReject={vi.fn()}
      />,
    );
    const region = screen.getByRole('region', { name: 'Approval details' });
    expect(region.scrollTop).toBe(0);
    expect(
      within(region).getByText('New proposal opening line'),
    ).toBeInTheDocument();
  });
});

it.each(['drive.trash', 'drive.trash_batch', 'drive.rename', 'drive.move'])(
  'shows trusted inert targets and action-specific confirmation for %s',
  (toolName) => {
    const targets = Array.from(
      { length: toolName === 'drive.trash_batch' ? 7 : 1 },
      (_, index) => ({
        fileId: `file-${index}`,
        name: `<script>untrusted-${index}</script>.txt`,
        expectedVersion: '5',
      }),
    );
    const approve = vi.fn();
    render(
      <ApprovalModal
        approval={{
          ...approval,
          toolName,
          details: {
            Account: 'Verified Drive · Synthetic',
            Files: JSON.stringify(targets),
            Scope: `${targets.length} listed app-authorized Drive files`,
          },
        }}
        onApprove={approve}
        onReject={vi.fn()}
      />,
    );
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getAllByRole('listitem')).toHaveLength(
      targets.length,
    );
    expect(dialog.querySelector('script')).toBeNull();
    for (const target of targets) {
      const row = within(dialog).getByText(target.name).closest('li')!;
      expect(within(row).getByText(`ID: ${target.fileId}`)).toBeInTheDocument();
      expect(
        within(row).getByText(`Version ${target.expectedVersion}`),
      ).toBeInTheDocument();
    }
    const verb =
      toolName === 'drive.trash_batch'
        ? 'Move 7 files to Trash'
        : toolName === 'drive.trash'
          ? 'Move to Trash'
          : toolName === 'drive.rename'
            ? 'Rename file'
            : 'Move file';
    fireEvent.click(within(dialog).getByRole('button', { name: verb }));
    expect(approve).toHaveBeenCalledOnce();
  },
);

it('keeps old incomplete Drive requests rejectable and requires fresh trusted targets', () => {
  const reject = vi.fn();
  render(
    <ApprovalModal
      approval={{ ...approval, toolName: 'drive.trash', details: {} }}
      onApprove={vi.fn()}
      onReject={reject}
    />,
  );
  const dialog = screen.getByRole('dialog');
  expect(
    within(dialog).getByRole('button', { name: 'Move to Trash' }),
  ).toBeDisabled();
  expect(
    within(dialog).getByText('Request fresh file details'),
  ).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
  expect(reject).toHaveBeenCalledOnce();
});

const validDriveTarget = {
  fileId: 'file-1',
  name: 'sample.xlsx',
  expectedVersion: '9',
};

it('preserves all twenty distinct targets at the supported metadata bounds', () => {
  const targets = Array.from({ length: 20 }, (_, index) => ({
    fileId: 'a'.repeat(158) + String(index).padStart(2, '0'),
    name: '長'.repeat(250) + '.xlsx',
    expectedVersion: '9'.repeat(64),
  }));
  render(
    <ApprovalModal
      approval={{
        ...approval,
        toolName: 'drive.trash_batch',
        details: { Files: JSON.stringify(targets) },
      }}
      onApprove={vi.fn()}
      onReject={vi.fn()}
    />,
  );
  const list = screen.getByRole('list', {
    name: 'Drive files in this request',
  });
  expect(within(list).getAllByRole('listitem')).toHaveLength(20);
  for (const target of targets)
    expect(within(list).getByText(`ID: ${target.fileId}`)).toBeInTheDocument();
  expect(within(list).getAllByText(targets[0].name)).toHaveLength(20);
  expect(
    within(list).getAllByText(`Version ${targets[0].expectedVersion}`),
  ).toHaveLength(20);
  expect(
    screen.getByRole('button', { name: 'Move 20 files to Trash' }),
  ).toBeEnabled();
});
it.each([
  ['empty ID', 'drive.trash', [{ ...validDriveTarget, fileId: '' }]],
  ['invalid ID', 'drive.trash', [{ ...validDriveTarget, fileId: 'file/1' }]],
  [
    'empty version',
    'drive.trash',
    [{ ...validDriveTarget, expectedVersion: '' }],
  ],
  [
    'control character in version',
    'drive.trash',
    [{ ...validDriveTarget, expectedVersion: '9\n' }],
  ],
  [
    'control character in filename',
    'drive.trash',
    [{ ...validDriveTarget, name: 'sample\nx.xlsx' }],
  ],
  [
    'duplicate targets',
    'drive.trash_batch',
    [validDriveTarget, validDriveTarget],
  ],
  [
    'multiple single-Trash targets',
    'drive.trash',
    [validDriveTarget, { ...validDriveTarget, fileId: 'file-2' }],
  ],
  [
    'multiple rename targets',
    'drive.rename',
    [validDriveTarget, { ...validDriveTarget, fileId: 'file-2' }],
  ],
  [
    'multiple move targets',
    'drive.move',
    [validDriveTarget, { ...validDriveTarget, fileId: 'file-2' }],
  ],
] as const)(
  'requires fresh details for %s and keeps rejection available',
  (_case, toolName, targets) => {
    const approve = vi.fn();
    const reject = vi.fn();
    render(
      <ApprovalModal
        approval={{
          ...approval,
          toolName,
          details: { Files: JSON.stringify(targets) },
        }}
        onApprove={approve}
        onReject={reject}
      />,
    );
    const dialog = screen.getByRole('dialog');
    expect(
      within(dialog).getByRole('button', {
        name: /^(Move to Trash|Rename file|Move file)$/,
      }),
    ).toBeDisabled();
    expect(
      within(dialog).getByText('Request fresh file details'),
    ).toBeInTheDocument();
    expect(approve).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reject' }));
    expect(reject).toHaveBeenCalledOnce();
  },
);
