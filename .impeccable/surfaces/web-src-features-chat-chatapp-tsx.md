---
version: 1
slug: "web-src-features-chat-chatapp-tsx"
primary_target: "web/src/features/chat/ChatApp.tsx"
related_targets: ["web/src/features/chat/ChatHeader.tsx", "web/src/features/chat/Conversation.tsx", "web/src/features/chat/ChatMessage.tsx", "web/src/features/chat/SpokenText.tsx", "web/src/features/chat/Composer.tsx", "web/src/features/chat/StructuredMessageEditor.tsx", "web/src/features/chat/ComposerChipNode.ts", "web/src/features/chat/MessageParts.tsx", "web/src/features/chat/composerChoices.ts", "web/src/features/chat/ModelPicker.tsx", "web/src/features/chat/AgentPicker.tsx", "web/src/features/chat/SessionRail.tsx", "web/src/features/chat/BackgroundWorkDrawer.tsx", "web/src/features/chat/AgentRunDetails.tsx", "web/src/features/chat/DiagnosticDetails.tsx", "web/src/features/chat/detailPopover.tsx", "web/src/features/chat/SessionArtifacts.tsx", "web/src/features/chat/ArtifactView.tsx", "web/src/features/chat/AttachmentPreview.tsx", "web/src/features/chat/ChatFileCard.tsx", "web/src/features/chat/DrawerListFooter.tsx", "web/src/app/antdTheme.ts", "web/src/features/filePreview/FilePreview.tsx", "web/src/app.css", "web/src/app/AppShell.tsx"]
---

# ChatApp

Mode: Operate. Code-led.

## Direction contract

THESIS: Conversation-first. Retain one Session for text/voice, protected file/work/evidence inspectors, service-owned transport/audio, readable content and the persistent composer. Labeled Tools and Workspace Menu disclose secondary actions; attention stays visible and inspectors restore the stable opener. Scope changes retire private state.

OWN-WORLD: Native Ant Design v6 on evergreen layout/container/elevated surfaces, warm white working text, mint links and independent semantic status colors.

STORY: Understand the authorized owner and current saved state, perform the named task, and inspect its attributable result or explicit recovery.

FIRST VIEWPORT: This composition remains inside its authorized page/Chat/inspector owner. The shared task/scope heading precedes native controls, evidence and actions; narrow layouts wrap and use local overflow.

FORM: Inherit the selected field-guide working grammar, candidate 7, seed `cb1be48c`; contextual classification without literal book decoration.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

The [Product Experience Contract](../../docs/product-experience-contract.md) and [frontend specification](../../docs/13-frontend-implementation-spec.md) own behavior. [DESIGN](../../.agents/context/DESIGN.md) owns concise visual guidance. Ant Design v6 controls and shared layout owners provide the presentation; no second framework or page-local tokens.

## Qualification

The [delivery ledger](../../docs/reports/product-experience-delivery-2026-10-30.md) and [master manifest](../../docs/reports/assets/product-experience/coverage.json) record actual journeys, roles, inspected images and open limits. This refreshed brief is source metadata, not acceptance. Preserve existing meaningful states and operations; verify desktop/tablet/mobile and narrow content/keyboard/authority boundaries.

## Source fingerprints

| Source | SHA256 |
| --- | --- |
| `web/src/features/chat/ChatApp.tsx` | `57acdf3fe6b58aaf217b78720c57ef1939a43357a7d248bf07ab0b333ea8e11f` |
| `web/src/features/chat/ChatHeader.tsx` | `ea3d56a27fa71ad0ef99054f0d18e961c25afd262b63678ef947e068db3d30d7` |
| `web/src/features/chat/Conversation.tsx` | `9e2de6a9e370bf5ec7ea403706e2766609f493537163fe3fc4b9b582018197d0` |
| `web/src/features/chat/ChatMessage.tsx` | `3fa4f8b4d50c04dac522e55aeb4a43827f010e02569404c55d4aab8d0e5b00d9` |
| `web/src/features/chat/SpokenText.tsx` | `272dc2acceed22baaed9074e771adeb8052c2fe6345f62876c2af5ef6a2c6a20` |
| `web/src/features/chat/Composer.tsx` | `b2384249f9b79d33133f23793e261b18fc04cfb229feade14bc812a696083e4e` |
| `web/src/features/chat/StructuredMessageEditor.tsx` | `f1d3c3700ea22d4ee0416417d8f45b6480bd0c4d13637aa5efaa876aaf609bca` |
| `web/src/features/chat/ComposerChipNode.ts` | `62944e22a1e842db3f1077b9816c75726a13bcdcff0bbd34f81d5a4c86745a45` |
| `web/src/features/chat/MessageParts.tsx` | `7d728c8699993d0953b9f651793426f7b4b6c8a9657c3d2b30ba07aea64e5fad` |
| `web/src/features/chat/composerChoices.ts` | `3a9a755f67dcf77437dfd4d1664ab8d8b5c2fcae991e19a546bf3cb07187185b` |
| `web/src/features/chat/ModelPicker.tsx` | `724529af012c71cb59622377600f383b3c918a86d32d94df9046d1f995da7d17` |
| `web/src/features/chat/AgentPicker.tsx` | `ca28b9a9769bfa5aa52ab5ae9632635e772026ad5bdc07614527261afd61aa9e` |
| `web/src/features/chat/SessionRail.tsx` | `df93b5475ea7c5e58c8bc0ce313fad4460d98f1d185bff20355bae430f7f7b02` |
| `web/src/features/chat/BackgroundWorkDrawer.tsx` | `9c6d5aefee5b32688ca79923a8f50bec1936e27481ad9cc7f61c2b942c462e22` |
| `web/src/features/chat/AgentRunDetails.tsx` | `c857b4f456ce7c9691757e59aedd77bdceebb3e404a0c375cc6988914962d6fc` |
| `web/src/features/chat/DiagnosticDetails.tsx` | `4ed6285461c76ef36f0416fc20ce182be43c347449f01bf0c81f02adb7f2183f` |
| `web/src/features/chat/detailPopover.tsx` | `81e986114b6813c91d5ed8b31f8367cd9e71ae99e6e7487bed66da800e64db5b` |
| `web/src/features/chat/SessionArtifacts.tsx` | `e6129e2f9e8ede33dd879760b60dd57e72b2195a16b7afa0b8bb3b76151c4e3e` |
| `web/src/features/chat/ArtifactView.tsx` | `cd27d8c823d5ea6f007459862e5682e9b79294b331dc470c5631015367101d24` |
| `web/src/features/chat/AttachmentPreview.tsx` | `ae54cc454e6202893fc9749fe3bfaf8e9351f01295885e470628b5c00ab8ab54` |
| `web/src/features/chat/ChatFileCard.tsx` | `c7bd029aecff43faafe4561d8066cfae7aece4223a1ff4e155f28c9289319c07` |
| `web/src/features/chat/DrawerListFooter.tsx` | `fcf4c47375dca76abd30ee9bc73ee41b0821d623fb9cca8b4b4b848a6bcc9283` |
| `web/src/app/antdTheme.ts` | `8f13dc29dc96e4ef2dcc86be8150743d7656975cbe202b06f04f039a8a8109ae` |
| `web/src/features/filePreview/FilePreview.tsx` | `8403ea81472d46881839a11373b46c197fe2723194be7ef34b58a7cbd2c5e83f` |
| `web/src/app.css` | `e827076d776b5687e59bd83d86cba88d889203430bc8ae23a14e85c6014e0cae` |
| `web/src/app/AppShell.tsx` | `35826591f0e988cd4a1ac068c23b61fd05404fb8a6a3a1f494832cd32a5998d1` |
