# CLAUDE.md — Project rules (Cone Rhythm Game v0)

## Source of truth
- Implement according to:
  - `Docs/Specs/player_app_v0_specs.md`
  - `Docs/Specs/chart_editor_v0_specs.md`
- If a behavior is not specified, do **not guess**: propose a small spec patch or ask.

## Project structure (assembly boundaries)
- `Assets/_Project/Shared/` — pure runtime code shared by both apps (data model, JSON IO, math, validation).
- `Assets/_Project/Player/` — player runtime only.
- `Assets/_Project/ChartEditorApp/` — chart editor runtime app (in-game tool), **no UnityEditor APIs**.
- `Assets/_Project/Editor/` — Unity Editor-only tooling (can use `UnityEditor`), keep minimal.

## Unity guardrails
- Do **not** hand-edit Unity prefab YAML or scene YAML.
  - If a scene/prefab change is needed, describe the exact Unity Editor clicks.
- Always commit `.meta` files with any new/moved Unity assets.
- Do not commit `Library/`, `Temp/`, `Logs/`, generated `*.csproj`/`*.sln`.

## Coding standards
- Use descriptive names, standard braces, and clear comments (junior-friendly).
- Prefer pure functions in `Shared` for math/validation.
- Avoid per-frame allocations in runtime hot paths (pooling later).

## Workflow expectations
- Output must include:
  1) Files created/changed (paths)
  2) What to do in Unity Editor (if needed)
  3) Git commands for a clean commit
- Keep changes small and reviewable; one logical feature per commit.