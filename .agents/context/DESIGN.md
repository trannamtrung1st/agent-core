---
name: Agent Core
description: Ant Design v6 dark operate UI for one persistent text/voice session.
colors:
  primary: "#1677ff"
  text: "rgba(255, 255, 255, 0.88)"
  textSecondary: "rgba(255, 255, 255, 0.65)"
  textTertiary: "rgba(255, 255, 255, 0.45)"
  layout: "#000000"
  container: "#141414"
  elevated: "#1f1f1f"
  border: "#303030"
  fill: "rgba(255, 255, 255, 0.08)"
  bubble: "rgba(255, 255, 255, 0.12)"
typography:
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
rounded:
  control: "6px"
  bubble: "18px"
  composer: "16px"
spacing:
  compact: "8px"
  default: "12px"
  section: "16px"
components:
  antd-control:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
  user-bubble:
    backgroundColor: "{colors.bubble}"
    textColor: "{colors.text}"
    rounded: "{rounded.bubble}"
    padding: "8px 12px"
  composer-shell:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.composer}"
    padding: "8px 12px"
---

# Design Context

This file is lightweight MVP **presentation** guidance only. Screens, copy, and behavior stay in `/docs`. If this file conflicts with `/docs`, `/docs` wins.

**Creative North Star: Ant Design operate UI**

Agent Core is a personal-chat operate surface. Ant Design is the MVP component system. Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT is a UX/layout reference for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer, in-flow activity)—not a component dependency or pixel clone.

The default appearance is Ant Design `darkAlgorithm`: black layout, `#141414` conversation, `#1f1f1f` elevated composer/code, and `#1677ff` for primary actions. Product CSS maps those roles; it does not invert the former light palette.

Ant Design v6 owns generic controls (Layout, Select, Button, Input, Empty, Spin, Tag, Alert, Progress, Typography, Dropdown, Drawer). Product components import those primitives directly. `web/src/app.css` may only size the shell, manage overflow, constrain product layout/content, size attachment previews, and fix accessibility.

## Product Context

- **What it is:** Operator UI for one active conversation at a time (durable catalog rail for resume); text and voice are modes of the same session.
- **Who it is for:** Operators and demo participants; Synthetic stays labeled.
- **What it is not:** Pixel Dialogue Field, a custom token catalog, an admin dashboard, or a second component framework.

## Personality

Quiet, labeled, and conventional. Status is written in type. Controls keep their accessible names (Identity, Send, Stop, Voice, Cancel voice, Mute, Unmute, End, Attach, Resume).

## Tone

Direct operator language. No invented marketing. Prefer Ant Design defaults over custom chrome.

## Voice

Use Ant Design type and spacing. Do not self-host a display face or recreate phosphor, bevels, markers, or decorative plates.

## Do

- Import `antd` components in feature files; keep ConfigProvider at the app root with `darkAlgorithm`.
- Honor `prefers-reduced-motion`; keep labeled errors and visible focus.
- Session rail is 280px at 1200px and above, 240px from 768–1199px, and an Ant Design Drawer below 768px.
- Session list hover fills the whole row, including the overflow control, using the fill token only—no outline on the open control. Overflow icons share one 24px size, sit inside the row padding, and share a common right edge.
- Rail sections use a title and gap, not dividing rules. New chat, the Chats heading, and session titles share the Agent Core left edge.
- Conversation meta shows a compact timestamp next to the agent name and above user bubbles. Interrupted/failed entries use an Ant Design Tag chip, not a full-width banner.
- After a user send, leave about half the conversation pane below that bubble for the incoming reply so prior turns can stay in view. Shrink that space as the reply grows. Historical turns stay compact.
- Preserve testids `connection` and `profile`.
- Show the Voice control only when `voiceAvailable` is true (catalog list before attach; `session.ready` agent after attach).
- Keep the labeled composer available while voice is live until `/docs` and tests change together.
- Ended conversations keep the same reading column; the composer slot is a quiet “This conversation has ended.” note, not a disabled input.

## Don't

- Reproduce Pixel Dialogue Field, Obsidian Mint, Martian Mono, field textures, presence plates, or custom Select.
- Add generic wrapper libraries, Ant Design Pro/ProComponents/X, or another CSS framework.
- Use Layout.Sider `theme="dark"` (navy admin sider). Keep `theme="light"` so `darkAlgorithm` supplies chat surfaces.
- Treat this file as behavioral authority over `/docs`.
