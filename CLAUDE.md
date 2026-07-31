# CLAUDE.md

Slay the Spire 2 quality-of-life mod: Harmony patches on the game's C# assembly, built as a
Godot 4.5 / .NET 9 class library. See README.md for the user-facing patch list and dev-tool usage.

## Publishing

- `dotnet publish ColinsPatchKit.csproj` — the one command for every change. It compiles, copies the
  dll/pdb/manifest into the game's mods folder (game path auto-discovered via `Sts2PathDiscovery.props`),
  exports `ColinsPatchKit.pck` (assets and localization) via Godot, and stages the three shipped
  files (dll, manifest, pck — no `.pdb`) into `release/ColinsPatchKit/content/` for both the Steam
  Workshop upload and the GitHub release zip. **Always publish, never a bare compile** — otherwise
  the installed `.pck` goes stale or missing and in-game text falls back to raw `COLINSPATCHKIT-*`
  loc keys (and assets vanish). It's cheap to always publish: the Godot export is skipped
  automatically when nothing under `ColinsPatchKit/` changed, so a code-only publish costs about the
  same as a plain compile.
- `dotnet publish ColinsPatchKit.csproj -t:UninstallMod` — removes the locally installed mod folder
  (`<game>/mods/ColinsPatchKit/`). Also runs automatically as part of `dotnet clean`. Use it to test
  the Steam Workshop copy, which the local install otherwise always shadows (the game's mod loader
  hardcodes local `mods/` to win over Steam for the same id).
- The `Alchyr.Sts2.BaseLib` NuGet version is **pinned** in the csproj to the oldest version that
  exposes the config APIs we use (the compile-time reference is the runtime *floor*: installed
  BaseLib >= the pin binds fine; older fails to load the mod with `FileNotFoundException`). Never
  use a floating `Version="*"` — a restore can outrun the installed BaseLib mod and break startup.
  The same floor is declared as `min_version` in `ColinsPatchKit.json` so the game's mod loader
  reports a friendly error instead; keep the two in sync. Only raise the pin when we start using
  newer BaseLib APIs.

## Releasing

A release is a git tag, a GitHub release whose only asset is a zip of the built mod folder, and a
Steam Workshop update. Releases are cut by hand (helper scripts assist, but there's no one-shot
release script); follow the steps below. **Always create the GitHub release
as a draft (`--draft`) so Colin can review the notes and asset before publishing** — never publish
directly. Steps:

1. Pick the version (semver, `vMAJOR.MINOR.PATCH`). New patches → minor bump; fixes only → patch.
   Confirm the number and the "Tested with" game version with Colin if unsure.
2. Bump the version and finalize the Workshop metadata (all of it lands in one commit, step 4), then
   `dotnet publish`:
   - `"version"` in `ColinsPatchKit.json` → the new tag.
   - `changeNote` in `release/ColinsPatchKit/workshop.json` → the new release URL
     (`https://github.com/colinking/sts2-patch-kit/releases/tag/<version>`; keep the file's UTF-8
     BOM). `title`, `visibility`, `tags`, and `dependencies` (BaseLib = `3737335127`) rarely change.
   - Only if patches changed: refresh `release/ColinsPatchKit/content/README.md` (the concise
     Workshop-facing patch list — it renders as the Workshop description and ships inside `content/`,
     excluded only from the GitHub zip), then re-render the description with
     `python3 release/update_workshop_json.py` (converts `content/README.md` to Steam BBCode into
     `workshop.json`'s `description`, aborting past Steam's 8000-char limit). Otherwise leave
     `description` alone.

   `dotnet publish` then stages the manifest, dll, and pck (carrying the new version) into both the
   game's mods folder and `release/ColinsPatchKit/content/`, and syncs `image.png` from its system of
   record `ColinsPatchKit/mod_image.png`.
3. Build the GitHub asset from the staged `release/ColinsPatchKit/content/` (dll, manifest, pck —
   the shipped `README.md` is **not** included in the zip). Stage a copy renamed to `ColinsPatchKit/`
   so it nests under a top-level dir and extracts straight into `mods/`, then zip it. From the repo
   root:

   ```sh
   ROOT=$PWD
   VERSION=$(grep -o '"version": "[^"]*"' ColinsPatchKit.json | cut -d'"' -f4)
   rm -rf /tmp/ColinsPatchKit && cp -R release/ColinsPatchKit/content /tmp/ColinsPatchKit
   rm -f /tmp/ColinsPatchKit/README.md
   rm -f "release/ColinsPatchKit-$VERSION.zip"
   ( cd /tmp && zip -rX "$ROOT/release/ColinsPatchKit-$VERSION.zip" ColinsPatchKit -x '*.pdb' '*.DS_Store' )
   rm -rf /tmp/ColinsPatchKit
   ```

   The zip is gitignored (`release/*.zip`); verify its contents (`unzip -l`) and the manifest version
   inside it.
4. Commit `Release <version>` — the manifest version bump and the Workshop metadata from step 2
   (`workshop.json`, plus `content/README.md` if patches changed) in a single commit. Then
   `git tag <version>` and push both `main` and the tag.
5. `gh release create <version> --draft --title <version> --notes-file <notes> "<zip>#<zip>"`.
   Notes structure (see prior releases for tone): one-line summary + current patch count, then
   `## New patches`, `## Changes`, `## Removed` as applicable, a link to the README patch list, and
   an `## Installation` blurb (BaseLib floor + "extract into mods/" + "Tested with `vX`"). Hand Colin
   the draft URL to review and publish.
6. Publish the Steam Workshop update. `release/ColinsPatchKit/` is the upload payload — step 2 already
   staged the binaries into `content/`, synced `image.png` from its system of record
   `ColinsPatchKit/mod_image.png`, and finalized `workshop.json`, so nothing more is edited here.
   Instruct Colin to perform the final release by:
   1. Building the uploader: in the `megacrit/sts2-mod-uploader` checkout, `git pull`, then
   `dotnet publish -c Release -r osx-arm64 -p:PublishTrimmed=true --artifacts-path osx-arm64`.
   2. Performing the upload (the Steam client must be running and logged in): from
   `megacrit/sts2-mod-uploader/osx-arm64/publish/ModUploader/release_osx-arm64`, run
   `./ModUploader upload -w /Users/colin/dev/github.com/colinking/sts2-patch-kit/release/ColinsPatchKit`.
   The uploader reads `workshop.json`, pushes `content/` and `image.png`, and updates the existing
   item identified by `mod_id.txt` (published mod `3747530432`).

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
  The repo root is a Godot project exporting with `all_resources`, so files anywhere visible to
  Godot get baked into the shipped .pck (images also get `.import` sidecars); a `.gdignore` keeps a
  folder out of both. `docs/images/`, `release/`, and `decompiled/` each have one — `release/`'s
  keeps the Steam Workshop upload payload (`workshop.json`, the duplicate `image.png`, `content/`)
  out of the shipped mod, and `decompiled/`'s keeps the exporter from baking thousands of dead
  `res://decompiled/*.cs` path stubs into the .pck (it's committed via a `.gitignore` exception,
  and `scripts/decompile-game.sh` re-creates it). Never put README assets under `ColinsPatchKit/` — that folder ships with the mod.
  Note `ColinsPatchKit/mod_image.png` *is* under the shipped folder (it's the in-game mod image),
  so its `.import` sidecar is committed and it is intentionally baked into the .pck.

## Dual game-branch support

When Steam carries two live game branches (e.g. stable v0.107.1 and beta v0.108.0), the one
shipped dll must run on both. Rules: never compile-time-reference a symbol that exists in only
one branch — resolve those with `AccessTools` (`TypeByName` for branch-only types, `Property`/
`Method` with fallback names for renames) — and when a *mechanic or API shape* differs across
branches, pick the behavior by feature-detecting the loaded assembly (e.g. "does this power
declare its own `DisplayAmount` override", see `PowerReadyPulsesManager`'s static ctor), not by
parsing the game version. Exception: a *balance-only number* that exists solely as a private
const the game inlines (no public surface to read) is keyed off the game's semver instead —
use `GameVersionHelper.CompareTo(major, minor, patch)` (wraps `ReleaseInfoManager.Instance.SemVer`,
public on both branches; see `CookMaxHpGain` in `MapNodeInfoTooltipPatch`); reflecting on a private field
would silently break if it's renamed, while a version threshold only goes stale on a rebalance,
which the per-update decompile sweep catches. Verify any newly referenced symbol exists in
*both* `decompiled/<version>/` trees.
The compile-time reference is whatever build is installed; either branch works since the shared
surface is what the code may bind to.

## Decompiled game source

A decompiled copy of the game's `sts2.dll` lives in-repo at `decompiled/<version>/` (gitignored),
one folder per game build so you can keep versions side by side and diff how internals change across
updates. Generate it with `scripts/decompile-game.sh` (ilspycmd; decompiles the installed game by
default, or pass `--dll <path>` to point at an old depot downloaded via Steam's `download_depot`).
The version label is read from the build's own `release_info.json`, so the folder name (e.g.
`decompiled/v0.107.1/`) is the build it came from — prefer the folder matching the installed version
(the most recent in `godot.log`). Namespaces are folders under it, so e.g.
`decompiled/v0.107.1/MegaCrit.Sts2.Core.Runs/RunManager.cs`.

The script also copies the game's `sts2.xml` (the compiled C# doc-comments, shipped next to `sts2.dll`
in the install) into `decompiled/<version>/sts2.xml`. It's `<summary>` prose on the public surface
only — no method bodies, no private/internal members (and ~half the entries are Godot's autogenerated
`MethodName`/`PropertyName` cached-string boilerplate). So use **the XML for intent/contracts**
(MegaCrit's own rationale and gotchas, authoritative where a decompiled name is ambiguous; it also
feeds IDE IntelliSense from its install location beside the dll) and **the decompiled source for
implementation** (actual behavior, private members, finding Harmony patch targets and exact
signatures). Both regenerate per build, so re-run the script after a game update to track the new
version.

Hard-won API facts:

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

- Localization is guarded by a standalone xUnit project, `ColinsPatchKit.Tests/` — run with
  `dotnet test ColinsPatchKit.Tests`. It needs **no game install** (it reads the loc JSON and source
  text directly), is deliberately kept **out of `ColinsPatchKit.sln`** so the mod build/publish flow
  never drags it in, and is excluded from the mod's own compile via `<Compile Remove="ColinsPatchKit.Tests/**"/>`.
  It asserts: every supported locale is present with both loc files (and nothing extra); every
  locale's file shares the English key set exactly; no value is blank; every `Loc("X")` key referenced
  in code exists in `map.json` and vice-versa (no dead keys); and the `settings_ui.json` keys match
  exactly what `ColinsPatchKitConfig`'s toggles/sections imply. Update the `SupportedLocales` list in
  the test only when the *game* adds/removes a shipped locale.
- Launch-flag harnesses (`ColinsPatchKitCode/E2ETests/`) verify patches without manual play;
  shared plumbing (scratch-profile switch, throwaway-run bootstrap) is in `E2ETests/E2EHelpers.cs`.
  Launch the game binary directly from
  `.../Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS`. **Prefer launching via macOS `open -g`**
  so the game doesn't steal foreground focus — the app activates itself at process launch (before
  any mod/`NoFocus` code runs), which a direct `./binary` invocation can't avoid; `-g` opens it in
  the background instead. Steamworks still initializes (steam_appid.txt resolves next to the binary,
  not from cwd). Pass launch flags after `--args`:
  `open -g -n -a ".../Slay the Spire 2/SlayTheSpire2.app" --args --powerpulse-e2e=2`. `open` returns
  immediately and detaches the process; monitor `godot.log` and `pkill -9 -f "Slay the Spire 2"` to
  stop it. The bare-binary form below still works but grabs focus, so reserve it for the rare case
  Steam isn't running. The per-flag examples below list just the flags:
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
  - `"./Slay the Spire 2" --choosecard-e2e=<profile>` — verifies the View Upgrades toggle on the
    choose-a-card screen (relics that offer a card choice on pickup): starts a throwaway run,
    grants Lead Paperweight via the console (its pickup effect opens the screen out of combat),
    and logs a `choosecard-e2e: PASS <assertion>` line per step (tickbox exists, ticking previews
    upgraded cards while `CardModel` keeps reporting the base card, unticking reverts, picking
    while previewing adds the unupgraded card to the deck), saving /tmp/choose_card_*.png. Same
    scratch-profile warning as above.
  - `"./Slay the Spire 2" --simplegrid-e2e=<profile>` — verifies the View Upgrades toggle on the
    simple card-select grid (Room Full of Cheese "Gorge", Brain Leech, Sea Glass, Sealed Deck):
    starts a throwaway run, obtains Sea Glass in code (CharacterId pre-assigned, so no
    missing-character error), and logs a `simplegrid-e2e: PASS <assertion>` line per step
    (tickbox exists, ticking flips `NCardGrid.IsShowingUpgrades` and previews the holders,
    unticking reverts, confirming a pick while previewing adds the unupgraded card), saving
    /tmp/simple_grid_*.png. Same scratch-profile warning as above.
  - `"./Slay the Spire 2" --pulsegif=<profile>` — captures 30 frames (100ms apart) of pulsing
    relics and powers to /tmp/pulse_gif/ for the README GIFs (assemble with ffmpeg crop+
    palettegen). Uses `RenderingServer.ForceDraw(swapBuffers, frameStep)` per frame because
    macOS stops redrawing occluded windows (frozen identical frames otherwise) — never grab
    window focus from a harness instead; stray user keystrokes leak into the game. Same
    scratch-profile warning as above.
  - `"./Slay the Spire 2" --conflag-sandbox=<profile>` — manual playtest setup (not an
    automated assertion harness): starts a throwaway **Ironclad** run, jumps to the Thieving
    Hopper fight (the bug that steals a card), grants 100 Strength and stocks Conflagration
    (x2 hand, x4 draw), then **stops and hands control to you** — it does NOT end turns, kill
    enemies, restore the profile or quit. Same scratch-profile warning as above; quit to menu
    and switch back to your own profile when done. Verify the setup via the
    `conflag-sandbox:` log lines (each console step logs success), not a "complete" line.
  - `"./Slay the Spire 2" --gameover-skip-sandbox=<profile>` — manual playtest setup (not an
    automated assertion harness) for the click-to-skip game-over patch: starts a throwaway run,
    jumps to the Bowlbugs (weak) fight, then runs the `die` console command to lose, landing you
    on the run-summary screen, and **stops and hands control to you** — it does NOT restore the
    profile or quit. Click Continue, then click (or press confirm) to fast-forward the score-line
    reveal. Same scratch-profile warning as above; quit to menu and switch back to your own
    profile when done. Verify the setup via the `gameover-skip-sandbox:` log lines, not a
    "complete" line.
  - `"./Slay the Spire 2" --restsitehold-sandbox=<profile>` — manual playtest setup (not an
    automated assertion harness) for the rest-site hold-to-confirm patch: starts a throwaway run,
    grants Miniature Tent, jumps to a rest site, and **stops and hands control to you** — it does
    NOT restore the profile or quit. Pick an option (e.g. heal); with Tent the other stays
    selectable, so leaving via proceed now requires a hold (a quick click is a no-op, a gold bar
    fills as you hold ~0.5s, releasing early cancels); use the second option and proceed reverts to
    an instant click. Same scratch-profile warning as above; quit to menu and switch back to your
    own profile when done. Verify the setup via the `restsitehold-sandbox:` log lines, not a
    "complete" line.
  - `"./Slay the Spire 2" --multilineconsole-sandbox=<any>` — light assertion harness + dev sandbox
    for the multi-line console patch. Needs no run or profile (the console works at the menu); the
    argument is ignored. Opens the dev console and drives its real input path (events via
    `Input.ParseInputEvent`) to exercise the overlay existing, a multi-line block running
    sequentially on Enter, Shift+Enter inserting a newline, and Tab completion — logging
    `multilineconsole-sandbox: ...` lines and saving /tmp/ml_console_*.png at each step, then leaving
    the console open so you can keep typing by hand. Verify via the log lines (each states its
    expected value); it does not quit.
- Window placement: whenever any of the above launch flags is present, `TestWindowPatch`
  (`E2ETests/TestWindowTool.cs`) wraps `NGame.ApplyDisplaySettings` so the game boots **windowed**
  (1280x800, top-left of the target display) instead of fullscreen — it must *prevent* fullscreen,
  not undo it, because once macOS puts the window in a fullscreen Space a later
  `WindowSetMode(Windowed)` doesn't reliably stick. This keeps a test run from hijacking the active
  screen. Target display defaults to the physically smallest screen — the laptop built-in on a
  laptop+external setup, which macOS may report as the *primary* "Main" display, so target by size
  not index (override with `--test-screen=<index>`). Automated harnesses also get
  the `NoFocus` window flag so the game never steals keyboard focus; `--conflag-sandbox` stays
  focusable since you play it. The patch snapshots and restores every `SettingsSave` display field
  it touches (Fullscreen/TargetDisplay/WindowSize/WindowPosition) — that store is global and
  `NGame.Quit()` persists it, so without the restore a test run would overwrite the user's real
  fullscreen/resolution preferences (settings live at `.../SlayTheSpire2/steam/<id>/settings.save`).
  Note this also shrinks `--pulsegif`/`--retainslots` captures to the 1280x800 window; re-derive
  GIF crop coords if you regenerate README assets, or pass `--test-screen` to a full-size display.
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
  - `InvalidOperationException: Dev console used before being created` from
    `NHotkeyManager._UnhandledInput` at boot (seen since v0.108.0) — a vanilla race where an input
    event arrives before the console node exists; it logs before any mod has loaded.
- `unlock all` in the dev console (backtick) unlocks everything on a fresh test profile.
