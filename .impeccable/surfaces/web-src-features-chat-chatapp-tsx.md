---
version: 1
slug: "web-src-features-chat-chatapp-tsx"
primary_target: "web/src/features/chat/ChatApp.tsx"
related_targets:
  - web/src/features/chat/ChatHeader.tsx
  - web/src/features/chat/Conversation.tsx
  - web/src/features/chat/ChatMessage.tsx
  - web/src/features/chat/Composer.tsx
  - web/src/features/chat/SessionRail.tsx
  - web/src/app/antdTheme.ts
  - web/src/app.css
---

# ChatApp

Mode: Operate. One identity, one conversation; text and voice are modes of the same session. Session catalog remains a quiet rail, not an inbox.

## Direction contract

THESIS: Conversation-first operate surface. Quiet session rail, centered reading column, right-aligned user bubbles against open assistant Markdown, composer as the primary control. Refuses HUD/debug chrome and “Transcript · N entries” framing.

OWN-WORLD: Ant Design v6 dark operate UI. ChatGPT is a layout and hierarchy reference only—no ChatGPT assets, branding, or second kit. Product CSS is limited to rail width, header, column measure, bubbles, markdown, composer shell, chips, and breakpoints.

STORY: Choose an identity, then talk. Status lives in the thread. Chats are a quiet list with overflow actions. Synthetic stays named.

FIRST VIEWPORT: Black rail (`#000`) and `#141414` conversation. 280px rail at 1200px+ (240px from 768–1199px; Drawer below 768px) with Agent Core, New chat, Chats — shared 16px left edge, title+gap sections, no divider rules. Compact header: agent name, timestamp, overflow. Centered ~52rem column. User bubbles, open assistant Markdown, solid Interrupted/Failed tags. After send, ~50% pane remains for the reply so prior turns stay in view. Sticky composer (`#1f1f1f`): attach, field, voice (when `voiceAvailable`), send. Empty new-chat: “What do you want to work on?” plus Identity Select and the same composer. Mobile empty header reads New chat; `profile` / `connection` stay visible.

FORM: Ant Design operate UI with ChatGPT conversational structure. Code-led proposal path (no concept-roll seed; direction is local/proposals/agent-core-chatgpt-inspired-ui-proposal.md). Signature interaction: Enter sends on the same session; Voice is a mode transition; activity is transient in-flow.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance.

## Scope

- Surface: `web/src/features/chat/*` and `web/src/app.css`.
- Preserve testids `connection` and `profile`, accessible names Identity, Speech locale, Send, Voice, Cancel voice, Mute, Unmute, End, Attach.
- Composer stays in voice. End remains the existing session-end control until `/docs` and tests change together.
- Session catalog, attachments, realtime, and voice contracts stay compatible.
