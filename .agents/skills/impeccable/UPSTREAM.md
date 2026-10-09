# Impeccable upstream integration

Installed from the official [skill-v4.5.2 universal bundle](https://github.com/pbakaus/impeccable/releases/tag/skill-v4.5.2) on 2026-10-09. The launcher pins engine **0.1.14** in `scripts/VERSION`.

## Repository adaptations

- Keep one shared skill in `.agents/skills/impeccable`; do not install editor-specific skill copies.
- Preserve the Agent Core component and layout policy in `SKILL.md`. Ant Design v6 remains the generic UI baseline, `.agents/context` supplies tooling context, and `/docs` owns product behavior.
- Adapt Codex and Cursor hook paths to the shared launcher. Keep unrelated editor configuration intact.
- Keep the bundled agent TOML definitions with the shared skill. Cursor agent copies are local, ignored files; refresh existing copies from the matching universal bundle.
- Keep downloaded platform engines and upstream font data local under the existing ignored paths. Verify downloaded engines against the official SHA-256 sidecar before installing them in `scripts/bin/<platform>/`.

## Verification after updates

Run the shared launcher with `engine-probe`, `doctor --json`, and `context --target web/src/features/chat/ChatApp.tsx`. Confirm it resolves `.agents/context/PRODUCT.md`, `.agents/context/DESIGN.md`, and the matching surface brief. Exercise `detect --json` on an existing source file and a disposable fixture with a known detector violation. Exercise the editor hook entry points with their event payloads, check Markdown references, parse JSON/TOML, check browser script syntax, and run `git diff --check`.

The 4.5.2 update passed these local macOS ARM64 checks. Doctor reported no findings; the detector reported `gradient-text` on the disposable fixture. Windows launcher/hook changes follow the official bundle but were not executed on Windows. This tooling update changes no application behavior and opens no product milestone.
