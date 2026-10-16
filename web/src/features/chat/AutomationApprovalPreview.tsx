import { Alert, Collapse, Descriptions, Flex, Grid, Typography, theme } from 'antd';
import type { PendingApproval } from '../../state/sessionStore';

type JsonObject = Record<string, unknown>;
type Field = { label: string; value: string; previous?: string };

export const automationApprovalActions: Record<string, { title: string; confirm: string; danger?: boolean }> = {
  'automation.create': { title: 'Create automation?', confirm: 'Create automation' },
  'automation.update': { title: 'Save automation changes?', confirm: 'Save changes' },
  'automation.enable': { title: 'Enable automation?', confirm: 'Enable automation' },
  'automation.disable': { title: 'Disable automation?', confirm: 'Disable automation' },
  'automation.delete': { title: 'Delete automation?', confirm: 'Delete automation', danger: true },
  'automation.run': { title: 'Run automation now?', confirm: 'Run now' },
};

function object(value: unknown): JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : {};
}
function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
function pick(value: JsonObject, ...keys: string[]): unknown {
  return keys.map(key => value[key]).find(item => item !== undefined && item !== null);
}
function display(value: unknown): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (value === null) return 'None';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

function instant(value: unknown, zone: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  try {
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) return value;
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone: typeof zone === 'string' ? zone : 'UTC' }).format(parsed);
  } catch { return value; }
}

const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
function scheduleFields(value: JsonObject): Field[] {
  const rawKind = pick(value, 'kind', 'Kind');
  const kind = typeof rawKind === 'number' ? ['oneShot', 'daily', 'weekly', 'fixedInterval'][rawKind] : rawKind;
  const interval = pick(value, 'IntervalDays', 'IntervalWeeks', 'IntervalSeconds', 'intervalSeconds', 'interval');
  const localTime = text(pick(value, 'localTime', 'LocalTime'));
  const fields: Field[] = [];
  if (kind === 'daily' || kind === 'weekly') {
    const unit = kind === 'daily' ? 'day' : 'week';
    fields.push({ label: 'Repeats', value: interval === 1 ? `Every ${unit}` : interval === undefined ? display(kind) : `Every ${display(interval)} ${unit}s` });
    if (kind === 'weekly') {
      const days = pick(value, 'weekdays', 'Weekdays');
      if (Array.isArray(days)) fields.push({ label: 'Weekdays', value: days.map(day => typeof day === 'number' ? weekdays[day] ?? String(day) : display(day)).join(', ') });
    }
    if (localTime) fields.push({ label: 'Time', value: /^\d{2}:\d{2}:00$/.test(localTime) ? localTime.slice(0, 5) : localTime });
  } else if (kind === 'fixedInterval' || kind === 'fixed_interval') {
    if (interval !== undefined) fields.push({ label: 'Repeats', value: `Every ${display(interval)} seconds` });
    fields.push({ label: 'Timezone', value: 'UTC' });
  } else if (kind === 'oneShot' || kind === 'one_shot' || value.AtUtc || value.atUtc) {
    fields.push({ label: 'When', value: instant(pick(value, 'atUtc', 'AtUtc'), pick(value, 'timeZone', 'TimeZoneId')) ?? 'Once' });
  }
  for (const [label, keys] of [
    ['Timezone', ['timeZone', 'TimeZoneId']], ['Starts', ['startDate', 'StartDate', 'anchorAtUtc', 'AnchorAtUtc']],
    ['Ends', ['endDate', 'EndDate', 'endAtUtc', 'EndAtUtc']], ['Run limit', ['maxOccurrences', 'MaxOccurrences']],
  ] as const) {
    const item = pick(value, ...keys);
    if (item !== undefined) fields.push({ label, value: typeof item === 'string' && item.includes('T')
      ? instant(item, pick(value, 'timeZone', 'TimeZoneId')) ?? item : display(item) });
  }
  return fields;
}

function triggerFields(value: JsonObject): Field[] {
  const schedule = object(value.schedule);
  const config = object(value.Configuration);
  if (Object.keys(schedule).length) return scheduleFields(schedule);
  if (config.Schedule) return scheduleFields(object(config.Schedule));
  const source = object(value.source);
  const event = pick(source, 'key', 'eventId') ?? pick(config, 'CoreEventKey', 'EventId') ?? pick(value, 'coreEventKey', 'eventId', 'CoreEventKey');
  const fields: Field[] = event === undefined ? [] : [{ label: 'When', value: `${source.kind === 'webhook' || value.eventId || config.EventId ? 'Webhook' : 'Built-in Event'} · ${display(event)}` }];
  const filter = Object.hasOwn(value, 'filterExpression') ? value.filterExpression : config.FilterExpression;
  if (filter !== undefined) fields.push({ label: 'Event filter', value: filter === null ? 'No filter' : display(filter) });
  const dispatch = object(value.dispatch ?? config.Dispatch ?? { mode: value.dispatchMode, windowSeconds: value.windowSeconds });
  const mode = pick(dispatch, 'mode', 'Mode');
  if (mode !== undefined) fields.push({ label: 'Event dispatch', value: mode === 0 || mode === 'everyMatch' ? 'Every match' : mode === 1 || mode === 'coalesceLatest' ? `Coalesce latest · ${display(pick(dispatch, 'windowSeconds', 'WindowSeconds'))} seconds` : display(mode) });
  return fields;
}

function destination(value: JsonObject, sessionId: unknown): string | undefined {
  const kind = pick(value, 'kind', 'Kind');
  if (kind === 0 || kind === 'backgroundSession') return 'Background work';
  if (kind === 1 || kind === 'existingSession') {
    const target = text(pick(value, 'sessionId', 'SessionId'));
    return target === sessionId ? 'This conversation' : target ? `Conversation ${target}` : undefined;
  }
  return undefined;
}

function preview(approval: PendingApproval) {
  const raw = approval.details['Exact action'];
  if (!raw) return null;
  try {
    const exact = object(JSON.parse(raw));
    if (exact.tool !== approval.toolName || !Object.keys(object(exact.arguments)).length) return null;
    const args = object(exact.arguments);
    const current = object(exact.current);
    const fields: Field[] = [];
    const add = (label: string, value: unknown, previous?: unknown) => {
      if (value !== undefined) fields.push({ label, value: display(value), previous: previous !== undefined && display(previous) !== display(value) ? display(previous) : undefined });
    };
    const currentTriggers = Array.isArray(current.triggers) ? current.triggers.map(trigger => {
      const original = object(trigger);
      const dispatch = object(original.dispatch);
      return { ...original,
        ...(Object.hasOwn(args, 'filterExpression') ? { filterExpression: args.filterExpression } : {}),
        dispatch: { ...dispatch, ...(args.dispatchMode !== undefined ? { mode: args.dispatchMode } : {}),
          ...(args.windowSeconds !== undefined ? { windowSeconds: args.windowSeconds } : {}) },
      };
    }) : [];
    const directSource = args.coreEventKey !== undefined || args.eventId !== undefined;
    const triggers = Array.isArray(exact.resolvedTriggers) ? exact.resolvedTriggers : Array.isArray(args.triggers) ? args.triggers : currentTriggers;
    const timing = object(exact.resolvedSchedule);
    if (approval.toolName === 'automation.run') add('When', 'Once, now');
    else if (Object.keys(timing).length) fields.push(...scheduleFields(timing));
    else if (args.kind) fields.push(...scheduleFields(args));
    else if (directSource || !triggers.length) {
      const eventFields = triggerFields(args);
      fields.push(...(eventFields.length ? eventFields : triggerFields(object(exact.resolvedTriggers))));
    }
    const target = destination(object(exact.executionTarget), exact.SessionId)
      ?? (args.executionTarget === 'currentSession' ? 'This conversation' : args.executionTarget === 'backgroundSession' ? 'Background work' : destination(object(current.executionTarget), exact.SessionId));
    add('Runs in', target);
    const delivery = object(exact.completionDelivery ?? current.completionDelivery);
    const reportSession = pick(delivery, 'SessionId', 'sessionId');
    if (typeof args.reportBack === 'boolean' || Object.keys(delivery).length) {
      add('Report back', typeof reportSession === 'string' ? reportSession === exact.SessionId ? 'This conversation' : `Conversation ${reportSession}` : args.reportBack === true ? 'This conversation' : 'No report back');
    }
    const enabled = approval.toolName === 'automation.enable' ? true : approval.toolName === 'automation.disable' ? false : exact.proposedEnabled ?? args.enabled;
    if (typeof enabled === 'boolean') add('Status after approval', enabled ? 'Active' : 'Disabled', current.status);
    const handled = new Set(['name', 'instructions', 'kind', 'localTime', 'timeZone', 'interval', 'intervalSeconds', 'weekdays', 'startDate', 'endDate', 'endAtUtc', 'maxOccurrences', 'atUtc', 'relativeDelaySeconds', 'relativeDayOffset', 'localDate', 'executionTarget', 'reportBack', 'enabled', 'automationId', 'expectedRevision', 'triggers', 'coreEventKey', 'eventId']);
    const labels: Record<string, string> = { requiresTools: 'Requires tools', requiresVision: 'Requires vision', modelKey: 'Model', reasoningEffort: 'Reasoning effort', filterExpression: 'Event filter', dispatchMode: 'Event dispatch', windowSeconds: 'Coalescing window (seconds)', presetId: 'Preset' };
    if (directSource || triggers.length) {
      handled.add('filterExpression');
      handled.add('dispatchMode');
      handled.add('windowSeconds');
    }
    for (const [key, value] of Object.entries(args)) {
      if (handled.has(key)) continue;
      const proposed = value === null && (key === 'modelKey' || key === 'reasoningEffort') ? 'Instance default'
        : value === null && key === 'filterExpression' ? 'No filter'
        : key === 'requiresTools' && exact.presetRequiresTools === true ? true : value;
      add(Object.hasOwn(labels, key) ? labels[key] : key, proposed, current[key]);
    }
    if (exact.presetRequiresTools === true && args.requiresTools === undefined) add('Requires tools', true);
    // Unknown settings remain visible; this projection never changes the approved payload.
    if (!Object.keys(timing).length && !args.kind && !triggers.length && !fields.some(field => field.label === 'When') && approval.toolName === 'automation.create') return null;
    return {
      name: text(args.name) ?? text(current.name) ?? (text(args.automationId) ? `Automation ${args.automationId}` : undefined),
      previousName: text(args.name) && text(args.name) !== text(current.name) ? text(current.name) : undefined,
      instructions: text(args.instructions) ?? text(exact.resolvedInstructions) ?? text(current.instructions),
      previousInstructions: text(args.instructions) && text(current.instructions) !== args.instructions ? text(current.instructions) : undefined,
      fields,
      triggers: Object.keys(timing).length || args.kind || directSource || approval.toolName === 'automation.run' ? [] : triggers,
    };
  } catch { return null; }
}

export function AutomationApprovalPreview({ approval }: { approval: PendingApproval }) {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const view = preview(approval);
  const details = Object.entries(approval.details);
  return <Flex vertical gap={token.padding}>
    <Typography.Text type="secondary">{approval.toolName === 'automation.delete'
      ? 'Stops future runs. Existing runs remain available in Runs.'
      : 'Review this proposal. Approval applies only to this change.'}</Typography.Text>
    {approval.summary !== 'Review exact Automation change' && <Typography.Text>{approval.summary}</Typography.Text>}
    {view ? <>
      {view.name && <Flex vertical gap={token.paddingXXS}>
        <Typography.Text strong style={{ fontSize: token.fontSizeLG }}>{view.name}</Typography.Text>
        {view.previousName && <Typography.Text type="secondary">Previously: {view.previousName}</Typography.Text>}
      </Flex>}
      {view.instructions && <Flex vertical gap={token.paddingXS}>
        <Typography.Text strong>Instructions</Typography.Text>
        <Typography.Paragraph className="chat-approval-value" style={{ margin: 0 }}>{view.instructions}</Typography.Paragraph>
        {view.previousInstructions && <Collapse size="small" items={[{ key: 'previous', label: 'Previous instructions', children: <Typography.Paragraph className="chat-approval-value" style={{ margin: 0 }}>{view.previousInstructions}</Typography.Paragraph> }]} />}
      </Flex>}
      <Descriptions size="small" column={1} layout={screens.sm ? 'horizontal' : 'vertical'} styles={{ label: { color: token.colorTextSecondary, width: screens.sm ? '12rem' : undefined }, content: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' } }}
        items={view.fields.map((field, index) => ({ key: `${field.label}-${index}`, label: field.label, children: <Flex vertical gap={token.paddingXXS}><span>{field.value}</span>{field.previous && <Typography.Text type="secondary">Previously: {field.previous}</Typography.Text>}</Flex> }))} />
      {view.triggers.map((trigger, index) => <Flex vertical gap={token.paddingXS} key={index}>
        <Typography.Text strong>Trigger {index + 1}{object(trigger).enabled === false || object(trigger).Enabled === false ? ' · Disabled' : ''}</Typography.Text>
        <Descriptions size="small" column={1} layout={screens.sm ? 'horizontal' : 'vertical'} styles={{ label: { color: token.colorTextSecondary, width: screens.sm ? '12rem' : undefined }, content: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' } }} items={triggerFields(object(trigger)).map(field => ({ key: field.label, label: field.label, children: field.value }))} />
      </Flex>)}
    </> : <Alert type="warning" showIcon title="Review the full request" description={approval.details['Exact action'] ? 'This request could not be formatted. Review its complete details before approving.' : 'The proposed action is missing. Reject this request and ask for a fresh proposal.'} />}
    <Collapse key={approval.approvalId} size="small" defaultActiveKey={view ? [] : ['details']} items={[{
      key: 'details', label: 'Technical details', children: <Flex vertical gap={token.padding}>
        {details.map(([label, value]) => <Flex vertical gap={token.paddingXS} key={label}>
          <Typography.Text strong>{label}</Typography.Text>
          <pre className="chat-approval-code" style={{ fontFamily: token.fontFamilyCode }}>{value}</pre>
        </Flex>)}
      </Flex>,
    }]} />
  </Flex>;
}
