# Portable Codex Workflows

This directory contains a portable Codex plugin marketplace package with five
company-neutral workflow skills. It intentionally contains no employer names,
internal URLs, credentials, proprietary templates, screenshots, database
schemas, or product-specific assets.

## What is and is not synchronized

- Raw personal skill folders are local filesystem content; signing in on a new
  machine does not install those folders automatically.
- A shared workspace plugin is cloud-managed inside that ChatGPT workspace on
  supported plugin surfaces.
- A Git marketplace repository keeps one source of truth across machines. Each
  machine still installs the marketplace and keeps its own connector login and
  local permissions.

## Included skills

- `evidence-backed-handoff-hub`: build traceable, self-contained project handoffs.
- `product-improvement-workbench`: collect evidence and shape review-ready improvements.
- `interactive-checklist-builder`: generate accessible single-file HTML checklists.
- `portable-ai-context-handoff`: prepare minimal, privacy-aware AI session handoffs.
- `asset-driven-ui-composer`: inventory approved images and compose reproducible UI mockups.

## Recommended cloud distribution

The repository-level manifest at `.agents/plugins/marketplace.json` points to
this package. On each Codex machine, add the containing repository as a
marketplace source:

```powershell
codex plugin marketplace add <owner>/<repository>
codex plugin marketplace list
```

Then open the Plugins directory in the ChatGPT desktop app or Codex, select the
marketplace, install `portable-workflows`, and start a new task. Refresh later
with:

```powershell
codex plugin marketplace upgrade
```

Git SSH URLs and HTTPS Git URLs are also supported when shorthand is not
appropriate. Authentication remains local to each machine.

### CLI and IDE fallback

Plugins are not available on every Codex surface. To install the five workflows
as standalone personal skills for local CLI or IDE use, clone the repository and
run from the repository root:

```powershell
python .\ai\portable-workflows\scripts\install_portable_skills.py --dry-run
python .\ai\portable-workflows\scripts\install_portable_skills.py
```

The installer defaults to `~/.agents/skills`, refuses broad target paths, and
skips existing skill directories unless `--force` is supplied.

## Workspace sharing alternative

The ChatGPT desktop app can share a locally created plugin with selected
members of the current ChatGPT workspace from **Plugins > Created by you >
Share**. That route stays inside the workspace boundary and may be disabled by
an administrator. It does not replace local CLI or IDE installation.

## Repository layout

```text
/.agents/plugins/marketplace.json
/ai/README.md
/ai/portable-workflows/
  plugins/portable-workflows/
    .codex-plugin/plugin.json
    skills/
  scripts/install_portable_skills.py
```

The repository-level marketplace manifest uses a relative plugin path, so the
repository can be cloned without rewriting machine-specific paths.

## Safety before publishing

1. Confirm the repository visibility and perform a deliberate content review.
2. Do not add source-company decks, images, logs, URLs, schemas, or personal data.
3. Store secrets in approved credential systems, never inside a skill or Git.
4. Re-run the included validators after every material change.
5. Review the destination company's policy before installing personal plugins.
