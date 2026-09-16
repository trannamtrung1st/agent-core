export function Hud({
  profile,
  connectionText,
  connectionTone,
  identity
}: {
  profile: string;
  connectionText: string;
  connectionTone: "live" | "wait" | "alarm";
  identity: { name: string; role: string } | null;
}) {
  return (
    <header className="hud">
      <div className="hud-copy">
        <h1 className="wordmark">Agent Core</h1>
        <div className="hud-meta">
          <div className="hud-row">
            <span className="marker marker-plus" aria-hidden="true" />
            <div className="hud-status-copy">
              <p className="profile" data-testid="profile">
                Profile: {profile || "…"}
              </p>
              <p className={`status status-${connectionTone}`} data-testid="connection">
                {connectionText}
              </p>
            </div>
          </div>
          {identity ? (
            <p className="hud-row identity">
              <span className="marker marker-diamond" aria-hidden="true" />
              <span className="identity-copy">
                Identity {identity.name || "Agent"}
                <span className="identity-dot"> · </span>
                <span className="identity-role">
                  <RoleMark role={identity.role} />
                </span>
              </span>
            </p>
          ) : null}
        </div>
      </div>
      <img className="presence" src="/plates/presence.png" alt="" width={960} height={648} />
    </header>
  );
}

function RoleMark({ role }: { role: string }) {
  const parts = role.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) {
    return null;
  }
  if (parts.length === 1) {
    return <span className="role">{parts[0]}</span>;
  }
  const last = parts[parts.length - 1];
  return (
    <>
      {parts.slice(0, -1).join(" ")} <span className="role">{last}</span>
    </>
  );
}
