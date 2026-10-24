# Light sources: groups, debug view and configuration

How to see which of the game's objects give off light, find the ones that are
missing or wrong, and turn them on or off as light sources in MoonDoes
CRT-Vision (Castlevania: Symphony of the Night, SymphonyRecomp).

Everything is done from the mod's window, the debug view and a few lists in
`crtvision.json`; no code changes are needed.

---

## 1. The three light groups

Dynamic light is split into three groups. Each is turned on separately and has
its own controls; turn on one, several or all three.

| Group (mod window) | Key in `crtvision.json` | What gives light | Default |
|---|---|---|---|
| **Candles and torches (any flame colour)** | `candleLight` | The rooms' candles and torches, whatever their flame colour (orange, blue…), while they burn | On |
| **Death fire (destroyed enemies and broken candles)** | `deathFireLight` | The fire enemies burst into when destroyed, and candles when broken | Off |
| **Fire attacks (player and enemies, not magic)** | `fireAttackLight` | Every fire attack, yours and the enemies' (fireballs, flames, Hellfire…). Magic without fire does not | Off |

Each group's controls (shown once it is on):

| Control | What it does |
|---|---|
| **Intensity** | Strength of all the group's light (halo and room). 0 turns it off, 1 is natural, 2 double |
| **Halo radius** | Size of the glow around the source itself (the flame), in PSX pixels |
| **Room lighting** | How much the group lights up nearby walls, floors and characters |
| **Light reach** | How far that room light goes, in PSX pixels |

---

## 2. How the mod decides what gives light, and in which group

Every frame the mod walks the game's object table (up to 256 objects: candles,
enemies, projectiles, explosions, effects…). Each object is run by an update
*function*, which the recomp's symbol table names (`EntityExplosion_no0`,
`EntityCtulhuFireball`…). The rules apply in this order; the first match
decides:

| Order | Rule | Group |
|---|---|---|
| 1 | The name matches an entry of **`lightNever`** | None: no light |
| 2 | The name matches a group's **`always`** list | That group, always: the whole object, any colour, animated or not |
| 3 | It is a **candle** (the stage's breakable object: `EntityBreakable…`, or the stage's unnamed object 1) | Candles, only while its flame animates. Vases and busts are the same object but never animate: no light |
| 4 | Its name has `Candle`, `Torch` or `Lamp` | Candles |
| 5 | Its name contains a fire word: `Fire`, `Flame`, `Explosion`, `Hellfire`, `Burn`, `Blaze`, `Ember`, `Lava`, `Magma`, `Meteor`, `CtulhuDeath` (except smoke: `SmokePuff`, `GreyPuff`, `DeathPuff`, `Smoke`) | By where it is born (below) |
| 6 | Unnamed (`func_…`), from the stage, and additive and animating, or an enemy's projectile | By where it is born, through its fire-coloured pixels only (red, orange, yellow) |
| 7 | Anything else (water, dust, after-images, lightning, magic, items…) | None |

**By where it is born:** a broken candle's flames, a destroyed enemy's fire and
an attack's fire are often the same game object (`EntityExplosion…`). The mod
tells them apart by where and when they appear:

- Born where a lit candle has just broken, or where an enemy has just died (up
  to 1.5 s before) → **death fire**.
- Anything else → **attacks**.

Inside a light-giving object only its bright pixels emit, and only within its
area (the debug view's rectangle). That is why a character walking past a
candle never glows.

**Taking light is not giving it.** Characters, life gauges and walls near a
candle are lit, but they do not become light sources:

- On candelabras only the band of the flames emits (the top edge of their
  area), not the stem: a character passing in front does not.
- On the life gauges (Richter's, the boss's and Alucard's HUD) only what has
  exactly the flame's hue emits, and the mod does not learn the candles' colour
  there. These areas are invisible: the gauges still take light like
  everything else.

**Off screen:** the light is worked out with a 128-pixel margin around the
screen, so a candle off screen still lights what is in view. The mod remembers
each kind of candle it has seen burning in the stage (with its flame colour) and
where each lit candle stands in the room. A candle scrolling out keeps shining
and fades as it gets further away; one not yet in view already shines if it is
of a kind seen before. The first candle of a new kind takes a fraction of a
second to light up: the mod has to see it burn once.

---

## 3. Turning the debug view on

The debug view has no switch in the mod's window, to keep it clean. Turn it on
in `crtvision.json` with the game closed:

```json
"debugLights": true,
```

**Recommended:** turn **Frame generation 60 → 120** off while debugging.
Generated in-between frames are presented without the debug overlay, so labels
flicker and may be missing from screenshots.

Set it back to `false` when done. The debug view does not change the lighting,
it only shows information. It labels objects even when their group is off, so
you can see what a group would light before turning it on.

---

## 4. What the screen shows

Every object on screen has a label with its name and, if it is fire, its group
in brackets:

| Label | Meaning |
|---|---|
| **Yellow** `[candle]` + yellow rectangle | Gives light in the candles group |
| **Orange** `[death]` + orange rectangle | Gives light in the death fire group |
| **Red** `[attack]` + red rectangle | Gives light in the attacks group |
| **Grey** `[death, off]` (or `candle`/`attack`) | Belongs to that group, but the group is off |
| **Grey**, no brackets | Not fire: gives light in no group |

The rectangle is the area where its bright pixels emit. The text is the function
name **without the `Entity` prefix**: `Explosion_st0` on screen is
`EntityExplosion_st0`.

Reading names:

| Form | Meaning |
|---|---|
| `CtulhuFireball` | Named, shared function |
| `Explosion_no0` | The suffix is the stage (`_no0`, `_st0`, `_rare`…) when several stages have a function by that name |
| `RicEntityWhip`, `ST0_EntityLockCamera` | Richter (`Ric`) or stage (`ST0_`) prefixes |
| `func_801A805C` | Unnamed function: its address only. **The same address is a different object in another stage** |

A candle's label is grey when it appears and turns yellow a fraction of a
second later, once the mod has seen its flame animate. That is expected.

Common stages: `st0` prologue · `no3`/`np3` castle entrance · `no0` marble
gallery · `no1` outer wall · `lib` long library · `nz0` alchemy laboratory ·
`dai` royal chapel · `no4` underground caverns · `are` colosseum · `nz1` clock
tower · `top` castle keep · `cat` catacombs · `chi` abandoned mine · `no2`
Olrox's quarters · `cen` centre cube. The inverted castle's start with `r`
(`rno0`, `rlib`…). Bosses use `bo0`…`bo7` and `rbo0`…`rbo8`.

---

## 5. The `light-objects.txt` log

While the debug view is on, the mod lists every kind of object it meets, once per
stage and group, in `light-objects.txt` next to `crtvision.json`
(`SymphonyRecomp/mods/crtvision/light-objects.txt`):

```
# 2026-10-23 13:34 stage | name | group | additive
st0 | func_801A805C | none | yes
st0 | func_801A805C | candle | yes
st0 | EntityExplosion_st0 | death | yes
st0 | EntityDraculaFireball | attack | no
st0 | EntityExplosion_st0 | attack | no
```

| Column | Meaning |
|---|---|
| `stage` | Stage it appeared in (the one used in `stage:name`) |
| `name` | Full function name (with `Entity`) |
| `group` | `candle`, `death`, `attack` or `none`. `(fire colours)`: only its fire-coloured pixels (rule 6) |
| `additive` | `yes` when the game draws it additively (glows, flames, magic) |

An object can be listed more than once: `EntityExplosion_st0` shows as `death`
when it is a broken candle's or a destroyed enemy's fire, and as `attack` when it
is an attack's.

The file only grows; delete it whenever you want to start over.

**Suggested workflow:** turn debug on, play the area, trigger the attack or
effect, take a screenshot showing its label, and look the name up in
`light-objects.txt` for the exact spelling and stage.

---

## 6. Adding or removing light sources

### Where

In `crtvision.json`, in the mod's folder inside the game
(`SymphonyRecomp/mods/crtvision/crtvision.json`).

**Edit it with the game closed.** The mod reads the file at start-up and
rewrites all of it whenever a setting is changed in the game, so edits made
while the game runs can be lost. Changes apply the next time the game starts.

### What it looks like

```json
"candleLight": {
  "enabled": true,
  "intensity": 1,
  "radius": 3.2,
  "room": 0.6,
  "reach": 24,
  "always": []
},
"deathFireLight": { "enabled": false, "intensity": 1, "radius": 3.2, "room": 0.6, "reach": 24, "always": [] },
"fireAttackLight": { "enabled": false, "intensity": 1, "radius": 3.2, "room": 0.6, "reach": 24, "always": [] },
"lightNever": []
```

- **`always`** (in each group): objects that always give light in that group,
  with all their bright pixels, any colour, animated or not. Use it for fire the
  mod does not recognise, or to move an object from one group to another.
- **`lightNever`** (one for all): objects that never give light. Wins over every
  `always` list and the built-in rules.
- `enabled`, `intensity`, `radius`, `room`, `reach` are the window's controls;
  change them there.

Each entry is a **fragment of the name**: the name only has to contain it. Case
does not matter. Use the name as shown on screen (`FlameAttack`) or in full as
the log shows it (`EntityFlameAttack`).

### One stage only: `stage:name`

`"no0:func_801B2F4C"` only applies in the marble gallery (`no0`). The stage is
the log's `stage` column.

**Always use it with `func_…` names**: an unnamed address is a different object
in each stage, and without the stage the rule would hit unrelated objects
elsewhere in the castle.

### Examples

A fire attack with a grey label and no brackets (`FlameAttack`): add it to the
attacks.

```json
"fireAttackLight": { "enabled": true, ..., "always": ["FlameAttack"] },
```

A death fire the mod classed as an attack (`[attack]`): add it to the death
group; a group's list wins over the automatic classification.

```json
"deathFireLight": { "enabled": true, ..., "always": ["no0:func_801B2F4C"] },
```

A room lamp or brazier that gives no light: add it to the candles.

```json
"candleLight": { "enabled": true, ..., "always": ["are:func_801C1234"] },
```

Something that glows and should not:

```json
"lightNever": ["PrizeDrop", "Splash"],
```

(In the examples `...` stands for the group's other values; do not type it.)

### JSON rules (so the file keeps working)

- Back the file up before editing (`crtvision.json.bak`).
- Each entry goes in double quotes `"…"`, separated by commas.
- Avoid a comma after the last entry (the mod accepts it, other tools do not).
- No `//` comments: the mod skips them and they are gone the next time it saves.
- If the JSON is broken, the mod cannot read it and starts with default
  settings (no lists, no preset). Changing a setting in the game in that state
  rewrites the file with those defaults — hence the backup. Check quotes and
  commas, or validate the file:

```bash
python3 -m json.tool SymphonyRecomp/mods/crtvision/crtvision.json
```

### Mind short fragments

`"Fire"` in `lightNever` would turn off **everything** with Fire in its name
(fireballs, FireWarg…). Use the most specific fragment that identifies the
object: `"CtulhuFireball"` rather than `"Fireball"`.

### An object gives light but it barely shows

The lists do not change intensity. A coloured label with weak light means the
object has few bright pixels. Raise its group's **Intensity** or **Room
lighting**; that affects the whole group, not just that object.

---

## 7. What to change and what to leave alone

### Meant to be changed

| Key | Purpose |
|---|---|
| `candleLight`, `deathFireLight`, `fireAttackLight` (`enabled`, `intensity`, `radius`, `room`, `reach`, `always`) | The three groups; the controls are easier in the in-game window |
| `lightNever` | Removing light sources |
| `debugLights` | Debug view |
| `frameGeneration`, `cropBands`, `integerScale`, `enabled` | Mod options; easier in the in-game window |

When the mod is reinstalled through the CRT-Vision launcher, your lists and
these settings are kept.

### Leave alone

| Key or file | Why |
|---|---|
| `preset`, `defaultPreset` | Preset paths; use the **Browse…** / **Default** buttons |
| `cropTop`, `cropBottom`, `cropLines`, `onlyFullscreen`, `menuHotZone` | Set by the CRT-Vision profile and overwritten on reinstall; wrong values break the black-band crop |
| `lightOffsetY` | Aligns object coordinates with the picture; calibrated at 0. Changing it shifts every light area |
| `params` | The default preset's parameters (the mask); use the **Mask strength** slider |
| The game's `mods/crtvision/source/*.cs` and `native/` | The installed copy: overwritten on reinstall, and a mistake stops the mod from compiling at start-up |

### C# only: the built-in rules

The fire words (`FireFunctions`), smoke (`NotFire`), candle detection
(`IsBreakable`) and where fire is born (`TrackBirths`, `BornFrom`) live in
`mod/source/CrtVision.cs` of this repository. Edit that copy, not the game's,
then build and install (see [Building](../README.md#building)).

For individual objects use the lists: no rebuild, and they survive mod updates.

---

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| No labels at all | Not in gameplay (menus, map, loading) |
| Labels flicker or are missing from screenshots | **Frame generation** is on: turn it off while debugging |
| A fire shows grey with `[…, off]` | Its group is off: turn it on in the window |
| A fire shows grey with no brackets | The mod does not recognise it: add it to the `always` list of the group you want |
| A fire lands in the wrong group | Add it to the right group's `always` list |
| Something that is not fire gives light | Add it to `lightNever` |
| A candle takes a moment to light up | Expected the first time a kind of candle shows up in a stage: the mod waits to see the flame animate so it is not mistaken for a vase. Later ones of that kind light up at once |
| An object added to a list does not change | The name does not match (check `light-objects.txt`), the JSON is broken, or the game was running while editing |
| My changes disappeared | The file was edited while the game was running and the mod rewrote it; edit with the game closed |

---

## 9. Reporting an object

To have an object handled by default for everyone, open an issue with:

1. A debug-view screenshot showing the effect's label.
2. Your `light-objects.txt`.
3. What should happen (which group it should light in, or none).

---

## 10. Worked example: `ExplosionPuffOpaque_no3 [death]`

A real case, in the castle entrance with Alucard: destroying a zombie leaves a
fireball labelled in orange **`ExplosionPuffOpaque_no3 [death]`**.

### Step 1: read the label

| Part | Meaning |
|---|---|
| `ExplosionPuffOpaque` | The object: the fireball enemies burst into. Its full name is `EntityExplosionPuffOpaque_no3` (the screen drops `Entity`) |
| `_no3` | The stage: the castle entrance (`no3`) |
| `[death]` + orange | Gives light in the **Death fire** group, because it was born where the zombie had just died |

In `light-objects.txt` it reads:

```
no3 | EntityExplosionPuffOpaque_no3 | death | no
```

Other labels in the same screenshot, for comparison:

- `Breakable_no3 [candle]` in yellow: the blue wall candle, candles group.
- `ZombieSpawner_no3`, `BackgroundBushes_no3`, `BackgroundLightning_no3` in grey
  with no brackets: not fire.
- `WeaponAttack_w0_043|DecelerateX_w0_053` in grey: the sword attack. When an
  address has several names the mod joins them with `|`; either one works in
  the lists (`WeaponAttack_w0_043`).

**Before editing:** close the game and back up `crtvision.json`.

### Step 2a: exclude it (never gives light)

In the castle entrance only, with the stage prefix:

```json
"lightNever": ["no3:ExplosionPuffOpaque"]
```

Everywhere (any stage), without it:

```json
"lightNever": ["ExplosionPuffOpaque"]
```

With other entries already there, separate them with commas:

```json
"lightNever": ["PrizeDrop", "no3:ExplosionPuffOpaque"]
```

Note: `"ExplosionPuffOpaque"` without a stage turns all of them off, including
the ones in attacks. To turn off only death fire (destroyed enemies and broken
candles), simply turn the **Death fire** group off in the mod's window.

### Step 2b: force it into a group

Useful when the mod does not classify it (grey, no brackets) or puts it in the
wrong group. Add it to the `always` list of the group you want. The whole group,
with the new entry in `always` (the other values are whatever you have):

```json
"deathFireLight": {
  "enabled": true,
  "intensity": 1,
  "radius": 3.2,
  "room": 0.6,
  "reach": 24,
  "always": ["no3:ExplosionPuffOpaque"]
},
```

- `enabled` must be `true`: with the group off, `always` does nothing.
- With `always` the object joins that group **every time**, wherever it is
  born: `"no3:ExplosionPuffOpaque"` in `deathFireLight` also puts that stage's
  attacks using that object in the death fire group. Use it for objects the mod
  does not recognise, with a stage prefix.
- If the same name is in `lightNever`, `lightNever` wins.

### Step 3: check it

1. Start the game and go to the castle entrance.
2. Turn the debug view on (`"debugLights": true`, section 3).
3. Destroy a zombie:
   - Excluded, the label is **grey with no brackets**: `ExplosionPuffOpaque_no3`.
   - Forced into `deathFireLight`, it is **orange** `[death]`.
4. If nothing changed, check the name in `light-objects.txt`, validate the
   JSON, and make sure the game was closed while editing.

---

## 11. An effect lights in the wrong group

Burning candles (the **Candles and torches** group) are recognised by
themselves: they are never mistaken for another object's fire. The fire a candle
**bursts into** when broken goes, like destroyed enemies' fire, to the **Death
fire** group. With only **Candles and torches** on, no death fire should give
light, neither enemies' nor broken candles'.

What can still be confused is **death fire** and **attacks**, because they are
often the same game object (`ExplosionPuffOpaque`, `Explosion`…) and the mod
tells them apart by where they are born:

| If it is born… | Group |
|---|---|
| Within 1.5 s of a lit candle breaking, at its height or above | Death fire |
| Within 1.5 s of an enemy dying, where it stood | Death fire |
| Otherwise | Attacks |

So:

- An enemy **shoots fire right where another one died**, or where you have just
  broken a candle: the attack counts as death fire.
- An enemy dies **away from where its body was** (one that explodes after being
  knocked back, say): its fire may count as an attack.

Outside those cases it should not happen:

- The game reuses an explosion's memory slot for the next one, and the mod
  tells when a new one is born (its position jumps, its animation starts over,
  or the slot was empty) to decide its group again.
- A candle only counts as broken when it vanishes inside the screen; one
  scrolling out, or whose flame pauses for a moment, does not.

If a fire lands in the wrong group outside those cases, that is a bug in the
mod: report it with the screenshot and `light-objects.txt` (section 9).

### Step 1: confirm what is happening

1. Turn the debug view on (section 3) and **Frame generation** off.
2. Repeat the situation until you see it, and take a screenshot.
3. Look at the colour and brackets of the unwanted effect's label:
   - **Orange `[death]`** or **red `[attack]`** with its group on: that group is
     on. If you do not want it, turn it off in the mod's window.
   - **Yellow `[candle]`** on something that is not a burning candle or torch:
     its name has `Candle`, `Torch` or `Lamp`, or it is the stage's breakable
     object and animates.
4. Note its name and stage (on screen or in `light-objects.txt`).

### Step 2: pick the fix

| What you want | Fix | Side effect |
|---|---|---|
| The effect **never gives light** | Add it to `lightNever` (with `stage:` to limit it to one stage) | It stops lighting in all three groups |
| The effect **belongs to one group only** | Add it to that group's `always` list | Every appearance goes to that group, wherever it is born |
| An attack **stops counting as death fire** | Add it to **`fireAttackLight`**'s `always` | If the same object is also that stage's death fire, that moves to attacks |
| A death fire **stops counting as an attack** | Add it to **`deathFireLight`**'s `always` | Attacks using that same object move to death fire |
| Something **that is not fire** stops giving light | Add it to `lightNever` | None |

The key: **a group's `always` list wins over the automatic classification**,
and it works even while that group is **off**. An object in
`deathFireLight.always` with death fire turned off never gives light, because
it is pinned to a group that is off.

### Example: an effect in Attacks only

A fire attack sometimes shows as `[death]` (it was born where an enemy died) and
you always want it as an attack:

```json
"fireAttackLight": {
  "enabled": true,
  "intensity": 1,
  "radius": 3.2,
  "room": 0.6,
  "reach": 24,
  "always": ["CtulhuFireball"]
},
```

### Example: something that is not fire shows as a candle

An ornament with `Lamp` in its name that does not burn shows yellow `[candle]`:

```json
"lightNever": ["top:LionLamp"]
```

### Rules of thumb

- **One name, one place.** In two groups' `always` lists, the first in this order
  wins: candles, death fire, attacks.
- **`lightNever` always wins**, over any `always`.
- **Use the stage prefix** (`no3:`) so fixing one area does not change the rest
  of the castle.
- **Test after each change** with debug on: break a candle, destroy an enemy and
  fire an attack nearby, and check each label lands in the group you want.
- **Undoing** is just removing the entry (or restoring the `crtvision.json`
  backup).


---

## 12. Defaults and restoring them

Only candles ship on: candles (0.95 / 3.4 px / 1.62 / 19 px), death fire (off;
0.51 / 2.6 px / 0.80 / 9 px) and attacks (off; 0.53 / 2.6 px / 0.85 / 10 px). The
`always` lists ship with:

- candles: `NP3_EntityBackgroundBlock` and `NO3_EntityBackgroundBlock` (the castle
  entrance's stone cup fire, in both of its stages)
  and `LightningThunder` (the bolt, only while it strikes: the object lasts
  about 12 frames per flash);
- death fire: `DeadMerman` (the blue or violet flames Mermen burn in when they
  die);
- attacks: `MediumWaterSplash` (the Merman's fire splash) and `UnkId24`
  (Hellfire's flames).

Remove them from the list if you do not want them.

**Restore all defaults** (next to **Default** in the mod's window) brings every
option and the default preset back to how the mod ships, the `always` and
`lightNever` lists included. The black-band crop and "fullscreen only", set by
the CRT-Vision profile, stay.
