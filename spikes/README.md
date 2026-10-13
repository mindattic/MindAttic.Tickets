# Console-menu spikes

Throwaway comparisons of console menu approaches for the MindAttic.Launcher (and any other
MindAttic TUI). Every spike draws the same menu from `Spike.Shared`: a main menu, a 20-project
list, and per-project actions, two levels deep. Each one also has a status line that ticks every
second, standing in for the Launcher's refresh timer. Not part of `MindAttic.Workflow.slnx`;
build them through `Spikes.slnx` or `run.ps1`.

```powershell
./spikes/run.ps1              # list
./spikes/run.ps1 FlickerFree  # build + run one
./spikes/run.ps1 -All         # each in turn; quit one to start the next
```

| # | Spike | What it shows | Redraw model | Cost to adopt in the Launcher |
|---|---|---|---|---|
| 1 | **Baseline** | How the Launcher draws today (`Ui/Menu.cs`, `Ui/Screen.cs`) | Clear, or erase-below, then one `MarkupLine` per row | n/a |
| 2 | **FlickerFree** | Same look, same "redraw the whole page", without the flicker | Off-screen frame, one write, overwrite in place, DEC 2026 sync, alt screen, skip identical frames | One file (`Menu.cs`/`Screen.cs`), no new packages |
| 3 | **SpectreLive** | Spectre's built-in `AnsiConsole.Live` region | Spectre overwrites the region; you still own the key loop and scrolling | Small; no new packages |
| 4 | **RazorConsole** | Razor components on Spectre (`Select`, `Panel`, focus) | Virtual DOM diff, then repaint the live region | Razor SDK + Hosting, Spectre 0.49 → 0.54; pre-1.0 |
| 5 | **XenoAtom** | Retained widget tree (`OptionList`, `DockLayout`, bindable `State<T>`) | Cell-buffer diff; only changed cells are written. F12 shows a perf overlay | Replaces Spectre for interactive screens; net10/C# 14 only (the Launcher already is) |
| 6 | **TerminalGui** | Full windowing toolkit (`Window`, `ListView`) | Dirty-region double buffering | Biggest; looks like a desktop app, not the Launcher |

Keys (all spikes): Up/Down (PgUp/PgDn/Home/End where supported), Enter or → to open, Esc/←/Backspace
to go back, Esc at the root or Q to quit. Exceptions: XenoAtom and TerminalGui quit with Ctrl+Q,
and RazorConsole uses `< Back` / `< Quit` rows (its `Select` swallows Esc) or Ctrl+C.

What to try in each: hold Down on the 20-project list (flicker, tearing), resize the window,
open a project and press Esc (does the highlight come back to where you were?), and watch the
clock line (does anything else repaint when it ticks?).

Versions spiked (2026-10-13): Spectre.Console 0.49.1, RazorConsole.Core 0.6.0,
XenoAtom.Terminal.UI 3.10.0, Terminal.Gui 2.5.0.
