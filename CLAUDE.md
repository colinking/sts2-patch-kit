# CLAUDE.md

Slay the Spire 2 quality-of-life mod: Harmony patches on the game's C# assembly, built as a
Godot 4.5 / .NET 9 class library. See README.md for the user-facing patch list and dev-tool usage.

## Build & publish

- `dotnet build ColinsPatchKit.csproj` — compiles and copies the dll/pdb/manifest into the game's
  mods folder (game path auto-discovered via `Sts2PathDiscovery.props`).
- `dotnet publish ColinsPatchKit.csproj` — additionally exports `ColinsPatchKit.pck` (assets and
  localization) via Godot. Required after any change under `ColinsPatchKit/` (assets, localization).
- The `Alchyr.Sts2.BaseLib` NuGet version is **pinned** in the csproj to the oldest version that
  exposes the config APIs we use (the compile-time reference is the runtime *floor*: installed
  BaseLib >= the pin binds fine; older fails to load the mod with `FileNotFoundException`). Never
  use a floating `Version="*"` — a restore can outrun the installed BaseLib mod and break startup.
  The same floor is declared as `min_version` in `ColinsPatchKit.json` so the game's mod loader
  reports a friendly error instead; keep the two in sync. Only raise the pin when we start using
  newer BaseLib APIs.

## Architecture

- `ColinsPatchKitCode/MainFile.cs` — `[ModInitializer]` entry point: `harmony.PatchAll()` plus
  config registration.
- One file per patch in `ColinsPatchKitCode/Patches/`, typically a static manager class plus small
  `[HarmonyPatch]` classes, each guarded by a `ColinsPatchKitConfig` toggle.
- Scripted verification harnesses live in `ColinsPatchKitCode/E2ETests/` (launch-flag driven, no
  effect during normal play). Namespaces follow folders (`...ColinsPatchKitCode.Patches`, `.E2ETests`).
- Config toggles are static bool properties on `ColinsPatchKitConfig` (BaseLib `SimpleModConfig`).
  Each needs localization keys `COLINSPATCHKIT-<SCREAMING_SNAKE_PROPERTY>.{title,hover.title,hover.desc}`
  in `ColinsPatchKit/localization/eng/settings_ui.json` (packed into the .pck — publish after editing).
- README-only assets (patch screenshots etc.) go in `docs/images/`, which contains a `.gdignore`.
  The repo root is a Godot project exporting with `all_resources`, so images anywhere visible to
  Godot get `.import` sidecars and are baked into the shipped .pck; `.gdignore` keeps docs assets
  out of both. Never put README assets under `ColinsPatchKit/` — that folder ships with the mod.

## Decompiled game source

A decompiled copy of the game's `sts2.dll` is expected at `../../elliotttate/sts2-modding-mcp/decompiled`
(sibling checkout). Hard-won API facts:

- The game's `DevConsole` auto-registers `AbstractConsoleCmd` subclasses found in mod assemblies —
  declaring the class is the whole registration. Console commands can also be invoked
  programmatically: `new DevConsole(shouldAllowDebugCommands: true).ProcessCommand("fight BOWLBUGS_WEAK")`.
- Model ids resolve via `ModelDb.GetId(typeof(X)).Entry` (SCREAMING_SNAKE of the class name;
  power ids keep the `_POWER` suffix, e.g. `WELL_LAID_PLANS_POWER`).
- Real card rendering outside combat: `NCard.Create(model)` + a holder (`NGridCardHolder.Create`) +
  `card.UpdateVisuals(PileType.None, CardPreviewMode.Normal)`; models from `ModelDb.AllCards`.
  The card frame is 300x422 centered on the NCard origin.
- Pooled nodes must be released with `QueueFreeSafely()` (routes IPoolable back to NodePool);
  use `QueueFreeSafelyNoPool()` for plain nodes.
- `CommandLineHelper` exposes arbitrary `--key=value` launch args to mods.
- Harmony patches on `async` methods run prefix/postfix around the kickoff stub, so a postfix
  executes after the method's synchronous prelude (everything before the first true await).
- For scripted UI driving, the game ships `MegaCrit.Sts2.Core.AutoSlay.Helpers`
  (`WaitHelper.Until/ForNode`, `UiHelper.Click/FindAll`) and `AutoSlayer` shows the menu node
  paths for starting a run. Gotchas: `NClickableControl.ForceClick()` only emits `Released`, which
  card holders ignore (drive `NPlayerHand.SelectCardInSimpleMode` via reflection instead),
  end-turn must go through `NEndTurnButton.CallReleaseLogic()` because a synthetic click is a
  no-op when the long-press-to-end-turn preference is enabled, and the real OS cursor resting
  over a hitbox fires genuine focus/unfocus events that race synthetic hover signals (re-fired
  as hover scale tweens move the hitbox edge under the cursor) — park it in an empty window
  corner first with `Input.WarpMouse(tree.Root.GetVisibleRect().Size - new Vector2(8, 8))`.

## Verification

- Launch-flag harnesses (`ColinsPatchKitCode/E2ETests/`) verify patches without manual play;
  shared plumbing (scratch-profile switch, throwaway-run bootstrap) is in `E2ETests/E2EHelpers.cs`.
  Launch the game binary directly from
  `.../Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS`:
  - `"./Slay the Spire 2" --retainslots=3:1 --retainslots-shot=/tmp/retain_slots.png --retainslots-quit`
    — static visual check at the main menu (~15s boot, then screenshots and exits); `3:1` = 3 slots
    with slot index 1 filled by a card.
  - `"./Slay the Spire 2" --retainslots-e2e=<profile>` — full in-combat check: starts a throwaway
    run, jumps to a weak fight, grants Well Laid Plans x2, ends the turn, then picks/deselects/
    re-picks cards, saving /tmp/retain_e2e_*.png at each step, and switches back to the original
    profile before quitting. It **abandons any run in progress on the given profile** — only ever
    pass a scratch profile whose saves are disposable, never one with a real run.
  - `"./Slay the Spire 2" --relicpulse-e2e=<profile>` — verifies the relic ready-pulse patch in a
    real combat: grants Permafrost/Music Box (tracked) plus Pael's Tears (untracked negative
    control), jumps to a weak fight, and logs a `relicpulse-e2e: PASS <assertion>` line per
    expected Status transition (armed at combat start, consumed on Flash, untracked relic never
    pulses across its real turn-boundary trigger, all cleared after combat), saving
    /tmp/relic_pulse_*.png. Same scratch-profile warning as above.
  - `"./Slay the Spire 2" --powerpulse-e2e=<profile>` — verifies the power ready-pulse patch in a
    real combat: applies Echo Form (armed gate), Constrict (constant reminder) and The Bomb x3
    (countdown) to the player, plays through three turns, and asserts the actual `pulse` shader
    parameter on the NPower icons at each step (`powerpulse-e2e: PASS ...` lines), saving
    /tmp/power_pulse_*.png. Same scratch-profile warning as above.
  - `"./Slay the Spire 2" --pulsegif=<profile>` — captures 30 frames (100ms apart) of pulsing
    relics and powers to /tmp/pulse_gif/ for the README GIFs (assemble with ffmpeg crop+
    palettegen). Uses `RenderingServer.ForceDraw(swapBuffers, frameStep)` per frame because
    macOS stops redrawing occluded windows (frozen identical frames otherwise) — never grab
    window focus from a harness instead; stray user keystrokes leak into the game. Same
    scratch-profile warning as above.
- Direct (non-Steam) launches need a `steam_appid.txt` containing `2868840` next to the game binary.
- The game ignores SIGTERM; kill it with SIGKILL. On macOS the log is at
  `~/Library/Application Support/SlayTheSpire2/logs/godot.log` (rotated per launch — the current
  file is the most recent run).
- **After every e2e/preview run, review the log for errors** — don't stop at the harness's own
  "complete" line: `grep -nE '\[ERROR\]|Exception' .../godot.log`. Investigate anything not on the
  known-benign list:
  - `Error deleting path .../current_run.save.backup: Failed` when abandoning a throwaway run that
    only ever saved once (no backup file was created) — vanilla noise, confined to the scratch profile.
  - BaseLib `Failed to open log window: ObjectDisposedException` at shutdown — BaseLib pops its log
    window on any logged error; harmless when it happens while the game is quitting (downstream of
    whatever error preceded it).
  - `Asset not cached` warnings and `does not declare min game version` mod warnings.
- `unlock all` in the dev console (backtick) unlocks everything on a fresh test profile.
