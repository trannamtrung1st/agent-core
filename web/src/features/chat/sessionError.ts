export const SESSION_ERROR_CLASSES = [
  "validation/protocol",
  "provider/model",
  "speech/capture/playback",
  "persistence",
  "transport/reconnect",
  "tool/policy denial",
  "sandbox",
  "resource limit",
  "session"
] as const;

export type SessionErrorClass = (typeof SESSION_ERROR_CLASSES)[number];

export type SessionErrorView = {
  category: string;
  code: string;
  message: string;
  fatal: boolean;
  retryAfterMs: number | null;
  classId: SessionErrorClass;
  extensions?: Record<string, string | number | boolean>;
};

export type WireError = {
  category?: string;
  code?: string;
  message?: string;
  fatal?: boolean;
  retryAfterMs?: number | null;
  extensions?: Record<string, unknown> | null;
};

const SECRET_KEY =
  /secret|token|password|authorization|cookie|apikey|api[_-]?key|stack|trace|path|filepath|body|payload|raw|credential/i;
const SECRET_VALUE = /sk-|bearer\s|api[_-]?key|-----begin|eyj[a-z0-9_-]+\.[a-z0-9_-]+/i;

export function sessionErrorClass(category: string, code: string): SessionErrorClass {
  const cat = category.trim().toLowerCase();
  const id = code.trim().toLowerCase();
  if (cat === "validation" || cat === "protocol" || id.includes("protocol") || id.includes("validation")) {
    return "validation/protocol";
  }

  if (cat === "provider" || cat === "model" || id.includes("provider") || id.includes("model") || id === "brainfailed") {
    return "provider/model";
  }

  if (
    cat === "speech"
    || cat === "capture"
    || cat === "playback"
    || id.includes("speech")
    || id.includes("voice")
    || id.includes("audio")
    || id.includes("microphone")
    || id.includes("playback")
  ) {
    return "speech/capture/playback";
  }

  if (cat === "persistence" || id.includes("persist")) {
    return "persistence";
  }

  if (cat === "transport" || id.includes("reconnect") || id.includes("backpressure") || id.includes("sessioninuse")) {
    return "transport/reconnect";
  }

  if (cat === "tool" || cat === "policy" || id.includes("policy") || id.includes("denied") || id.includes("tool")) {
    return "tool/policy denial";
  }

  if (cat === "sandbox" || id.includes("sandbox")) {
    return "sandbox";
  }

  if (cat === "resource" || id.includes("resource") || id.includes("limit") || id.includes("quota")) {
    return "resource limit";
  }

  return "session";
}

export function classLabel(classId: SessionErrorClass): string {
  switch (classId) {
    case "validation/protocol":
      return "Validation or protocol";
    case "provider/model":
      return "Provider or model";
    case "speech/capture/playback":
      return "Speech, capture, or playback";
    case "persistence":
      return "Persistence";
    case "transport/reconnect":
      return "Transport or reconnect";
    case "tool/policy denial":
      return "Tool or policy denial";
    case "sandbox":
      return "Sandbox";
    case "resource limit":
      return "Resource limit";
    default:
      return "Session";
  }
}

export function sanitizeExtensions(raw: Record<string, unknown> | null | undefined): Record<string, string | number | boolean> | undefined {
  if (!raw) {
    return undefined;
  }

  const next: Record<string, string | number | boolean> = {};
  for (const [key, value] of Object.entries(raw)) {
    if (SECRET_KEY.test(key)) {
      continue;
    }

    if (typeof value === "string") {
      if (SECRET_VALUE.test(value) || value.length > 200) {
        continue;
      }

      next[key] = value;
      continue;
    }

    if (typeof value === "number" && Number.isFinite(value)) {
      next[key] = value;
      continue;
    }

    if (typeof value === "boolean") {
      next[key] = value;
    }
  }

  return Object.keys(next).length > 0 ? next : undefined;
}

export function sessionErrorFromMessage(
  message: string,
  extras: Omit<Partial<SessionErrorView>, "message"> & { category?: string; code?: string } = {}
): SessionErrorView {
  const category = extras.category ?? "Session";
  const code = extras.code ?? "Error";
  return {
    category,
    code,
    message,
    fatal: extras.fatal ?? false,
    retryAfterMs: extras.retryAfterMs ?? null,
    classId: extras.classId ?? sessionErrorClass(category, code),
    extensions: extras.extensions
  };
}

export function sessionErrorFromWire(error: WireError | null | undefined, fallbackMessage: string): SessionErrorView {
  const category = typeof error?.category === "string" && error.category.trim() ? error.category : "Session";
  const code = typeof error?.code === "string" && error.code.trim() ? error.code : "Error";
  const message = typeof error?.message === "string" && error.message.trim() ? error.message : fallbackMessage;
  return {
    category,
    code,
    message,
    fatal: error?.fatal === true,
    retryAfterMs: typeof error?.retryAfterMs === "number" && Number.isFinite(error.retryAfterMs)
      ? error.retryAfterMs
      : null,
    classId: sessionErrorClass(category, code),
    extensions: sanitizeExtensions(error?.extensions ?? undefined)
  };
}

export function resolveSessionError(
  error: SessionErrorView | string | null,
  fatal = false
): SessionErrorView | null {
  if (!error) {
    return null;
  }

  if (typeof error === "string") {
    return sessionErrorFromMessage(error, { fatal });
  }

  return error;
}
