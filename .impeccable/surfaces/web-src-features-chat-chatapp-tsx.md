---
version: 1
slug: "web-src-features-chat-chatapp-tsx"
primary_target: "web/src/features/chat/ChatApp.tsx"
related_targets: []
---

# ChatApp

Mode: Operate. One identity, one conversation; text and voice are modes of the same session.

## Direction contract

THESIS: Agent Core is a labeled personal-chat operate surface. Ant Design v6 supplies the generic chrome; product code maps session, history, and composer callbacks onto those controls.

OWN-WORLD: Default Ant Design light theme. Layout sider or drawer for sessions, header for HUD and identity, content for transcript plus composer. No pixel field, bevel, or phosphor.

STORY: Pick Alex or Sam, then speak or type in one continuous conversation. Status is labeled (Ready, Starting voice…, Listening, Reconnecting). Synthetic stays named.

FIRST VIEWPORT: Wordmark Agent Core. Profile and Ready as labeled cues. Session list. Scrollable transcript. Sticky composer: Message, Attach, Send, Voice, End.

FORM: Ant Design operate UI. Signature interaction: default Button/Select/Input; conversation remains one session through Voice.

FINISH: unreviewed and undocumented is unfinished; presentation follows DESIGN.md and `/docs` wins on behavior.

## Scope

- Surface: `web/src/features/chat/ChatApp.tsx` and `web/src/app.css`.
- Preserve testids `connection` and `profile`, accessible names Identity, Start conversation, Send, Voice, Cancel voice, Mute, Unmute, End.
- Composer stays in voice. End remains the existing session-end control until `/docs` and tests change together.

## Shipped assets

- None. Decorative plates and custom fonts are retired.
