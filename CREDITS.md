# Credits

MoonDoes CRT-Vision is glue and a few passes of its own around other people's
work. Thank you to everyone below.

## Shaders (the CRT look)

The MoonDoes_Lite_2 preset is a configuration of the **Mega Bezel** shader
chain. Every shader keeps its original header, copyright and license.

- **HyperspaceMadness** — *Mega Bezel Reflection Shader*: the framework the
  whole chain is built on (de-rez, screen scale and cache passes, CRT prep and
  SDR output, parameter system).
  https://github.com/HyperspaceMadness/Mega_Bezel ·
  https://forums.libretro.com/t/hsm-mega-bezel-reflection-shader-feedback-and-updates/25512
- **guest(r)** (guest.r) — *CRT-Guest-Advanced*: the CRT itself — scanlines,
  phosphor masks, bloom, halation, afterglow, interlace and linearize,
  deconvergence, gaussian passes; also the G-sharp resampler and
  sharpsmoother. With contributions and ideas from **Dr. Venom**.
- **Hyllian** — *Super-xBR* (2015), the edge-directed upscaler the chain
  uses to smooth the PlayStation's pixels before the CRT, and its custom
  bicubic passes (crt-super-xbr). Hyllian created the whole xBR family of
  pixel-art scalers and many libretro shaders, crt-hyllian among them.
  https://forums.libretro.com/t/xbr-algorithm-tutorial/123
- **Dogway (Jose Linares)** — *Grade*: colour grading, gamut and gamma.
- **cgwg**, **TroggleMonkey**, **Sp00kyFox** — functions and code credited in
  the shader headers.
- **libretro / RetroArch** — the `.slang` / `.slangp` shader format and the
  slang-shaders collection. https://github.com/libretro/slang-shaders
- **MoonDoes** — the MoonDoes_Lite preset configuration.

## Runtime and code

- **librashader** — Ronny Chan (chyyran) and contributors, SnowflakePowered.
  Runs the `.slangp` presets on OpenGL. MPL-2.0 or GPL-3.0-only.
  https://github.com/SnowflakePowered/librashader
- **glow** — grovesNL and contributors: OpenGL bindings for Rust.
  MIT/Apache-2.0. https://github.com/grovesNL/glow
- **vkSlang** — Ataraxsys: the Vulkan layer CRT-Vision Linux started from;
  its librashader integration and framing ideas shaped this mod.
  MPL-2.0. https://github.com/Ataraxsys/vkSlang
- **SymphonyRecomp / RecompOne** — BlackLabelHQ, flafmg and contributors: the
  recompilation, its C# mod loader and runtime. The bundled *Shaders* mod by
  flaffy (crt-geom) showed the way for post-processing mods.
  https://github.com/BlackLabelHQ/SymphonyRecomp
- **MonoMod** — the MonoMod team: runtime detours used through the recomp.
  https://github.com/MonoMod/MonoMod
- **CRT-Vision** — the Windows and Linux CRT-Vision projects this mod comes
  from.

## Game

*Castlevania: Symphony of the Night* is © Konami. Nothing from the game is
included here.
