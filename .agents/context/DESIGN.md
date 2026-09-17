---
name: Agent Core
description: Ant Design v6 operate UI for one persistent text/voice session.
colors:
  primary: "#1677ff"
  text: "rgba(0, 0, 0, 0.88)"
  layout: "#f5f5f5"
  container: "#ffffff"
  border: "#f0f0f0"
typography:
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
rounded:
  control: "6px"
spacing:
  compact: "8px"
  default: "12px"
  section: "16px"
components:
  antd-control:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
---

# Design Context

This file is lightweight MVP **presentation** guidance only. Screens, copy, and behavior stay in `/docs`. If this file conflicts with `/docs`, `/docs` wins.

**Creative North Star: Ant Design operate UI**

Agent Core is a personal-chat operate surface. Ant Design is the MVP component system. Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT is a UX/layout reference for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer, in-flow activity)—not a component dependency or pixel clone.

Ant Design v6 owns generic controls (Layout, Select, Button, Input, Empty, Spin, Tag, Alert, Progress, Typography, Dropdown, Drawer). Product components import those primitives directly. `web/src/app.css` may only size the shell, manage overflow, constrain product layout/content, size attachment previews, and fix accessibility.

## Product Context

- **What it is:** Operator UI for one identity and one conversation; text and voice are modes of the same session.
- **Who it is for:** Operators and demo participants; Synthetic stays labeled.
- **What it is not:** Pixel Dialogue Field, a custom token catalog, an admin dashboard, or a second component framework.

## Personality

Quiet, labeled, and conventional. Status is written in type. Controls keep their accessible names (Identity, Send, Voice, Cancel voice, Mute, Unmute, End, Attach).

## Tone

Direct operator language. No invented marketing. Prefer Ant Design defaults over custom chrome.

## Voice

Use Ant Design type and spacing. Do not self-host a display face or recreate phosphor, bevels, markers, or decorative plates.

## Do

- Import `antd` components in feature files; keep ConfigProvider at the app root.
- Honor `prefers-reduced-motion`; keep labeled errors and visible focus.
- Session rail is 280px at 1200px and above, 240px from 768–1199px, and an Ant Design Drawer below 768px.
- Preserve testids `connection` and `profile`.
- Keep the labeled composer available while voice is live until `/docs` and tests change together.

## Don't

- Reproduce Pixel Dialogue Field, Obsidian Mint, Martian Mono, field textures, presence plates, or custom Select.
- Add generic wrapper libraries, Ant Design Pro/ProComponents/X, or another CSS framework.
- Treat this file as behavioral authority over `/docs`.
