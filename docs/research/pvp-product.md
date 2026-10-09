# PvP: product and player experience

Status: research proposal, 2026-10-09. Nothing here is built. Grounded in the current pause policy
(`pause_sync`, `menu_guard_sync`, `pause_button_hook`; limitations.md "Pausing"), the console
(`say`, `who`; CONSOLE.md), the chat overlay, status toasts, the Co-op menu and server browser, and
the host-authoritative settings model in CONFIG.md (`enemy_health`, `corpse_loot`, `inventory_mode`).

## 1. Formats

| Format | Fit | Verdict |
| --- | --- | --- |
| Casual duel in the campaign world | Natural for a slow game: two characters, one agreed fight, no hurry. Cheapest to build because both bodies already exist in the world. | MVP |
| Dedicated arena | Needs map, spawns, loadouts, a way out of the campaign. Large scope. | Later |
| Team fights with companions | Each side is 3 bodies, queued actions and AI companions. Highest chaos, hardest to balance, hardest to sync. | Opt-in extra, after duels |

Pause is the hard part. The mod's current rule is cooperative: the pause key pauses both games, and
a menu pauses only your own game, covered by a short "cannot be hit" cushion. In PvP that cushion is
an exploit (open a menu, cannot be hit). Proposed rules:

- **Shared pause stays, but is spent from a budget.** Either duellist can pause (both games stop, as
  today). Each gets 3 pauses per fight, 30 seconds each, shown on screen. Pause is for queuing
  actions, which is how KOTOR is meant to be played, so it is kept rather than banned.
- **Menus do not protect you during a duel.** The menu cushion is switched off for duellists. Opening
  inventory or the map mid-duel counts as a pause and spends the budget. When it runs out the menu
  still opens, but the game keeps running.
- **Bystanders are not paused by a duel** beyond what `pause_sync` already does today.
- Medpacs and adrenals stay allowed but share a short cooldown (see section 4), so a duel is not
  decided by who carries more.
- Setting `pvp_pause = shared | budget | off`. `off` means real-time, no pausing.

## 2. Starting a fight

Chosen surface: **chat command plus a dialogue-style prompt**. Chat is the one UI already built for
talking between players; the prompt reuses the on-screen toast and the dialogue-follow look, so
nothing new has to be learned. An in-world "interact with their body" option is attractive but
risky: a stray click on a friend would start a fight offer, so it is a later extra, never the
default.

Flow:
1. Challenger types `duel <name>` (or `duel` on a targeted player). Needs `pvp = duel` on the host.
2. The other player sees a toast and a chat line, and a two-choice prompt. No click is needed to
   decline: ignoring it for 30 seconds is a no.
3. On accept, both see a 5 second countdown. Both characters are set hostile to each other only.
4. The fight ends at defeat, `yield`, a disconnect, or an area change. Nobody actually dies.

Text (house style, plain):

- Challenger, after typing: `You challenged Aria to a duel. Waiting for an answer.`
- Target toast and chat: `Aria challenges you to a duel. Type accept or decline. No answer in 30 seconds counts as no.`
- Decline, shown to challenger: `Aria said no. Nothing happened.`
- Timeout: `Aria did not answer. Nothing happened.`
- Countdown: `Duel with Aria starts in 5. Type yield at any time to stop.`
- Pause line: `Aria paused the duel. You each have 2 pauses left.`
- End: `Aria won the duel. Both of you are back at full health. Nothing was lost.`
- Blocked: `The host has not turned on duels. Ask them, or see the pvp setting.`
- Refused when busy: `Aria is in a conversation or a fight. Try again in a moment.`

Consent and cancellation: no fight without an explicit accept from both; `yield` and `stop` (either
duellist) end it instantly with no penalty; an area change, loading screen, or dialogue ends it as
cancelled. A player can type `duel off` to refuse all challenges quietly (no message sent to the
challenger beyond "not accepting duels"). Where it lives: console commands and chat for entry; toast
for prompts; the existing status line for "In a duel with Aria" and the pause budget.

## 3. Fairness

| Option | Pros | Cons |
| --- | --- | --- |
| Raw characters | Zero setup, honest to the campaign | A level 20 Sith vs a level 4 Soldier is not a fight |
| Level-normalised | Fair on paper | Needs engine stat rewrites; feats, powers and gear still diverge |
| Fixed arena loadouts | Truly fair | Not a campaign character; only for arena |
| Handicaps | Cheap, readable, keeps the character | Approximate |

Recommendation for duels: **raw characters plus an automatic handicap, with a level-gap warning.**
The accept prompt shows both levels. If the gap is more than 3 levels the prompt adds: `You are 6
levels apart. Aria will fight at reduced strength to even it out. Accept?` The stronger player's
hit points and damage are scaled down by a modest percentage per level of difference, capped (say
50 percent), reusing the host-authoritative scaling the mod already does for enemies
(`enemy_health`/`enemy_damage` approach). Gear stays real. Arena mode, later, uses fixed loadouts at
a chosen level, which is the only truly fair option.

## 4. Settings and consequences

Proposed `coop.ini` keys, all host-authoritative like `corpse_loot`: joiners adopt the host's value.

| Key | Default | Meaning |
| --- | --- | --- |
| `pvp` | `off` | `off`, `duel` (challenge and accept), later `arena` |
| `pvp_pause` | `budget` | `shared`, `budget`, `off` (real-time) |
| `pvp_pauses` | `3` | pauses per player per fight in `budget` mode |
| `pvp_force_powers` | `on` | `off` stops Force powers, for a plain blaster-and-sword fight |
| `pvp_consumables` | `limited` | `on`, `limited` (shared cooldown), `off` |
| `pvp_companions` | `off` | whether companions fight; off means they stand aside |
| `pvp_handicap` | `auto` | `auto`, `off`, or a percent cap |
| `pvp_stakes` | `none` | `none` only for now |

Consequences, default and only mode at MVP: **nothing is lost.** No XP, no loot transfer, no death
penalty, no save change. At the end, health and Force points are restored to what they were before
the countdown, and the duel happens inside the campaign world without touching the story. This
avoids the worst class of bug, a duel corrupting a campaign save (compare the staged-save and
snapshot lessons in the memory notes). Optional later stakes: bragging scoreboard in the chat
history only. XP and loot stakes are explicitly not recommended, because they reward a single
bully on a public server.

## 5. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Griefing (forced fights, killing a friend mid-quest) | Consent required both ways; PvP off by default; no damage outside an accepted duel |
| Accidental hostility (stray blaster, area effects, companions hitting a friend) | Hostility is scoped to the two duellists only; bystander and companion damage is blocked; no friendly fire outside a duel |
| Harassment on public servers | `duel off` per player, host `pvp = off`, duel spam limited to one pending challenge per target per minute, host can `kick`; mute is a likely addition |
| Quest or dialogue breakage | Duel refused during conversation, cutscene, or an unfinished area change; auto-cancel on any of them |
| Campaign-save corruption | Duel adds no persistent state; everything restored after; never save mid-duel (block autosave while one runs) |
| Sync cheating via the pause budget | Host counts the pauses; clients cannot extend them |
| Disconnect used to escape a loss | Counts as a forfeit, no consequence beyond the chat line |

## 6. MVP and roadmap

**MVP:** `pvp = off | duel`; `duel`, `accept`, `decline`, `yield`, `duel off`; budgeted pause with
menu cushion off; automatic restore at the end; level display and a simple handicap; chat and
toast text above. 1v1 only, no companions, no stakes.

**Roadmap:** (1) MVP duels. (2) Handicap tuning and `pvp_force_powers`, `pvp_consumables`. (3)
Optional in-world interaction and a Co-op menu toggle. (4) Arena mode with fixed loadouts and a
separate arena area. (5) Companion team fights. (6) Scoreboard.

**Plain-language paragraph for README / limitations once shipped:**

> **Duels.** By default, players cannot hurt each other. If the host turns duels on, any player can
> challenge another by typing `duel` and their name. Nothing happens unless they type `accept`, and
> either of you can type `yield` to stop. During a duel each of you can pause three times, and
> opening a menu counts as a pause. When it ends, everyone is back at full health: nothing is lost,
> no experience is gained, and your save is not touched. If you are far apart in level, the
> stronger character is weakened a little to even things out. You can type `duel off` to refuse all
> challenges. Companions do not join in.
