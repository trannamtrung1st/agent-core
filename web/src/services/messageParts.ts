export type ResourceReference = {
  kind:
    | "homeFile"
    | "session"
    | "backgroundSession"
    | "artifact"
    | "agentRun"
    | "skill";
  agentInstanceId?: string;
  sessionId?: string;
  itemId?: string;
  artifactId?: string;
  agentRunId?: string;
  skillKey?: string;
  selectedRevision?: number;
};
export type MessagePart =
  | { kind: "text"; text: string }
  | {
      kind: "invocation";
      invocationKind: "skill";
      skillKey: string;
      label?: string;
    }
  | { kind: "reference"; reference: ResourceReference; label?: string };
export function locator(r: ResourceReference): string {
  switch (r.kind) {
    case "homeFile":
      return `homeFile:${r.agentInstanceId}/${r.itemId}`;
    case "session":
    case "backgroundSession":
      return `${r.kind}:${r.sessionId}`;
    case "artifact":
      return `artifact:${r.sessionId}/${r.artifactId}`;
    case "agentRun":
      return `agentRun:${r.sessionId}/${r.agentRunId}`;
    case "skill":
      return `skill:${r.agentInstanceId}/${r.skillKey}`;
  }
}
export function partText(p: MessagePart): string {
  return p.kind === "text"
    ? p.text
    : p.kind === "invocation"
      ? `/${p.skillKey}`
      : `@${locator(p.reference)}`;
}
export function displayPart(p: MessagePart): string {
  return p.kind === "text"
    ? p.text
    : `${p.kind === "invocation" ? "/" : "@"}${p.label ?? (p.kind === "invocation" ? p.skillKey : locator(p.reference))}`;
}
export function messageText(parts: readonly MessagePart[]): string {
  return parts.map(partText).join("");
}
export function hasTask(
  parts: readonly MessagePart[] | null | undefined,
  text: string,
): boolean {
  return parts
    ? parts.some(
        (p) =>
          p.kind === "reference" ||
          (p.kind === "text" && p.text.trim().length > 0),
      )
    : text.trim().length > 0;
}
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const skill =
  /^(?:definition:[a-z][a-z0-9._-]{0,63}|instance:(?:[a-z][a-z0-9._-]{0,63}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}))$/;
export function normalizeParts(raw: unknown): MessagePart[] {
  if (!Array.isArray(raw) || raw.length > 128)
    throw new Error("Use at most 128 message parts.");
  const result: MessagePart[] = [];
  const skills = new Set<string>();
  for (const p of raw) {
    if (!p || typeof p !== "object") throw new Error("Invalid message part.");
    const keys = Object.keys(p).filter((k) => p[k] != null);
    const allowed =
      p.kind === "text"
        ? ["kind", "text"]
        : p.kind === "invocation"
          ? ["kind", "invocationKind", "skillKey", "label"]
          : ["kind", "reference", "label"];
    if (
      keys.some((k) => !allowed.includes(k)) ||
      (p.label != null && (typeof p.label !== "string" || p.label.length > 200))
    )
      throw new Error("Invalid message part fields.");
    if (p.kind === "text" && typeof p.text === "string") {
      if (!p.text) continue;
      const prev = result.at(-1);
      if (prev?.kind === "text") prev.text += p.text;
      else result.push({ kind: "text", text: p.text });
    } else if (
      p.kind === "invocation" &&
      p.invocationKind === "skill" &&
      typeof p.skillKey === "string" &&
      skill.test(p.skillKey)
    ) {
      if (!skills.has(p.skillKey)) {
        skills.add(p.skillKey);
        result.push({
          kind: "invocation",
          invocationKind: "skill",
          skillKey: p.skillKey,
          ...(p.label != null ? { label: p.label } : {}),
        });
      }
    } else if (
      p.kind === "reference" &&
      p.reference &&
      typeof p.reference === "object"
    ) {
      if (
        Object.keys(p.reference).some(
          (k) =>
            ![
              "kind",
              "agentInstanceId",
              "sessionId",
              "itemId",
              "artifactId",
              "agentRunId",
              "skillKey",
              "selectedRevision",
            ].includes(k),
        )
      )
        throw new Error("Invalid reference locator fields.");
      const r = Object.fromEntries(
        Object.entries(p.reference).filter(([, v]) => v != null),
      ) as ResourceReference;
      const fields: Record<string, string[]> = {
        homeFile: ["agentInstanceId", "itemId"],
        session: ["sessionId"],
        backgroundSession: ["sessionId"],
        artifact: ["sessionId", "artifactId"],
        agentRun: ["sessionId", "agentRunId"],
        skill: ["agentInstanceId", "skillKey"],
      };
      const required = fields[r.kind];
      if (
        !required ||
        Object.keys(r).some(
          (k) =>
            ![
              "kind",
              ...required,
              ...(r.kind === "artifact" ? [] : ["selectedRevision"]),
            ].includes(k),
        ) ||
        required.some(
          (k) =>
            typeof r[k as keyof ResourceReference] !== "string" ||
            !(k === "skillKey" ? skill : guid).test(
              r[k as keyof ResourceReference] as string,
            ),
        ) ||
        (r.selectedRevision !== undefined &&
          (!Number.isSafeInteger(r.selectedRevision) ||
            r.selectedRevision <= 0))
      )
        throw new Error("Invalid reference locator.");
      result.push({
        kind: "reference",
        reference: { ...r },
        ...(p.label != null ? { label: p.label } : {}),
      });
    } else throw new Error("Invalid message part.");
  }
  if (
    messageText(result).length > 8000 ||
    new TextEncoder().encode(JSON.stringify(wireParts(result))).length > 32768
  )
    throw new Error("Message exceeds 8000 characters or 32 KiB of parts.");
  return result;
}
export function wireParts(parts: readonly MessagePart[]): MessagePart[] {
  return parts.map((p) =>
    p.kind === "text"
      ? { ...p }
      : p.kind === "invocation"
        ? {
            kind: p.kind,
            invocationKind: p.invocationKind,
            skillKey: p.skillKey,
          }
        : { kind: p.kind, reference: { ...p.reference } },
  );
}
export function snapshotParts(
  parts: readonly MessagePart[] | null | undefined,
): MessagePart[] | undefined {
  return parts ? normalizeParts(parts) : undefined;
}
export type ComposerChoice = {
  id: string;
  label: string;
  description: string;
  category: string;
  skillKey?: string | null;
  reference?: ResourceReference | null;
  unavailableReason?: string | null;
};
