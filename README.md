# Colin's Patch Kit

A Slay the Spire 2 mod with various quality-of-life patches.

This mod has been tested with the latest beta branch (`v0.107.0`).

## Patches

All patches are enabled by default, but can be disabled individually via the BaseLib mod configuration UI.

All patches are purely QOL and do not impact gameplay. They are safe to use in multiplayer.

### Map: Show Current Room Tooltip

If enabled, hovering over the current map node will show a tooltip indicating the game state. The vanilla game
only shows this tooltip for previously visited nodes.

![Map tooltip](docs/images/map-tooltip.png)

### Combat: Well Laid Plans Retain Slots

If enabled, choosing cards to Retain with Well Laid Plans shows one dotted card outline ("slot")
per retain charge above your hand. Picking a card fills the leftmost empty slot, and clicking a
selected card returns it to your hand and leaves its slot empty in place — so you can always see
how many cards you can still retain.

![Retain slots](docs/images/retain-slots.png)

### Startup: Skip Intro Logo

If enabled, the Mega Crit logo animation at startup is skipped and the game boots straight to the
main menu.

### Cards: Translucent Ethereal Card Frames

If enabled, the colored frame of Ethereal cards is rendered slightly transparent so they are easy
to spot at a glance. Card text, art and icons stay fully opaque. (Quest and Ancient cards are
excluded; their frames use a different texture layout.)

This UI is experimental. It's unclear what the best way to indicate Ethereal (and eventually Retain, Sly, and Exhaust)
keywords without conflicting with enchantments and afflictions.

### Relics: Ready-Effect Pulses

Vanilla relics with a limited effect (e.g. Vambrace) pulse their inventory icon while the effect
is still available and stop once it is used. Seven relics with the same kind of once-per-turn or
once-per-combat effect don't, so you can't tell whether the effect is still pending. If enabled,
these relics pulse the same way:

- **Permafrost** — once per combat, your first Power card grants Block.
- **Centennial Puzzle** — once per combat, the first time you take unblocked damage you draw cards.
- **Ruined Helmet** — once per combat, your first Strength gain is doubled.
- **Lava Lamp** — pulses while you still qualify for upgraded card rewards (no unblocked damage taken).
- **Demon Tongue** — once per turn, the first unblocked damage taken during your turn heals you.
- **Mini Regent** — once per turn, the first time you spend stars you gain Strength.
- **Music Box** — once per turn, your first Attack is duplicated.

## Installation instructions

You will need to download the `.dll`, `.pck`, and `.json` files from
[Releases](https://github.com/colinking/sts2-patch-kit/releases) and put them in your `Slay the Spire 2/mods` folder.

## Development

### Building

You can build this mod by running `dotnet build ColinsPatchKit.csproj`.

### Publishing

You can publish this mod by running `dotnet publish ColinsPatchKit.csproj`. This should be done after every edit.

### Decompiled source code

The decompiled source code is accessible in: `../../elliotttate/sts2-modding-mcp/decompiled`. This is based on
https://github.com/elliotttate/sts2-modding-mcp.
