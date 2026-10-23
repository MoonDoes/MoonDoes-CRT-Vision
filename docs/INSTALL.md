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
- **Emissive light**: candles, torches and magic light up their surroundings.
  Intensity, threshold and radius are adjustable. Being a 2D effect, some very
  bright colours that are not lights may glow a little too; raise the
  threshold if it bothers you.
- **Frame generation 60 → 120 (EXPERIMENTAL)**: off by default. Linux only.
  Requires **Variable Refresh Rate** (VRR / FreeSync / G-Sync) enabled in the
  system and the monitor — in GNOME: Settings → Displays → Variable Refresh
  Rate. May cause occasional flicker and adds about 8 ms of latency. The game
  speed does not change.

In gameplay, once the game's own black bands have stayed empty for half a
second, the picture fills the screen height; title and menus are shown whole
in 4:3.

Settings are saved in `crtvision.json`. To use another `.slangp` preset,
change `preset` (absolute path, or relative to the mod folder).

## Uninstall

Disable it in the Mods menu, or delete `mods/crtvision`.

Licenses and credits: `crtvision/LICENSES/`.
