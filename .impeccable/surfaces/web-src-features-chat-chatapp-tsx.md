---
version: 1
slug: "web-src-features-chat-chatapp-tsx"
primary_target: "web/src/features/chat/ChatApp.tsx"
related_targets: []
---

# ChatApp

Mode: Operate. One identity, one conversation; text and voice are modes of the same session.

## Direction contract

THESIS: Agent Core is a sixteen-color dialogue field: presence lives as ordered-dither light in the upper plane, talk docks as a bordered transcript, and Voice is the same session lighting mint. It refuses the cream-paper messenger, the bubble column, and the fake inbox.

OWN-WORLD: Obsidian `#071116` field, `#0D2024` panels, bone `#F2EADF` type, mint `#58E6C2` for live connection, persimmon `#FF6B3D` only for commit/interrupt/fail. Bitmap cell grid, 1px beveled windows, integer-pixel dither, no smooth gradients.

STORY: Pick Alex or Sam, then speak or type in one continuous conversation. Status is labeled (Ready, Starting voice…, Listening, Reconnecting). Synthetic stays named.

FIRST VIEWPORT: Full-bleed field. Wordmark AGENT CORE top-left. Profile and Ready as labeled cues. Identity line. Abstract mint/steel pixel presence top-right. Bordered transcript occupying the mid band. Sticky composer: Message field, Send, Voice, End as labeled keys.

FORM: Pixel Dialogue Field, seed `39fb46bf`, assigned by bolder re-roll then palette lock `obsidian-mint`. Signature interaction: mint phosphor on Ready/Voice; persimmon on Send; presence field dithers, never animates under reduced motion.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Scope

- Surface: `web/src/features/chat/ChatApp.tsx` and `web/src/styles.css`.
- Preserve testids `connection` and `profile`, accessible names Identity, Start conversation, Send, Voice, Cancel voice, Mute, Unmute, End.
- Composer stays in voice. End remains the existing session-end control until `/docs` and tests change together.
- No inbox, attachments, markdown, developer timeline.

## Shipped assets

- Plates: `assets/plates/presence.png`, `assets/plates/field-texture.png` (served from `web/public/plates/`).
