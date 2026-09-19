---
name: Agent Core
description: Ant Design v6 dark operate UI for one persistent text/voice session.
colors:
  primary: "#1677ff"
  success: "#52c41a"
  successHover: "#73d13d"
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
  headline:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.3
  title:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: 1.3
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
  label:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
rounded:
  control: "6px"
  bubble: "18px"
  composer: "16px"
spacing:
  compact: "8px"
  default: "12px"
  section: "16px"
  controlInner: "8px"
components:
  antd-control:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    size: "32px"
  user-bubble:
    backgroundColor: "{colors.bubble}"
    textColor: "{colors.text}"
    rounded: "{rounded.bubble}"
    padding: "8px 12px"
  composer-shell:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.composer}"
    padding: "{spacing.compact}"
  composer-control:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    padding: "{spacing.controlInner}"
  spoken-text:
    backgroundColor: "transparent"
    textColor: "{colors.textSecondary}"
    padding: "8px 0 0"
---

# Design System: Agent Core

This file is lightweight MVP **presentation** guidance only. Screens, copy, and behavior stay in `/docs`. If this file conflicts with `/docs`, `/docs` wins.

## Overview

**Creative North Star: "Ant Design operate UI"**

Agent Core is a personal-chat operate surface. Ant Design v6 is the MVP component system; Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT/Codex are UX and hierarchy references for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer with inference controls, in-flow activity)—not a component dependency or pixel clone.

The shipped appearance is Ant Design `darkAlgorithm`: black layout, conversation container, elevated composer, and primary blue for Send and Voice-on. Product CSS maps those roles from `:root` custom properties; it does not invert a former light palette or invent a second token catalog.

**Key Characteristics:**

- Ant Design v6 imported directly in product components; `app.css` only sizes the shell, overflow, product layout, previews, and accessibility.
- Compact / default / section spacing (8 / 12 / 16px) owns shells, docks, and sibling `gap`. Text-control inner padding is `{spacing.controlInner}` (8px, Ant Design `paddingXS`, same as a session-row body).
- Composer owns Model (reasoning level inside the Model button when supported) with Attach/Voice/Send; header owns identity, Speech locale, and overflow.
- Assistant display Markdown stays primary; persisted public speech text is a quieter **Spoken** inset on the same turn.

## Colors

Dark operate neutrals with one primary accent and one success accent for live microphone state.

### Primary
- **Ant Design primary** (`{colors.primary}`): Send, Voice-on (`aria-pressed`), focus rings, and selection tint. Use sparingly so the transcript stays readable.

### Secondary
- **Listening green** (`{colors.success}` / `{colors.successHover}`): microphone is actively capturing. Distinct from Voice mode (primary). Do not use green for Voice-on.

### Neutral
- **Layout** (`{colors.layout}`): rail and page chrome.
- **Container** (`{colors.container}`): conversation pane and header.
- **Elevated** (`{colors.elevated}`): composer shell, code, chips-on-dark.
- **Border** (`{colors.border}`): 1px hairlines (rail edge, header, composer, Spoken separator).
- **Fill** (`{colors.fill}`): session-row hover across the whole row including overflow.
- **Bubble** (`{colors.bubble}`): user message fill only.
- **Text / secondary / tertiary** (`{colors.text}`, `{colors.textSecondary}`, `{colors.textTertiary}`): body, meta/timestamps/Spoken body, Spoken caption and icons.

**The Role Surfaces Rule.** Map these dark roles in product CSS. Do not invert a light palette or add extra brand hues.

## Typography

**Display/Body Font:** system UI stack (San Francisco / Segoe UI / Roboto fallbacks). No self-hosted display face.

**Character:** Ant Design defaults. Status is written in type, not color alone.

### Hierarchy
- **Headline** (600, 28px desktop / 22px below 768px): empty-chat “What do you want to work on?”
- **Title** (600, 16px): Agent Core rail wordmark (line-height 1.4) and session header agent name (line-height 1.3).
- **Body** (400, 14px, line-height 1.57): conversation, composer field, rail titles. Reading column max width 52rem.
- **Label** (400, 12px): timestamps, header subtitle, connection/profile, Spoken caption. Spoken icon matches this size.

**The One Face Rule.** Do not self-host a display face or costume monospace except for code in Markdown.

## Layout

One session: black rail + conversation column + sticky composer. Column is `min(100%, 52rem)` centered with 16px inline padding. Conversation list gap is section (16px); inside a turn, compact (8px) owns sibling stacks (meta → body → Spoken).

- Rail: 280px at 1200px+, 240px from 768–1199px, Drawer below 768px. Shared 16px left edge for New chat, Chats, session titles. Sections use title + gap, not dividing rules.
- Header: 56px min height, container background, compact block padding, 16px inline.
- Composer dock: 12px above, 16px below the shell. Toolbar min-height 32px (44px below 768px); wrap with 8px gap.
- After a user send, leave about half the pane for the incoming reply; shrink as the reply grows. Historical turns stay compact.
- New chat empty: Identity + Speech locale in the intro stack (max 22rem). Model/Reasoning are not duplicated there.
- Paused: Resume replaces the composer; Model returns to the header. Ended: quiet ended note; disabled Model in the header.

**The Docs Win Rule.** This file does not own Voice availability, Send/Queue/Stop behavior, or speech persistence. `/docs` does.

**The Compact Rhythm Rule.** Shells, docks, and sibling `gap` use compact/default/section (8 / 12 / 16px). Do not invent extra `--ac-space-*` steps or 2px CSS gaps.

**The Additive Inset Rule.** A padded shell owns the outer inset. Nested text controls keep their own inner padding (`{spacing.controlInner}` = 8px, Ant Design `paddingXS` via `theme.useToken()`, same as `.session-row-body`). Align labels by giving equivalent children the same inner padding. Example: composer `{spacing.compact}` (8px) + field/button `{spacing.controlInner}` (8px) so Message and Model text share one edge, while hover fill still has 8px around the glyph. Apply the same stack to the Model Dropdown overlay (shell 8px + 8px on title, catalog rows, and reasoning footer). Header/paused/ended outlined chip uses the same compact frame. Icon-only 32×32 toolbar hits stay padding 0.

Audit: Message placeholder left edge equals the Model name left edge; Model hover fill matches session-row inset and does not overlap the next control. Never zero a text button’s padding to force alignment, and never use negative margin to grow a hover fill.

## Elevation & Depth

Mostly flat tonal layering (layout → container → elevated → bubble/fill). One structural shadow on the composer.

### Shadow Vocabulary
- **Composer lift** (`box-shadow: 0 6px 16px rgba(0, 0, 0, 0.45)`): sticky message well only.

**The Flat-By-Default Rule.** Surfaces are flat at rest. Do not add card shadows inside transcript turns.

## Shapes

- **Control** (6px): AntD buttons, Selects, 32px composer icon/send hits, status tags.
- **Composer** (16px): message well.
- **Bubble** (18px): user turns only. Assistant content is unbubbled Markdown.

Hairline 1px `{colors.border}` separators. No colored 2px side rails, no glass.

## Components

### Buttons
- **Shape:** 6px radius; icon Send/Attach/Voice are 32×32px (44px tall toolbar on small screens).
- **Primary:** Send and Voice-on use `{colors.primary}`.
- **Ghost/text:** Attach, overflow, New chat, Model, catalog rows; hover uses `{colors.fill}` only. Suppress Ant Design text-button `::before`/`::after` rings so hover does not flash a border. Selected catalog row is a checkmark, not a persistent fill.
- **Success:** live microphone control uses `{colors.success}`.

### Chips
- Interrupted/failed: solid Ant Design Tag, not a full-width banner. Default tag marks the catalog default model in the Model menu.

### Cards / Containers
- Composer shell: elevated fill, 16px radius, compact 8px padding, 8px inner gap, 1px border, composer shadow.
- Do not nest a card inside each assistant message.

### Inputs / Fields
- Message: borderless textarea inside the composer; placeholder secondary text. Follow **The Additive Inset Rule** (shell `{spacing.compact}` + `{spacing.controlInner}` on the field).
- Model in the live composer: one Ant Design `Dropdown` (not Modal, not a pair of Selects). The chip is a single Model text button: model name, optional Default tag, optional reasoning level as secondary text inside the same control (`aria-label="Reasoning"`, Codex-style, not a sibling button), then the chevron. The overlay lists models (Default tag + check for the active row) and, when supported, a footer with Reasoning, current level, and a dotted slider. Overlay chrome matches the composer shell; title, rows, and footer use `{spacing.controlInner}` (8px, same as session rows). Header/paused/ended uses the outlined chip with the same additive padding (frame 8px + control 8px).
- Identity and Speech locale: labeled AntD Selects.

### Navigation
- Session row hover fills the whole row including overflow (24px icon, inside row padding, common right edge).
- Header: agent name + 12px timestamp; Speech locale; conversation overflow (End). No Model in the live header.

### Conversation turns
- User: right-aligned bubble (`8px 12px`, 18px radius).
- Assistant: open sanitized Markdown; optional blocks/files; then **Spoken** when public `speechText` meaningfully differs. Spoken is the same list item: speaker icon (decorative) + visible “Spoken” label (tertiary via CSS), body secondary with `pre-wrap`; 8px stack gap; 8px padding above a 1px border. Not a second bubble, avatar, or timestamp.

### Composer toolbar
- Left: Model (reasoning level inside the Model button when supported), Attach, Voice, microphone.
- Right: Stop / Queue / Send.
- Accessible names stay Model, Reasoning, Attach, Voice, Send, Stop.

## Do's and Don'ts

### Do:
- **Do** import `antd` in feature files; ConfigProvider uses `darkAlgorithm`. Keep Sider `theme="light"` so chat surfaces stay black.
- **Do** use compact/default/section (8/12/16px) for shells and sibling `gap`; use Ant Design `paddingXS` (8px, `{spacing.controlInner}`) via `theme.useToken()` for text-control inner padding so it matches session-row inset; align Spoken and composer toolbar to that rhythm.
- **Do** put Model in the composer when the composer is shown (Reasoning level inside the Model button when supported); keep Identity/Speech locale in the new-chat intro; keep Speech locale in the live header.
- **Do** compose Model/catalog rows with Ant Design `Button type="text"` and `Flex`; keep `app.css` for shell chrome, overflow, and ring suppression only.
- **Do** render Spoken only for assistant public `speechText` that differs after whitespace normalization; keep it secondary to display Markdown.
- **Do** honor `prefers-reduced-motion`; keep labeled errors, visible focus, and testids `connection` and `profile`.
- **Do** keep the labeled composer available while voice is live until `/docs` and tests change together.
- **Do** keep ended history on the same reading column with a quiet ended note, not a disabled input.

### Don't:
- **Don't** reproduce Pixel Dialogue Field, Obsidian Mint, Martian Mono, field textures, presence plates, or a custom Select.
- **Don't** add generic wrappers, Ant Design Pro/ProComponents/X, or another CSS framework.
- **Don't** use Layout.Sider `theme="dark"` (navy admin sider).
- **Don't** treat this file as behavioral authority over `/docs`.
- **Don't** use a blocking Modal or a second Select/button for model or effort; keep one Dropdown anchored to the Model chip, with effort in the overlay footer.
- **Don't** zero a text control’s padding, use negative margin, or stack extra child padding to fake alignment with a sibling.
- **Don't** show Spoken as another conversational turn or from internal generated tails.
