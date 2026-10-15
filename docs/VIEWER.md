# Workflow viewer — plan (WF-T11, built as WF-T10)

A read-only window onto the workflow database: every repo's stories and tickets, with filters, in
MindAttic.Browser.

## Decision: a static export, opened in the Browser as a local site

`workflow view` writes a small site and opens it in MindAttic.Browser:

```
%LOCALAPPDATA%\MindAttic\Workflow\viewer\
  index.html   the page: one file, no build step, no CDN (works offline)
  data.js      window.WORKFLOW = { generated, repos, stories, tickets } from the database
```

```
MindAttic.Browser.exe %LOCALAPPDATA%\MindAttic\Workflow\viewer --host workflow.local
```

**Why static, not live.** The database has one writer: the `workflow` CLI (WF-LAW-1, WF-LAW-2).
A live view would need a local server or a WebView2 host object reading SQLite, and that's a
second path into the store. An export is a snapshot, so it can never write back, and a reader
can't lock the database while an agent is closing a ticket. `workflow view` re-exports on every
run, and the page's Reload button re-reads `data.js`, so a fresh look is one keystroke away.

**Why `data.js`, not `data.json`.** WebView2's folder mapping serves `fetch()` fine, but a
script file also opens straight from disk (`file://`) in any browser. That makes the export
useful without MindAttic.Browser and easy to test.

**Why the Browser needs no change.** "Generalize MindAttic.Browser beyond capture" is already
done: `BrowserTarget.Parse` serves any folder at `https://<host>/` through
`SetVirtualHostNameToFolderMapping` (DenyCors), and opens its `index.html`. The capture bridge
stays limited to that origin, which is harmless here. If the Browser isn't installed
(`C:\Apps\MindAttic.Browser\MindAttic.Browser.exe`), `workflow view` prints the folder and
opens `index.html` with the default browser instead.

## What the page shows

- A repo list with open-ticket and story counts (✅/🟡/⬜), plus a filter box.
- **Tickets**: ID, date, type, status, summary, ref. Filter by repo, status and type; open first.
- **Stories**: grouped by epic, with status glyph and evidence. Filter by repo and status.
- Plain text only. Every value is inserted with `textContent`, never `innerHTML`, so ticket
  text can't inject markup.

## Command

`workflow view [--repo NAME] [--out DIR] [--no-open]`

- `--out` writes the export somewhere else (tests use a temp folder).
- `--no-open` writes the files and prints the path without launching anything.
- `--repo` opens with that repo selected (the export always holds every repo).

## Not now

- Editing from the page (it would bypass the CLI, which is the only writer).
- A Launcher menu item to open the view. That's a one-liner in MindAttic.Launcher once this ships.
- Auto-refresh while open. Revisit if Reload proves too manual.
