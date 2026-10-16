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
  const exactAction = (toolName = 'automation.create', changes: Record<string, unknown> = {}) => JSON.stringify({
    tool: toolName,
    AgentInstanceId: 'private-instance-id',
    SessionId: 'this-session',
    arguments: { name: 'Daily review', instructions: 'Review experience safely.', kind: 'daily', localTime: '09:00', timeZone: 'Asia/Ho_Chi_Minh', executionTarget: 'backgroundSession', reportBack: false, ...changes },
    resolvedSchedule: { Kind: 1, IntervalDays: 1, LocalTime: '09:00:00', TimeZoneId: 'Asia/Ho_Chi_Minh' },
    executionTarget: { Kind: 0, SessionId: null },
    completionDelivery: { SessionId: null },
    proposedEnabled: true,
  }, null, 2);
  const automation = (toolName = 'automation.create', raw = exactAction(toolName)): PendingApproval => ({
    ...approval, toolName, summary: 'Review exact Automation change', details: { Reason: 'Semantic verification could not complete.', 'Exact action': raw },
  });

  it('puts the complete task and schedule ahead of technical metadata', () => {
    const approve = vi.fn();
    const instructions = 'Review experience safely. '.repeat(30) + '<script>untrusted</script>';
    const raw = exactAction('automation.create', { instructions });
    render(<ApprovalModal approval={automation('automation.create', raw)} onApprove={approve} onReject={vi.fn()} />);
    const dialog = screen.getByRole('dialog', { name: 'Create automation?' });
    expect(within(dialog).getByText(instructions)).toBeInTheDocument();
    expect(within(dialog).getByText('Every day')).toBeInTheDocument();
    expect(within(dialog).getByText('09:00')).toBeInTheDocument();
    expect(within(dialog).getByText('Asia/Ho_Chi_Minh')).toBeInTheDocument();
    expect(within(dialog).getByText('Background work')).toBeInTheDocument();
    expect(within(dialog).getByText('No report back')).toBeInTheDocument();
    expect(dialog.textContent).not.toContain('private-instance-id');
    expect(dialog.textContent).not.toContain('Semantic verification');
    expect(dialog.querySelector('script')).toBeNull();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Technical details' }));
    expect(Array.from(dialog.querySelectorAll('pre')).some(element => element.textContent === raw)).toBe(true);
    expect(dialog.querySelector('script')).toBeNull();
    expect(approve).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create automation' }));
    expect(approve).toHaveBeenCalledOnce();
  });

  it.each([
    ['automation.update', 'Save automation changes?', 'Save changes'],
    ['automation.enable', 'Enable automation?', 'Enable automation'],
    ['automation.disable', 'Disable automation?', 'Disable automation'],
    ['automation.delete', 'Delete automation?', 'Delete automation'],
    ['automation.run', 'Run automation now?', 'Run now'],
  ])('names the exact %s decision', (tool, title, button) => {
    const approve = vi.fn();
    render(<ApprovalModal approval={automation(tool)} onApprove={approve} onReject={vi.fn()} />);
    const dialog = screen.getByRole('dialog', { name: title });
    expect(approve).not.toHaveBeenCalled();
    if (tool === 'automation.run') expect(within(dialog).getByText('Once, now')).toBeInTheDocument();
    if (tool === 'automation.delete') expect(within(dialog).getByText(/Stops future runs/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: button }));
    expect(approve).toHaveBeenCalledOnce();
  });

  it('keeps unknown proposed settings visible and preserves changed instructions', () => {
    const raw = JSON.parse(exactAction('automation.update', { instructions: 'New instructions', futureSetting: 'An explicit new setting' }));
    raw.current = { name: 'Original name', instructions: 'Original instructions', status: 'Disabled' };
    render(<ApprovalModal approval={automation('automation.update', JSON.stringify(raw))} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.getByText('New instructions')).toBeInTheDocument();
    expect(screen.getByText('Previously: Original name')).toBeInTheDocument();
    expect(screen.getByText('An explicit new setting')).toBeInTheDocument();
    expect(screen.getByText('Previously: Disabled')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Previous instructions' }));
    expect(screen.getByText('Original instructions')).toBeInTheDocument();
  });

  it('shows resolved weekly limits and destination without inferring defaults', () => {
    const raw = JSON.parse(exactAction());
    raw.resolvedSchedule = { Kind: 2, IntervalWeeks: 2, Weekdays: [1, 5], LocalTime: '09:30:00', TimeZoneId: 'Asia/Ho_Chi_Minh', StartDate: '2026-10-17', EndDate: '2026-12-31', MaxOccurrences: 8 };
    raw.executionTarget = { Kind: 1, SessionId: 'this-session' };
    raw.completionDelivery = { SessionId: 'another-conversation' };
    render(<ApprovalModal approval={automation('automation.create', JSON.stringify(raw))} onApprove={vi.fn()} onReject={vi.fn()} />);
    for (const value of ['Every 2 weeks', 'Monday, Friday', '09:30', '2026-10-17', '2026-12-31', '8', 'This conversation', 'Conversation another-conversation'])
      expect(screen.getByText(value)).toBeInTheDocument();
  });

  it('shows every Event trigger and its filter, dispatch and disabled state', () => {
    const raw = JSON.parse(exactAction());
    delete raw.resolvedSchedule;
    delete raw.arguments.kind;
    raw.resolvedTriggers = [
      { Enabled: true, Configuration: { CoreEventKey: 'run.completed', FilterExpression: 'event.data.ok === true', Dispatch: { Mode: 1, WindowSeconds: 120 } } },
      { Enabled: false, Configuration: { EventId: 'orders-event-id', Dispatch: { Mode: 0 } } },
    ];
    render(<ApprovalModal approval={automation('automation.create', JSON.stringify(raw))} onApprove={vi.fn()} onReject={vi.fn()} />);
    for (const value of ['Built-in Event · run.completed', 'event.data.ok === true', 'Coalesce latest · 120 seconds', 'Trigger 2 · Disabled', 'Webhook · orders-event-id', 'Every match'])
      expect(screen.getByText(value)).toBeInTheDocument();
  });

  it('presents the proposed Event source rather than the previous source on update', () => {
    const raw = JSON.parse(exactAction('automation.update', { coreEventKey: 'run.failed', filterExpression: 'event.data.retry === true' }));
    delete raw.arguments.kind;
    delete raw.resolvedSchedule;
    raw.current = { name: 'Existing automation', triggers: [{ kind: 'event', source: { kind: 'builtin', key: 'run.completed' } }] };
    render(<ApprovalModal approval={automation('automation.update', JSON.stringify(raw))} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.getByText('Built-in Event · run.failed')).toBeInTheDocument();
    expect(screen.queryByText('Built-in Event · run.completed')).not.toBeInTheDocument();
    expect(screen.getAllByText('event.data.retry === true').length).toBeGreaterThan(0);
  });

  it('shows clearing an Event filter as No filter and does not duplicate it', () => {
    const raw = JSON.parse(exactAction('automation.update', { coreEventKey: 'run.failed', filterExpression: null, modelKey: null }));
    delete raw.arguments.kind;
    delete raw.resolvedSchedule;
    raw.current = { name: 'Existing automation', triggers: [{ kind: 'event', source: { kind: 'builtin', key: 'run.completed' }, filterExpression: 'old condition' }] };
    render(<ApprovalModal approval={automation('automation.update', JSON.stringify(raw))} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.getAllByText('No filter')).toHaveLength(1);
    expect(screen.getByText('Instance default')).toBeInTheDocument();
    expect(screen.queryByText('old condition')).not.toBeInTheDocument();
  });

  it('exposes malformed details without inventing a readable proposal', () => {
    const raw = '{invalid <script>untrusted</script>';
    render(<ApprovalModal approval={automation('automation.create', raw)} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.getByText('Review the full request')).toBeInTheDocument();
    expect(screen.getByText(raw)).toBeInTheDocument();
    expect(document.querySelector('script')).toBeNull();
  });

  it('keeps rejection available when the exact action is missing', () => {
    const reject = vi.fn();
    render(<ApprovalModal approval={automation('automation.create', '')} onApprove={vi.fn()} onReject={reject} />);
    expect(screen.getByRole('button', { name: 'Create automation' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    expect(reject).toHaveBeenCalledOnce();
  });

  it('resets technical disclosure for a different pending action', () => {
    const props = { onApprove: vi.fn(), onReject: vi.fn() };
    const view = render(<ApprovalModal {...props} approval={automation()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Technical details' }));
    view.rerender(<ApprovalModal {...props} approval={{ ...automation(), approvalId: 'next-approval' }} />);
    expect(screen.getByRole('button', { name: 'Technical details' })).toHaveAttribute('aria-expanded', 'false');
    expect(props.onApprove).not.toHaveBeenCalled();
  });

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
    const table = within(dialog).getByRole('table', {
      name: 'Drive files in this request',
    });
    expect(within(table).getAllByRole('row')).toHaveLength(targets.length + 1);
    for (const header of ['Name', 'File ID', 'Version'])
      expect(
        within(table).getByRole('columnheader', { name: header }),
      ).toBeInTheDocument();
    expect(dialog.querySelector('script')).toBeNull();
    for (const target of targets) {
      const row = within(dialog).getByText(target.name).closest('tr')!;
      expect(within(row).getByText(target.fileId)).toBeInTheDocument();
      expect(within(row).getByText(target.expectedVersion)).toBeInTheDocument();
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

it('keeps exact IDs with filenames in the narrow two-column table', () => {
  const original = window.matchMedia;
  const media = vi
    .spyOn(window, 'matchMedia')
    .mockImplementation((query) => ({
      ...original(query),
      matches: query.includes('max-width'),
    }));
  try {
    render(
      <ApprovalModal
        approval={{
          ...approval,
          toolName: 'drive.trash',
          details: { Files: JSON.stringify([validDriveTarget]) },
        }}
        onApprove={vi.fn()}
        onReject={vi.fn()}
      />,
    );
    const table = screen.getByRole('table', {
      name: 'Drive files in this request',
    });
    expect(
      within(table).queryByRole('columnheader', { name: 'File ID' }),
    ).toBeNull();
    const row = within(table).getByText(validDriveTarget.name).closest('tr')!;
    expect(
      within(row).getByText(`ID: ${validDriveTarget.fileId}`),
    ).toBeInTheDocument();
    expect(
      within(row).getByText(validDriveTarget.expectedVersion),
    ).toBeInTheDocument();
  } finally {
    media.mockRestore();
  }
});

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
  const list = screen.getByRole('table', {
    name: 'Drive files in this request',
  });
  expect(within(list).getAllByRole('row')).toHaveLength(21);
  for (const target of targets)
    expect(within(list).getByText(target.fileId)).toBeInTheDocument();
  expect(within(list).getAllByText(targets[0].name)).toHaveLength(20);
  expect(within(list).getAllByText(targets[0].expectedVersion)).toHaveLength(
    20,
  );
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
