# MoonDoes CRT-Vision — SymphonyRecomp mod (Linux x86_64)

Runs the MoonDoes CRT preset (RetroArch `.slang` shaders based on Mega Bezel,
through librashader OpenGL) directly on the native PlayStation picture inside
SymphonyRecomp, in fullscreen. No screen capture, no Vulkan layers, no extra
programs.

## Requirements

- Linux x86_64 with glibc 2.38 or newer (Ubuntu 24.04+, Fedora 39+, Arch…).
- SymphonyRecomp with mod support (RecompOne). Tested with v0.5.1b.
- A GPU with OpenGL 3.3 or newer.

## Install

1. Copy the `crtvision` folder into the game's `mods` folder (next to
   `blackout` and `shaders`): `SymphonyRecomp/mods/crtvision/`.
2. Start the game. The recomp compiles the mod at start-up.
3. Open the **Mods** menu and enable **MoonDoes CRT-Vision**.
4. Recommended: in the recomp's display settings set **Render scale to 1x**
   (original PlayStation output) and play in **fullscreen**. The CRT is only
   applied in fullscreen; in windowed mode the game looks as usual.

## Options

Mods → MoonDoes CRT-Vision → gear icon:

- **CRT enabled**, **Mask strength**, **Integer vertical scale** (exact steps:
  6× at 1440p, 4× at 1080p).
- **Crop the game's black bands**: on by default. In gameplay the picture
  fills the screen height once the game's own bands have stayed empty for half
  a second; turn it off to always see the whole picture.
- **Dynamic light**: the game's own fire glows and lights up the surfaces
  around it, in three groups turned on separately: **Candles and torches**
  (on by default, any flame colour), **Death fire** (destroyed enemies and
  broken candles, off by default) and **Fire attacks** (player and enemies,
  not magic, off by default). Each has its own **Intensity**, **Halo radius**, **Room
  lighting** and **Light reach**. Characters never glow by themselves. See
  [LIGHT-SOURCES.md](LIGHT-SOURCES.md) to add or remove light sources.
- **Frame generation 60 → 120 (EXPERIMENTAL)**: off by default. Linux only.
  Requires **Variable Refresh Rate** (VRR / FreeSync / G-Sync) enabled in the
  system and the monitor — in GNOME: Settings → Displays → Variable Refresh
  Rate. May cause occasional flicker and adds about 8 ms of latency. The game
  speed does not change.

- **Preset: Browse… / Default**: load any Mega Bezel or RetroArch `.slang`
  preset from a browser inside the game, exactly as it is configured (Mega
  Bezel presets with bezels get the whole window). Default goes back to
  MoonDoes_Lite_2. Only `.slang` presets work; a large preset takes a few
  seconds to compile the first time.

Title and menus are always shown whole in 4:3.

Settings are saved in `crtvision.json`. To use another `.slangp` preset,
change `preset` (absolute path, or relative to the mod folder).

## Uninstall

Disable it in the Mods menu, or delete `mods/crtvision`.

Licenses and credits: `crtvision/LICENSES/`.
