import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptySession, useSessionStore } from "../state/sessionStore";

const hub = vi.hoisted(() => {
  const on = vi.fn();
  const withUrl = vi.fn();
  const withHubProtocol = vi.fn();
  const withAutomaticReconnect = vi.fn();
  const build = vi.fn(() => ({
    on,
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    off: vi.fn(),
    invoke: vi.fn().mockResolvedValue({ accepted: true }),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn()
  }));
  const builder = { withUrl, withHubProtocol, withAutomaticReconnect, build };
  withUrl.mockReturnValue(builder);
  withHubProtocol.mockReturnValue(builder);
  withAutomaticReconnect.mockReturnValue(builder);
  return {
    on,
    withUrl,
    withHubProtocol,
    build,
    MessagePackHubProtocol: vi.fn(function MessagePackHubProtocol() {
      return { name: "messagepack" };
    })
  };
});

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
    return hub;
  }),
  HttpTransportType: { WebSockets: 1 },
  HubConnectionState: { Connected: 1, Disconnected: 0 }
}));

vi.mock("@microsoft/signalr-protocol-msgpack", () => ({
  MessagePackHubProtocol: hub.MessagePackHubProtocol
}));

vi.mock("./api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./api")>();
  return {
    ...actual,
    ensureOwnerCapability: vi.fn().mockResolvedValue("owner-token")
  };
});

import { bytesOf, retryConnection } from "./realtime";

const sessionId = "11111111-2222-4333-8444-555555555555";

describe("realtime protocol selection", () => {
  beforeEach(() => {
    vi.unstubAllEnvs();
    hub.on.mockClear();
    hub.withUrl.mockClear();
    hub.withHubProtocol.mockClear();
    hub.MessagePackHubProtocol.mockClear();
    useSessionStore.setState({
      ...emptySession(),
      sessionId,
      status: "active"
    });
  });

  it("decodes a SignalR JSON base64 audio payload into bytes", () => {
    expect(Array.from(bytesOf("AQIDBA=="))).toEqual([1, 2, 3, 4]);
    expect(bytesOf("")).toEqual(new Uint8Array());
    expect(bytesOf("not base64")).toEqual(new Uint8Array());
  });

  it.each([
    ["unset", undefined],
    ["messagepack", "messagepack"],
    ["other", "other"]
  ] as const)("uses MessagePack and one handler path when the protocol is %s", async (_label, protocol) => {
    vi.stubEnv("VITE_AGENTCORE_REALTIME_PROTOCOL", protocol ?? "");

    await retryConnection();

    expect(hub.withUrl).toHaveBeenCalledWith(
      "/hubs/session",
      expect.objectContaining({ skipNegotiation: true })
    );
    expect(hub.MessagePackHubProtocol).toHaveBeenCalledTimes(1);
    expect(hub.withHubProtocol).toHaveBeenCalledTimes(1);
    expect(hub.on.mock.calls.map((call) => call[0])).toEqual(["SessionEvent", "AudioOutput"]);
  });

  it("skips MessagePack for json and keeps the same handlers", async () => {
    vi.stubEnv("VITE_AGENTCORE_REALTIME_PROTOCOL", "json");

    await retryConnection();

    expect(hub.withUrl).toHaveBeenCalledWith(
      "/hubs/session",
      expect.objectContaining({ skipNegotiation: true })
    );
    expect(hub.MessagePackHubProtocol).not.toHaveBeenCalled();
    expect(hub.withHubProtocol).not.toHaveBeenCalled();
    expect(hub.on.mock.calls.map((call) => call[0])).toEqual(["SessionEvent", "AudioOutput"]);
  });
});
