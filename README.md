# Colin's Patch Kit

A mod for Slay the Spire 2 with **15 quality-of-life improvements**. These patches are intended to feel like missing vanilla features.

All patches do not impact gameplay, nor competitive integrity. This mod supports the latest stable version (`v0.107.1`) and the latest beta version (`v0.111.0`), and is safe for use in multiplayer.

Please submit feedback / feature requests [via GitHub](https://github.com/colinking/sts2-patch-kit/issues).

## Installation

Install this mod from [Steam Workshop here](https://steamcommunity.com/sharedfiles/filedetails/?id=3747530432) by clicking `Subscribe`.

## Patches

All patches are **enabled by default** and can be toggled
individually (Main Menu > Mod Configuration > ColinsPatchKit).

- [Map](#map)
  - [Current floor tooltip](#current-floor-tooltip)
  - [Upcoming floor tooltips](#upcoming-floor-tooltips)
  - [Potion chances](#potion-chances)
- [Combat](#combat)
  - [Well Laid Plans retain slots](#well-laid-plans-retain-slots)
  - [Curses \& statuses first when exhausting/transforming](#curses--statuses-first-when-exhaustingtransforming)
  - [More active relic pulses](#more-active-relic-pulses)
  - [Active buff/debuff pulses](#active-buffdebuff-pulses)
- [Menus](#menus)
  - [Random character bans](#random-character-bans)
  - [Shop affordability preview](#shop-affordability-preview)
  - [Hold to confirm leaving a rest site with Tent](#hold-to-confirm-leaving-a-rest-site-with-tent)
  - [View Upgrades toggle](#view-upgrades-toggle)
  - [Multiplayer cards Compendium toggle](#multiplayer-cards-compendium-toggle)
- [Speed-ups](#speed-ups)
  - [Skip game over animations](#skip-game-over-animations)
  - [Move shop hand away faster](#move-shop-hand-away-faster)
  - [Skip special card reward delay](#skip-special-card-reward-delay)
- [Console](#console)
  - [Multi-line console input](#multi-line-console-input)

### Map

#### Current floor tooltip

If enabled, hovering over the current floor on the map will show a tooltip indicating the game state. The vanilla game
only shows this tooltip for previously visited floors.

![Map tooltip](/docs/images/map-tooltip.png)

#### Upcoming floor tooltips

If enabled, hovering over an upcoming floor on the map shows an informative tooltip. It only ever shows wiki-level information a diligent player could already work out —
never the actual pre-rolled outcome of that specific floor:

- **Unknown (`?`)** — the chance it resolves into a Monster / Treasure / Shop / Event. Hold
  Cmd (macOS) / Ctrl (Windows) while hovering to list the events that could spawn.
- **Merchant** — the price ranges for cards, relics and potions, the next card-removal cost, and an indication of which you are expected to be able to afford.
- **Treasure** — the expected rewards.
- **Rest Site** — the possible options, including the max you could heal.
- **Enemy** — the expected rewards. Hold Cmd / Ctrl to list the enemies that
  could spawn. The act's opening combats draw from an
  easy pool and later ones from a hard pool, so the list narrows to just the easy or hard pool
  when every route to the floor guarantees it.
- **Elite** — the expected rewards. Hold Cmd / Ctrl to list the elites that could spawn.
- **Boss** — the expected rewards.

![Upcoming floor](/docs/images/upcoming-floor.png)

#### Potion chances

If enabled, combat map tooltips show the chance of receiving a potion (on previous, current, and upcoming tooltips).

![Potion chances](/docs/images/potion-chance.png)

### Combat

#### Well Laid Plans retain slots

If enabled, choosing cards to Retain with Well Laid Plans shows one dotted card outline ("slot") per card you can retain.

*This only applies on the main branch, as Well Laid Plans was reworked on the beta branch.*

![Retain slots](/docs/images/retain-slots.png)

#### Curses & statuses first when exhausting/transforming

If enabled, curses, statuses, and quest cards will be rendered first (in that order) when choosing a card from your draw pile to exhaust (Cleanse) or transform into a specific
card (Charge, Séance).

In vanilla, these are sorted to the bottom even though they are usually the cards you want to get rid of. Order is otherwise not affected. This mirrors the vanilla behavior when removing cards.

![Curses first](/docs/images/curse-exhaust.png)

#### More active relic pulses

Vanilla relics with a limited effect (e.g. Vambrace) pulse their inventory icon while the effect is available and stop once used.
If enabled, this patch enables pulsing for a handful of additional relics that benefit from this behavior:

- **Permafrost** — once per combat, your first Power card grants Block.
- **Centennial Puzzle** — once per combat, the first time you take unblocked damage you draw cards.
- **Ruined Helmet** — once per combat, your first Strength gain is doubled.
- **Lava Lamp** — pulses while you still qualify for upgraded card rewards (no unblocked damage taken).
- **Demon Tongue** — once per turn, the first unblocked damage taken during your turn heals you.
- **Mini Regent** — once per turn, the first time you spend stars you gain Strength.
- **Music Box** — once per turn, your first Attack is duplicated.
- **Miniature Tent** — at a rest site, while you can still select another option (it lets you take more than one).
- **Shovel** — at a rest site, while its Dig option is unused.
- **Girya** — at a rest site, while its Lift option is unused.
- **Meat Cleaver** — at a rest site, while its Cook option is unused.
- **Pael's Growth** — at a rest site, while its Clone option is unused.
- **Dream Catcher** — at a rest site, while you can still rest for its bonus card reward.
- **Tiny Mailbox** — at a rest site, while you can still rest for its bonus potions.
- **Stone Humidifier** — at a rest site, while you can still rest for its bonus Max HP.

![Relic pulses](/docs/images/relic-pulses.gif)

#### Active buff/debuff pulses

If enabled, buff/debuff icons will also pulse if they have a limited-time effect that is still available:

- **First-each-turn effects** pulse while still available this turn: Echo Form, Iteration, Nostalgia,
  Phantom Blades, Lethality, Unmovable, Pale Blue Dot (stable only), and Smoggy (warning: your first
  Skill will trigger it).
- **Counters** pulse when one step from triggering: Juggling (2 of 3 attacks played), Orbit,
  Automation, Panache, Outbreak (stable only), Cacophony (beta only), and the Aeonglass's Withering
  Presence (your next card play adds a Wither).
- **Countdowns** pulse when the event fires after this turn: The Bomb, Asleep, Slumber,
  Hatch, and the Battleworn Dummy time limit.
- **End-of-turn damage debuffs** pulse the whole time as a "don't forget to block" reminder:
  Constrict and Disintegration.

![Power pulses](/docs/images/power-pulses.gif)

*Juggling pulsing after the second Attack this turn — the next Attack triggers it.*

### Menus

#### Random character bans

If enabled, you can ban characters when selecting a random character. For example, you may want to play a random character other than who you just played as.

First select "Random", then right-click a character to ban it (right-click again to unban); embarking then chooses only from the characters you haven't banned.

In multiplayer, only your own random pick is affected. Due to how random character selection is implemented in multiplayer, readying up with at least one ban will immediately resolve the random character.

![Random character bans](/docs/images/random-ban.png)

#### Shop affordability preview

If enabled, hovering over a shop item will turn other item price's red if you can't afford both.

![Shop prices](/docs/images/shop-prices.png)

*Inspired by [Minty Spire](https://steamcommunity.com/sharedfiles/filedetails/?id=1812723899) from Slay the Spire 1*

#### Hold to confirm leaving a rest site with Tent

If enabled and you have Miniature Tent, proceeding from a rest site requires a long-press to confirm. This serves as a reminder
that you can use all options at a rest site instead of reflexively proceeding after using one option.

![Tent reminder](/docs/images/tent.png)

#### View Upgrades toggle

If enabled, the card reward screen gets the same "View Upgrades" toggle as the deck view.

![Card reward upgrades](/docs/images/card-reward-upgrades.png)

#### Multiplayer cards Compendium toggle

If enabled, the "Multiplayer cards" toggle in the Compendium defaults to match your current run: in singleplayer it defaults off, in multiplayer it defaults on.

### Speed-ups

#### Skip game over animations

The game over screen that renders your score slowly shows one line at a time. If enabled, this patch allows you to click on the screen to skip this animation and full resolve the score UI. This is useful if you just want to get back to the main menu faster to start a new run.

#### Move shop hand away faster

If enabled, the merchant's pointing hand returns to its resting position faster after you
stop hovering over a shop item. This helps prevent the merchant's hand from blocking your view of the shop.

#### Skip special card reward delay

If enabled, taking a "special card" reward (Thieving Hopper, Lantern Key) will send it straight to your deck instead of floating
in the center of the screen for two seconds. This avoids blocking your view and mirrors how normal card rewards work.

### Console

#### Multi-line console input

If enabled, the dev console accepts multiple lines of input. Press Shift+Enter to start a new line
and keep typing, or paste a block of commands straight in. Pressing Enter runs every non-blank line
in order, stopping at the first one that fails. Completions (Tab) and command history (up/down) still
work on the line you're editing. This is handy for pasting a setup script of console commands and
running it in one go.

This patch is **disabled by default**. Compared to the default console, it doesn't support
emacs keybinds and ghost text tab completion previews. If brought to parity, it may be enabled
in the future.
