import type { HistoryEntry } from "../../state/sessionStore";

export function Transcript({ agentName, entries }: { agentName: string; entries: HistoryEntry[] }) {
  const entryCount = entries.length;

  return (
    <section className="transcript-window" aria-label="Transcript">
      <div className="transcript-bar">
        <p className="transcript-title">
          <span className="marker marker-square" aria-hidden="true" />
          Transcript · {entryCount} {entryCount === 1 ? "entry" : "entries"}
        </p>
        <p className="live">
          Agent Core · Live
          <span className="marker marker-square" aria-hidden="true" />
        </p>
      </div>
      <div className="transcript-well">
        <div className="transcript-grid" aria-hidden="true" />
        <ol className="transcript" aria-live="polite">
          {entries.length === 0 ? (
            <li className="entry entry-empty">Send a message or start voice.</li>
          ) : (
            entries.map((entry) => {
              const isUser = entry.role === "user";
              const speaker = isUser ? "You" : agentName || "Agent";
              return (
                <li key={entry.entryId} data-role={entry.role} className="entry">
                  <span className={`marker ${isUser ? "marker-plus" : "marker-diamond"}`} aria-hidden="true" />
                  <p className="entry-copy">
                    <strong>{speaker}</strong>
                    <span className="emdash" aria-hidden="true" />
                    {entry.text}
                    {entry.status === "interrupted" || entry.status === "failed" ? <em>{entry.status}</em> : null}
                  </p>
                </li>
              );
            })
          )}
        </ol>
      </div>
    </section>
  );
}
