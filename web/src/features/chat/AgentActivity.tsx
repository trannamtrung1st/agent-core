import { Spin } from "antd";
import type { AgentActivityState } from "./activityState";

export function AgentActivity({ activity }: { activity: AgentActivityState }) {
  if (activity.kind === "idle") {
    return null;
  }

  const label = activity.label;
  const spinning = activity.kind !== "error";

  return (
    <div className="agent-activity" role="status" aria-live="polite" aria-atomic="true">
      {spinning ? <Spin size="small" /> : null}
      <span>{label}</span>
    </div>
  );
}
