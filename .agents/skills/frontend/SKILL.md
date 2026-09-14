---
name: frontend
description: "Implement or review Agent Core React/Vite UI, Zustand state and browser connection/audio lifecycle in web."
---

# Frontend

Read [Frontend Implementation](../../../docs/13-frontend-implementation-spec.md); use [Protocol](../../../docs/14-api-and-realtime-protocol.md) and [realtime](../realtime/SKILL.md) for connection/audio changes.

- Use React/Vite, strict TypeScript, Zustand and plain CSS/CSS Modules. Services own connection/audio lifetimes; components subscribe, render and issue commands. Avoid SSR, Next.js and large UI/data frameworks without a requirement.
- Keep serializable UI/session projections in Zustand; PCM buffers, AudioContext and HubConnection stay in services. Guard deltas by attachment, response, event sequence and text offset; preserve tombstones until authoritative snapshot replacement.
- Render escaped plain text and interrupted/failed statuses. Retain drafts on reconnect, disable submission until ready, and provide labeled controls, keyboard access, visible focus and actionable errors.
- Register handlers before connecting and reattach on each new connection. On loss stop playback and discard input buffers; never replay microphone frames. Use same-origin URLs and documented Vite proxies; keep secrets backend-only.
- Follow one-session text/voice **mode** lifecycle (`session.mode.set`); do not create a second conversation for voice. While `pendingMode` is voice, render Starting voice… and disable the control. Initialize audio from user gestures after voice actually applies and release tracks/resources when returning to text. Mute affects input only; capture continues during output.
- Synthetic mode defaults to scripted hardware-free input/playback simulation, not recognition of arbitrary speech. Keep separate fake-device AudioWorklet coverage.
- Use [testing](../testing/SKILL.md) for Vitest, React Testing Library, build and synthetic Playwright checks; report device behavior that automated checks cannot establish.
