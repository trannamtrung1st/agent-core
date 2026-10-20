import { DatePicker, Form, Input, InputNumber, Select, Typography, Alert } from 'antd';
import dayjs from 'dayjs';
import type { AutomationPolicy, ScheduleTiming } from '../../services/adminApi';

export const defaultAutomationTiming = (): ScheduleTiming => ({ kind: 'daily', timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', interval: 1, localTime: '09:00' });
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
const supportedTimeZones = [...new Set([localZone, 'UTC', ...Intl.supportedValuesOf('timeZone')])].sort();
export function validAutomationTiming(timing: ScheduleTiming): boolean {
  const maximum = timing.kind === 'fixedInterval' ? 604800 : timing.kind === 'daily' ? 365 : 52;
  return (timing.maxOccurrences == null || Number.isInteger(timing.maxOccurrences) && timing.maxOccurrences >= 1 && timing.maxOccurrences <= 366)
    && (timing.kind !== 'fixedInterval' || !!timing.anchorAtUtc && Number.isFinite(Date.parse(timing.anchorAtUtc)))
    && (timing.kind === 'oneShot' ? !!timing.atUtc && Number.isFinite(Date.parse(timing.atUtc)) && Date.parse(timing.atUtc) > Date.now()
      : timing.interval >= (timing.kind === 'fixedInterval' ? 60 : 1) && timing.interval <= maximum && (timing.kind === 'fixedInterval'
        || /^\d{2}:\d{2}$/.test(timing.localTime ?? '') && !!timing.timeZone.trim() && (timing.kind !== 'weekly' || !!timing.weekdays?.length)));
}
export function AutomationScheduleFields({ timing, setTiming, disabled: busy, policy }: { timing: ScheduleTiming; setTiming: (patch: Partial<ScheduleTiming>) => void; disabled: boolean; policy?: AutomationPolicy | null }) {
  const maximum = timing.kind === 'fixedInterval' ? 604800 : timing.kind === 'daily' ? 365 : 52;
  const viewerZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  const previewTime = (value?: string | null) => value && Number.isFinite(Date.parse(value)) ? `${new Date(value).toLocaleString()} (${viewerZone})` : null;
  return <>
        <div className="automation-timing-fields">
          <Form.Item label="Timing"><Select aria-label="Schedule timing" value={timing.kind} disabled={busy}
            options={[{ value: "oneShot", label: "Once",  }, { value: "daily", label: "Daily",  }, { value: "weekly", label: "Weekly",  }, { value: "fixedInterval", label: "Fixed interval",  }]}
            onChange={kind => setTiming({ kind, timeZone: timing.timeZone, interval: kind === "fixedInterval" ? 3600 : 1, localTime: "09:00", weekdays: kind === "weekly" ? [1] : null, ...(kind === "fixedInterval" && !timing.anchorAtUtc ? { anchorAtUtc: new Date().toISOString() } : {}) })} /></Form.Item>
          {timing.kind === "oneShot" ? <Form.Item className="automation-timing-wide" label="Run at" extra={`Times shown in ${viewerZone}. Saved in UTC.`}>
            <DatePicker aria-label="Schedule run at" classNames={{ popup: { root: "automation-date-picker-popup" } }} showTime={{ format: "HH:mm" }} format="YYYY-MM-DD HH:mm"
              value={timing.atUtc ? dayjs(timing.atUtc) : null} disabled={busy}
              onChange={value => setTiming({ atUtc: value?.toISOString() ?? null })} />
          </Form.Item> :
            <Form.Item label={timing.kind === "fixedInterval" ? "Interval (seconds)" : timing.kind === "daily" ? "Every (days)" : "Every (weeks)"}>
              <InputNumber aria-label={timing.kind === "fixedInterval" ? "Schedule interval (seconds)" : timing.kind === "daily" ? "Schedule every (days)" : "Schedule every (weeks)"} min={timing.kind === "fixedInterval" ? 60 : 1} max={maximum} precision={0} value={timing.interval} disabled={busy} onChange={value => setTiming({ interval: value ?? 0 })} /></Form.Item>}
          {timing.kind === "daily" || timing.kind === "weekly" ? <><Form.Item label="Local time"><Input aria-label="Schedule local time" type="time" value={timing.localTime ?? "09:00"} disabled={busy} onChange={e => setTiming({ localTime: e.target.value })} /></Form.Item>
            <Form.Item className="automation-timing-zone" label="Time zone" extra="Runs at the selected local time. Daylight saving changes follow this zone; missing times move forward and repeated times run once."><Select aria-label="Schedule time zone" showSearch optionFilterProp="label" value={timing.timeZone} disabled={busy} options={[...new Set([timing.timeZone, ...supportedTimeZones])].filter((value): value is string => !!value).sort().map(value => ({ value, label: value }))} onChange={timeZone => setTiming({ timeZone })} /></Form.Item></> : null}
        </div>
        {timing.kind === 'fixedInterval' && <Form.Item label="Interval anchor" extra={`Times shown in ${viewerZone}. Saved in UTC.`}><DatePicker aria-label="Schedule interval anchor" classNames={{ popup: { root: 'automation-date-picker-popup' } }} showTime={{ format: 'HH:mm' }} format="YYYY-MM-DD HH:mm" value={timing.anchorAtUtc ? dayjs(timing.anchorAtUtc) : null} disabled={busy} onChange={value => setTiming({ anchorAtUtc: value?.toISOString() ?? null })} /></Form.Item>}
        {timing.kind === "weekly" ? <Form.Item label="Weekdays"><Select mode="multiple" aria-label="Schedule weekdays" value={timing.weekdays ?? []} disabled={busy}
          options={["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map((label, value) => ({ label, value }))} onChange={weekdays => setTiming({ weekdays })} /></Form.Item> : null}
        {timing.kind !== "oneShot" ? <>
          {policy?.allowIndefiniteRecurrence === false ? <Alert type="info" showIcon title="This Definition requires an end date or occurrence limit." /> : null}
          <div className="automation-limit-fields">
            <Form.Item label={timing.kind === "fixedInterval" ? "End at" : "End date"}
              extra={timing.kind === "fixedInterval" ? `Times shown in ${viewerZone}. Saved in UTC.` : undefined}>
              <DatePicker aria-label={timing.kind === "fixedInterval" ? "Schedule end at" : "Schedule end date"} classNames={{ popup: { root: "automation-date-picker-popup" } }}
                showTime={timing.kind === "fixedInterval" ? { format: "HH:mm" } : false}
                format={timing.kind === "fixedInterval" ? "YYYY-MM-DD HH:mm" : "YYYY-MM-DD"}
                value={timing.kind === "fixedInterval" ? timing.endAtUtc ? dayjs(timing.endAtUtc) : null : timing.endDate ? dayjs(timing.endDate) : null}
                disabled={busy} onChange={value => setTiming(timing.kind === "fixedInterval"
                  ? { endAtUtc: value?.toISOString() ?? null } : { endDate: value?.format("YYYY-MM-DD") ?? null })} />
            </Form.Item>
            <Form.Item label="Maximum occurrences">
              <InputNumber aria-label="Schedule maximum occurrences" min={1} precision={0} value={timing.maxOccurrences} disabled={busy} onInput={text => setTiming({ maxOccurrences: text.trim() ? Number(text) : null })} onChange={value => setTiming({ maxOccurrences: value })} />
            </Form.Item>
          </div>
          <Typography.Text type="secondary">Leave both bounds empty only if the Definition permits ongoing recurrence.</Typography.Text>
        </> : null}
        {timing.kind === "oneShot" && previewTime(timing.atUtc) ? <Typography.Paragraph style={{ margin: 0 }} type="secondary" role="status">
          Runs on {previewTime(timing.atUtc)}.
        </Typography.Paragraph> : null}
        {timing.kind === "fixedInterval" && previewTime(timing.endAtUtc) ? <Typography.Paragraph style={{ margin: 0 }} type="secondary" role="status">
          Stops on {previewTime(timing.endAtUtc)}.
        </Typography.Paragraph> : null}
  </>;
}

export function scheduleTimingLabel(timing: ScheduleTiming) {
  if (timing.kind === "oneShot") return `Once · ${timing.atUtc ? new Date(timing.atUtc).toLocaleString() : "Not scheduled"}`;
  if (timing.kind === "fixedInterval") return `Every ${timing.interval} seconds`;
  const days = timing.weekdays?.map(day => ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][day]).join(", ");
  return `Every ${timing.interval} ${timing.kind === "daily" ? "day" : "week"}${timing.interval === 1 ? "" : "s"}${days ? ` · ${days}` : ""} · ${timing.localTime} · ${timing.timeZone}`;
}
