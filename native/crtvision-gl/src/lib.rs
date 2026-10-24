//! librashader's OpenGL runtime behind a small C API.
//!
//! Loaded by the CRT-Vision mod inside SymphonyRecomp, which calls it on the
//! game's own thread with its OpenGL context current. The preset reads the
//! recomp's native PSX picture (256/320/512 x 240...) directly: no capture, no
//! resampling of an upscaled image, no Vulkan translation.
//!
//! Every call saves and restores the OpenGL state it touches, so the recomp's
//! renderer and ImGui find the context as they left it.

use std::ffi::{c_char, c_void, CStr};
use std::num::NonZeroU32;
use std::path::Path;
use std::sync::Arc;

use librashader::presets::context::VideoDriver;
use librashader::presets::{ShaderFeatures, ShaderPreset};
use librashader::runtime::gl::{FilterChain, FilterChainOptions, FrameOptions, GLImage};
use librashader::runtime::{FilterChainParameters, Size, Viewport};

use glow::HasContext;

const RTLD_NOW: i32 = 2;
extern "C" {
    fn dlopen(filename: *const c_char, flag: i32) -> *mut c_void;
    fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
}

/// Frames that must agree before the crop changes on a new screen (a new
/// picture width), so a flash or a single odd frame does not make it jump.
const AGREE_FRAMES: u32 = 3;
/// Frames the game's bands must stay black before the crop is applied: the
/// picture grows only once the screen has settled (half a second).
const CROP_FRAMES: u32 = 30;
/// Frames the game's bands must hold picture before an established crop is
/// dropped while the screen keeps its width: a screen shake or an effect that
/// touches the bands for a moment must not make the picture jump in size.
const UNCROP_FRAMES: u32 = 45;
/// Lit pixels in the bands below this count are stray (a particle).
const BAND_NOISE: usize = 16;
/// A channel above this is picture; the game's own bands are 0.
const BLACK: u8 = 6;

/// Resolves OpenGL entry points through GLX, or EGL when GLX is absent.
unsafe fn gl_loader() -> Option<impl Fn(&CStr) -> *const c_void> {
    type GetProc = unsafe extern "C" fn(*const c_char) -> *const c_void;
    let candidates: [(&CStr, &CStr); 2] =
        [(c"libGL.so.1", c"glXGetProcAddressARB"), (c"libEGL.so.1", c"eglGetProcAddress")];
    for (lib, sym) in candidates {
        let handle = dlopen(lib.as_ptr(), RTLD_NOW);
        if handle.is_null() {
            continue;
        }
        let f = dlsym(handle, sym.as_ptr());
        if !f.is_null() {
            let get: GetProc = std::mem::transmute(f);
            return Some(move |name: &CStr| get(name.as_ptr()));
        }
    }
    None
}

/// The OpenGL state the chain and the helpers below may change.
struct SavedState {
    program: i32,
    vao: i32,
    array_buffer: i32,
    draw_fbo: i32,
    read_fbo: i32,
    pack_buffer: i32,
    unpack_buffer: i32,
    active_texture: i32,
    texture_2d: i32,
    sampler: i32,
    viewport: [i32; 4],
    scissor_box: [i32; 4],
    enables: [(u32, bool); 6],
    pack_alignment: i32,
    unpack_alignment: i32,
    blend_src_rgb: i32,
    blend_dst_rgb: i32,
    blend_src_alpha: i32,
    blend_dst_alpha: i32,
    blend_eq_rgb: i32,
    blend_eq_alpha: i32,
}

impl SavedState {
    unsafe fn save(gl: &glow::Context) -> SavedState {
        let mut viewport = [0; 4];
        gl.get_parameter_i32_slice(glow::VIEWPORT, &mut viewport);
        let mut scissor_box = [0; 4];
        gl.get_parameter_i32_slice(glow::SCISSOR_BOX, &mut scissor_box);
        let active_texture = gl.get_parameter_i32(glow::ACTIVE_TEXTURE);
        gl.active_texture(glow::TEXTURE0);
        let texture_2d = gl.get_parameter_i32(glow::TEXTURE_BINDING_2D);
        let sampler = gl.get_parameter_i32(glow::SAMPLER_BINDING);
        gl.active_texture(active_texture as u32);
        let enables = [glow::BLEND, glow::SCISSOR_TEST, glow::DEPTH_TEST, glow::CULL_FACE, glow::STENCIL_TEST, glow::FRAMEBUFFER_SRGB]
            .map(|cap| (cap, gl.is_enabled(cap)));
        SavedState {
            program: gl.get_parameter_i32(glow::CURRENT_PROGRAM),
            vao: gl.get_parameter_i32(glow::VERTEX_ARRAY_BINDING),
            array_buffer: gl.get_parameter_i32(glow::ARRAY_BUFFER_BINDING),
            draw_fbo: gl.get_parameter_i32(glow::DRAW_FRAMEBUFFER_BINDING),
            read_fbo: gl.get_parameter_i32(glow::READ_FRAMEBUFFER_BINDING),
            pack_buffer: gl.get_parameter_i32(glow::PIXEL_PACK_BUFFER_BINDING),
            unpack_buffer: gl.get_parameter_i32(glow::PIXEL_UNPACK_BUFFER_BINDING),
            active_texture,
            texture_2d,
            sampler,
            viewport,
            scissor_box,
            enables,
            pack_alignment: gl.get_parameter_i32(glow::PACK_ALIGNMENT),
            unpack_alignment: gl.get_parameter_i32(glow::UNPACK_ALIGNMENT),
            blend_src_rgb: gl.get_parameter_i32(glow::BLEND_SRC_RGB),
            blend_dst_rgb: gl.get_parameter_i32(glow::BLEND_DST_RGB),
            blend_src_alpha: gl.get_parameter_i32(glow::BLEND_SRC_ALPHA),
            blend_dst_alpha: gl.get_parameter_i32(glow::BLEND_DST_ALPHA),
            blend_eq_rgb: gl.get_parameter_i32(glow::BLEND_EQUATION_RGB),
            blend_eq_alpha: gl.get_parameter_i32(glow::BLEND_EQUATION_ALPHA),
        }
    }

    unsafe fn restore(&self, gl: &glow::Context) {
        let program = NonZeroU32::new(self.program as u32).map(glow::NativeProgram);
        gl.use_program(program);
        gl.bind_vertex_array(NonZeroU32::new(self.vao as u32).map(glow::NativeVertexArray));
        gl.bind_buffer(glow::ARRAY_BUFFER, NonZeroU32::new(self.array_buffer as u32).map(glow::NativeBuffer));
        gl.bind_buffer(glow::PIXEL_PACK_BUFFER, NonZeroU32::new(self.pack_buffer as u32).map(glow::NativeBuffer));
        gl.bind_buffer(glow::PIXEL_UNPACK_BUFFER, NonZeroU32::new(self.unpack_buffer as u32).map(glow::NativeBuffer));
        gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, NonZeroU32::new(self.draw_fbo as u32).map(glow::NativeFramebuffer));
        gl.bind_framebuffer(glow::READ_FRAMEBUFFER, NonZeroU32::new(self.read_fbo as u32).map(glow::NativeFramebuffer));
        gl.active_texture(glow::TEXTURE0);
        gl.bind_texture(glow::TEXTURE_2D, NonZeroU32::new(self.texture_2d as u32).map(glow::NativeTexture));
        gl.bind_sampler(0, NonZeroU32::new(self.sampler as u32).map(glow::NativeSampler));
        gl.active_texture(self.active_texture as u32);
        let [x, y, w, h] = self.viewport;
        gl.viewport(x, y, w, h);
        let [x, y, w, h] = self.scissor_box;
        gl.scissor(x, y, w, h);
        for (cap, on) in self.enables {
            if on {
                gl.enable(cap)
            } else {
                gl.disable(cap)
            }
        }
        gl.pixel_store_i32(glow::PACK_ALIGNMENT, self.pack_alignment);
        gl.pixel_store_i32(glow::UNPACK_ALIGNMENT, self.unpack_alignment);
        gl.blend_func_separate(
            self.blend_src_rgb as u32,
            self.blend_dst_rgb as u32,
            self.blend_src_alpha as u32,
            self.blend_dst_alpha as u32,
        );
        gl.blend_equation_separate(self.blend_eq_rgb as u32, self.blend_eq_alpha as u32);
    }
}

/// Asynchronous read of the native picture, one frame late.
struct Readback {
    pbo: [glow::Buffer; 2],
    fence: [Option<glow::Fence>; 2],
    size: [(u32, u32); 2],
    next: usize,
}

pub struct Context {
    gl: Arc<glow::Context>,
    chain: FilterChain,
    read_fbo: glow::Framebuffer,
    draw_fbo: glow::Framebuffer,
    /// The picture of this frame and of the previous one (ping-pong).
    staging: [Option<(glow::Texture, u32, u32)>; 2],
    current: usize,
    /// The other staging texture holds the previous frame at the same size.
    prev_ok: bool,
    output: Option<(glow::Texture, u32, u32)>,
    /// The preset's output for the frame generated between two real ones.
    mid_output: Option<(glow::Texture, u32, u32)>,
    interp: Option<Interp>,
    /// Emissive light, per group of light sources.
    light_groups: [LightGroup; LIGHT_GROUPS],
    /// Light sources from the game's objects: rectangles (x0, y0, x1, y1) in
    /// the full native picture, and their group.
    light_rects: Vec<([f32; 4], i32, i32)>,
    /// The HUD's gauges (x0, y0, x1, y1 in the full native picture): what is
    /// drawn there only passes for a flame with the flame's own hue.
    light_hud: Vec<[f32; 4]>,
    /// Lines cropped at the top of the picture being lit.
    crop_top: u32,
    glow: Option<Glow>,
    readback: Readback,
    /// Crop in use: lines removed at the top and bottom.
    crop: (u32, u32),
    streak: u32,
    /// Width of the last picture analysed: a new width is a new screen.
    crop_width: u32,
    frame: usize,
    /// The preset places the screen itself (Mega Bezel with its own aspect
    /// handling: bezels, reflections) and wants the whole window.
    fullscreen: bool,
}

fn new_texture(gl: &glow::Context, w: u32, h: u32) -> Option<glow::Texture> {
    unsafe {
        let tex = gl.create_texture().ok()?;
        gl.bind_texture(glow::TEXTURE_2D, Some(tex));
        gl.tex_storage_2d(glow::TEXTURE_2D, 1, glow::RGBA8, w as i32, h as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MIN_FILTER, glow::NEAREST as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MAG_FILTER, glow::NEAREST as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_S, glow::CLAMP_TO_EDGE as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_WRAP_T, glow::CLAMP_TO_EDGE as i32);
        Some(tex)
    }
}

/// Keeps `slot` a `w`x`h` texture, recreating it when the size changes.
fn ensure(gl: &glow::Context, slot: &mut Option<(glow::Texture, u32, u32)>, w: u32, h: u32) -> Option<glow::Texture> {
    if let Some((tex, tw, th)) = *slot {
        if (tw, th) == (w, h) {
            return Some(tex);
        }
        unsafe { gl.delete_texture(tex) };
        *slot = None;
    }
    let tex = new_texture(gl, w, h)?;
    *slot = Some((tex, w, h));
    Some(tex)
}

fn native_texture(id: u32) -> Option<glow::Texture> {
    NonZeroU32::new(id).map(glow::NativeTexture)
}

// ---------------------------------------------------------------- frame generation

/// Block size and search radius of the motion estimation, in native pixels.
const BLOCK: u32 = 8;

const FULLSCREEN_VS: &str = r#"#version 330 core
void main() {
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
"#;

/// One fragment per 8x8 block of the current picture: the displacement
/// into the previous one with the smallest difference (sum over the block),
/// small motions preferred on ties so flat areas stay still.
const MOTION_FS: &str = r#"#version 330 core
uniform sampler2D cur;
uniform sampler2D prev;
uniform ivec2 size;
out vec4 o;
const int B = 8;
const int R = 8;
float diff(vec3 a, vec3 b) { vec3 d = abs(a - b); return d.r + d.g + d.b; }
void main() {
    ivec2 base = ivec2(gl_FragCoord.xy) * B;
    vec3 block[64];
    for (int y = 0; y < B; y++)
        for (int x = 0; x < B; x++)
            block[y * B + x] = texelFetch(cur, clamp(base + ivec2(x, y), ivec2(0), size - 1), 0).rgb;
    float best = 1e9;
    ivec2 bestD = ivec2(0);
    for (int dy = -R; dy <= R; dy++) {
        for (int dx = -R; dx <= R; dx++) {
            float sad = 0.004 * float(abs(dx) + abs(dy));
            for (int y = 0; y < B; y++)
                for (int x = 0; x < B; x++)
                    sad += diff(block[y * B + x], texelFetch(prev, clamp(base + ivec2(x + dx, y + dy), ivec2(0), size - 1), 0).rgb);
            if (sad < best) { best = sad; bestD = ivec2(dx, dy); }
        }
    }
    o = vec4(vec2(bestD), best, 1.0);
}
"#;

/// The picture halfway between the previous frame and this one. Each pixel
/// tries the vectors of its block, its neighbours and no motion, samples both
/// frames half way along it and keeps the pair that agrees best. Integer
/// steps only: pixel art is never resampled between pixels.
const INTERP_FS: &str = r#"#version 330 core
uniform sampler2D cur;
uniform sampler2D prev;
uniform sampler2D motion;
uniform ivec2 size;
uniform ivec2 blocks;
out vec4 o;
float diff(vec3 a, vec3 b) { vec3 d = abs(a - b); return d.r + d.g + d.b; }
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    ivec2 b = p / 8;
    float bestCost = 1e9;
    vec3 best = texelFetch(cur, p, 0).rgb;
    for (int k = 0; k < 10; k++) {
        ivec2 d = ivec2(0);
        if (k < 9) {
            ivec2 bb = clamp(b + ivec2(k % 3 - 1, k / 3 - 1), ivec2(0), blocks - 1);
            d = ivec2(texelFetch(motion, bb, 0).xy);
        }
        ivec2 half1 = d / 2;
        ivec2 half0 = d - half1;
        vec3 a = texelFetch(prev, clamp(p + half0, ivec2(0), size - 1), 0).rgb;
        vec3 c = texelFetch(cur, clamp(p - half1, ivec2(0), size - 1), 0).rgb;
        float cost = diff(a, c) + 0.002 * float(abs(d.x) + abs(d.y));
        if (cost < bestCost) { bestCost = cost; best = (a + c) * 0.5; }
    }
    // Nothing agrees (something appears, or a cut): the newer frame.
    if (bestCost > 0.5) best = texelFetch(cur, p, 0).rgb;
    o = vec4(best, 1.0);
}
"#;

struct Interp {
    vao: glow::VertexArray,
    fbo: glow::Framebuffer,
    motion_prog: glow::Program,
    interp_prog: glow::Program,
    motion: Option<(glow::Texture, u32, u32)>,
    mid: Option<(glow::Texture, u32, u32)>,
}

unsafe fn program(gl: &glow::Context, fs: &str) -> Result<glow::Program, String> {
    let prog = gl.create_program()?;
    for (kind, src) in [(glow::VERTEX_SHADER, FULLSCREEN_VS), (glow::FRAGMENT_SHADER, fs)] {
        let sh = gl.create_shader(kind)?;
        gl.shader_source(sh, src);
        gl.compile_shader(sh);
        if !gl.get_shader_compile_status(sh) {
            return Err(gl.get_shader_info_log(sh));
        }
        gl.attach_shader(prog, sh);
        gl.delete_shader(sh);
    }
    gl.link_program(prog);
    if !gl.get_program_link_status(prog) {
        return Err(gl.get_program_info_log(prog));
    }
    Ok(prog)
}

impl Interp {
    unsafe fn new(gl: &glow::Context) -> Result<Interp, String> {
        Ok(Interp {
            vao: gl.create_vertex_array()?,
            fbo: gl.create_framebuffer()?,
            motion_prog: program(gl, MOTION_FS)?,
            interp_prog: program(gl, INTERP_FS)?,
            motion: None,
            mid: None,
        })
    }

    unsafe fn destroy(&self, gl: &glow::Context) {
        gl.delete_vertex_array(self.vao);
        gl.delete_framebuffer(self.fbo);
        gl.delete_program(self.motion_prog);
        gl.delete_program(self.interp_prog);
        for tex in [self.motion, self.mid].into_iter().flatten() {
            gl.delete_texture(tex.0);
        }
    }

    unsafe fn float_texture(gl: &glow::Context, slot: &mut Option<(glow::Texture, u32, u32)>, w: u32, h: u32) -> Option<glow::Texture> {
        if let Some((tex, tw, th)) = *slot {
            if (tw, th) == (w, h) {
                return Some(tex);
            }
            gl.delete_texture(tex);
        }
        let tex = gl.create_texture().ok()?;
        gl.bind_texture(glow::TEXTURE_2D, Some(tex));
        gl.tex_storage_2d(glow::TEXTURE_2D, 1, glow::RGBA32F, w as i32, h as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MIN_FILTER, glow::NEAREST as i32);
        gl.tex_parameter_i32(glow::TEXTURE_2D, glow::TEXTURE_MAG_FILTER, glow::NEAREST as i32);
        *slot = Some((tex, w, h));
        Some(tex)
    }

    /// Draws a full-screen pass of `prog` into `target` (`w`x`h`).
    unsafe fn pass(&self, gl: &glow::Context, prog: glow::Program, target: glow::Texture, w: u32, h: u32, inputs: &[(&str, glow::Texture)], ints: &[(&str, [i32; 2])]) {
        gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, Some(self.fbo));
        gl.framebuffer_texture_2d(glow::DRAW_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(target), 0);
        gl.viewport(0, 0, w as i32, h as i32);
        gl.use_program(Some(prog));
        for (unit, (name, tex)) in inputs.iter().enumerate() {
            gl.active_texture(glow::TEXTURE0 + unit as u32);
            gl.bind_texture(glow::TEXTURE_2D, Some(*tex));
            gl.bind_sampler(unit as u32, None);
            gl.uniform_1_i32(gl.get_uniform_location(prog, name).as_ref(), unit as i32);
        }
        for (name, [x, y]) in ints {
            gl.uniform_2_i32(gl.get_uniform_location(prog, name).as_ref(), *x, *y);
        }
        gl.bind_vertex_array(Some(self.vao));
        gl.draw_arrays(glow::TRIANGLES, 0, 3);
    }

    /// The native picture halfway between `prev` and `cur` (`w`x`h`).
    unsafe fn generate(&mut self, gl: &glow::Context, prev: glow::Texture, cur: glow::Texture, w: u32, h: u32) -> Option<glow::Texture> {
        let (bw, bh) = (w.div_ceil(BLOCK), h.div_ceil(BLOCK));
        let motion = Self::float_texture(gl, &mut self.motion, bw, bh)?;
        let mid = ensure(gl, &mut self.mid, w, h)?;
        gl.disable(glow::BLEND);
        gl.disable(glow::SCISSOR_TEST);
        gl.disable(glow::DEPTH_TEST);
        gl.disable(glow::CULL_FACE);
        gl.disable(glow::FRAMEBUFFER_SRGB);
        let size = [w as i32, h as i32];
        self.pass(gl, self.motion_prog, motion, bw, bh, &[("cur", cur), ("prev", prev)], &[("size", size)]);
        self.pass(
            gl,
            self.interp_prog,
            mid,
            w,
            h,
            &[("cur", cur), ("prev", prev), ("motion", motion)],
            &[("size", size), ("blocks", [bw as i32, bh as i32])],
        );
        Some(mid)
    }
}

// ---------------------------------------------------------------- emissive light

/// The light is worked out on a canvas `LIGHT_MARGIN` pixels larger than the
/// picture on every side, so sources off screen light the picture as they
/// would from there. The part of a source outside the picture shines with the
/// colour remembered for its kind (`rectKey`, learnt while one was in view) or,
/// for sources without one, with the mean light of its visible part scaled by
/// how much of it is still visible, so it fades out as it leaves.
const LIGHT_MARGIN: i32 = 128;
/// Kinds of light source whose colour is remembered.
const LIGHT_KEYS: u32 = 256;

/// One texel per light source: the mean light of its visible pixels, and the
/// fraction of it that is inside the picture.
const LIGHT_MEANS_FS: &str = r#"#version 330 core
uniform sampler2D src;
uniform ivec2 size;
// Light sources: rectangles (x0, y0, x1, y1 in the picture's pixels) and the
// group each belongs to. A rectangle given right to left (x0 > x1) only emits
// fire-coloured pixels.
uniform int rectCount;
uniform vec4 rects[64];
uniform int rectGroup[64];
// Each source's remembered colour (index into `colours`), or -1.
uniform int rectKey[64];
uniform float threshold;
// The HUD's gauges: drawn over flames, they must not pass for one.
uniform int hudCount;
uniform vec4 hud[8];
bool onHud(ivec2 q) {
    for (int k = 0; k < 8; k++) {
        if (k >= hudCount) break;
        vec4 h = hud[k];
        if (float(q.x) >= h.x && float(q.x) < h.z && float(q.y) >= h.y && float(q.y) < h.w) return true;
    }
    return false;
}
// Bright pixels give off light, with a soft knee; coloured light (fire) is
// favoured over white and grey.
vec3 emitted(vec3 c) {
    float m = max(c.r, max(c.g, c.b));
    float sat = m > 0.0 ? (m - min(c.r, min(c.g, c.b))) / m : 0.0;
    return c * smoothstep(threshold - 0.08, threshold + 0.08, m) * mix(0.25, 1.0, smoothstep(0.15, 0.5, sat));
}
// Fire is red, orange or yellow: red over blue, green no higher than red.
float fiery(vec3 c) {
    return smoothstep(0.1, 0.3, c.r - c.b) * smoothstep(-0.1, 0.05, c.r - c.g);
}
vec3 emittedBy(vec4 r, vec3 c) {
    return emitted(c) * (r.x > r.z ? fiery(c) : 1.0);
}
out vec4 o;
void main() {
    int k = int(gl_FragCoord.x);
    if (k >= rectCount) { o = vec4(0.0); return; }
    vec4 r = rects[k];
    ivec2 a = ivec2(floor(vec2(min(r.x, r.z), r.y))), b = ivec2(ceil(vec2(max(r.x, r.z), r.w)));
    ivec2 va = clamp(a, ivec2(0), size), vb = clamp(b, ivec2(0), size);
    float area = float(max(b.x - a.x, 1) * max(b.y - a.y, 1));
    ivec2 seen = max(vb - va, ivec2(0));
    if (seen.x == 0 || seen.y == 0) { o = vec4(0.0); return; }
    ivec2 stride = max((seen + 47) / 48, ivec2(1));
    vec3 sum = vec3(0.0);
    float count = 0.0;
    for (int y = va.y; y < vb.y; y += stride.y)
        for (int x = va.x; x < vb.x; x += stride.x) {
            if (onHud(ivec2(x, y))) continue;
            sum += emittedBy(r, texelFetch(src, ivec2(x, y), 0).rgb);
            count += 1.0;
        }
    o = vec4(sum / max(count, 1.0), float(seen.x * seen.y) / area);
}
"#;

/// The remembered colours, one texel per kind: the mean light of a source of
/// that kind that is mostly in view this frame, or what was remembered.
const LIGHT_COLOURS_FS: &str = r#"#version 330 core
uniform sampler2D means;
uniform sampler2D previous;
uniform int rectCount;
uniform int rectKey[64];
out vec4 o;
void main() {
    int key = int(gl_FragCoord.x);
    vec4 colour = texelFetch(previous, ivec2(key, 0), 0);
    for (int k = 0; k < 64; k++) {
        if (k >= rectCount) break;
        if (rectKey[k] != key) continue;
        vec4 m = texelFetch(means, ivec2(k, 0), 0);
        if (m.a >= 0.75 && max(m.r, max(m.g, m.b)) > 0.001) colour = vec4(m.rgb, 1.0);
    }
    o = colour;
}
"#;

/// The light a group's sources give off, on the canvas with its margin.
const LIGHT_EXTRACT_FS: &str = r#"#version 330 core
uniform sampler2D src;
uniform sampler2D means;
uniform sampler2D colours;
uniform ivec2 size;
uniform int margin;
uniform int group;
// Light sources: rectangles (x0, y0, x1, y1 in the picture's pixels) and the
// group each belongs to. A rectangle given right to left (x0 > x1) only emits
// fire-coloured pixels.
uniform int rectCount;
uniform vec4 rects[64];
uniform int rectGroup[64];
// Each source's remembered colour (index into `colours`), or -1.
uniform int rectKey[64];
uniform float threshold;
// The HUD's gauges: drawn over flames, they must not pass for one.
uniform int hudCount;
uniform vec4 hud[8];
bool onHud(ivec2 q) {
    for (int k = 0; k < 8; k++) {
        if (k >= hudCount) break;
        vec4 h = hud[k];
        if (float(q.x) >= h.x && float(q.x) < h.z && float(q.y) >= h.y && float(q.y) < h.w) return true;
    }
    return false;
}
// Bright pixels give off light, with a soft knee; coloured light (fire) is
// favoured over white and grey.
vec3 emitted(vec3 c) {
    float m = max(c.r, max(c.g, c.b));
    float sat = m > 0.0 ? (m - min(c.r, min(c.g, c.b))) / m : 0.0;
    return c * smoothstep(threshold - 0.08, threshold + 0.08, m) * mix(0.25, 1.0, smoothstep(0.15, 0.5, sat));
}
// Fire is red, orange or yellow: red over blue, green no higher than red.
float fiery(vec3 c) {
    return smoothstep(0.1, 0.3, c.r - c.b) * smoothstep(-0.1, 0.05, c.r - c.g);
}
vec3 emittedBy(vec4 r, vec3 c) {
    return emitted(c) * (r.x > r.z ? fiery(c) : 1.0);
}
// How much a pixel has a candle's flame hue (`flame`: its remembered colour).
float flameHue(vec3 c, vec3 flame) {
    float mc = max(c.r, max(c.g, c.b)), mf = max(flame.r, max(flame.g, flame.b));
    if (mc <= 0.0 || mf <= 0.0) return 0.0;
    return 1.0 - smoothstep(0.2, 0.35, distance(c / mc, flame / mf));
}
out vec4 o;
void main() {
    ivec2 q = ivec2(gl_FragCoord.xy) - ivec2(margin);
    bool inside = all(greaterThanEqual(q, ivec2(0))) && all(lessThan(q, size));
    vec3 c = inside ? texelFetch(src, q, 0).rgb : vec3(0.0);
    vec3 light = vec3(0.0);
    for (int k = 0; k < 64; k++) {
        if (k >= rectCount) break;
        if (rectGroup[k] != group) continue;
        vec4 r = rects[k];
        if (float(q.x) < min(r.x, r.z) || float(q.x) >= max(r.x, r.z) || float(q.y) < r.y || float(q.y) >= r.w) continue;
        vec4 m = rectKey[k] >= 0 ? texelFetch(colours, ivec2(rectKey[k], 0), 0) : texelFetch(means, ivec2(k, 0), 0);
        // On a life gauge drawn over a flame only the flame's own hue emits:
        // the gauge takes light but does not become a light source.
        float flame = !onHud(q) ? 1.0 : rectKey[k] >= 0 && m.a > 0.0 ? flameHue(c, m.rgb) : 0.0;
        light = max(light, inside ? emittedBy(r, c) * flame : m.rgb * m.a);
    }
    o = vec4(light, 1.0);
}
"#;

/// Separable gaussian. Beyond the canvas there is no light: nothing at its
/// edge is counted twice.
const LIGHT_BLUR_FS: &str = r#"#version 330 core
uniform sampler2D src;
uniform ivec2 size;
uniform ivec2 dir;
uniform float sigma;
out vec4 o;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    int r = int(ceil(sigma * 3.0));
    vec3 acc = vec3(0.0);
    float total = 0.0;
    for (int i = -128; i <= 128; i++) {
        if (i < -r || i > r) continue;
        float w = exp(-float(i * i) / (2.0 * sigma * sigma));
        ivec2 q = p + dir * i;
        if (all(greaterThanEqual(q, ivec2(0))) && all(lessThan(q, size))) acc += texelFetch(src, q, 0).rgb * w;
        total += w;
    }
    o = vec4(acc / total, 1.0);
}
"#;

/// Each group's halo is screen-blended (it brightens what is around a source
/// without burning out what is already bright); each group's room light is
/// multiplied with the surfaces it reaches, so they take its colour like under
/// a real lamp.
const GLOW_COMPOSITE_FS: &str = r#"#version 330 core
uniform sampler2D base;
uniform sampler2D halo0;
uniform sampler2D halo1;
uniform sampler2D halo2;
uniform sampler2D room0;
uniform sampler2D room1;
uniform sampler2D room2;
uniform vec3 haloGain;
uniform vec3 roomGain;
// The light textures have a margin around the picture.
uniform int margin;
out vec4 o;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    vec3 b = texelFetch(base, p, 0).rgb;
    p += ivec2(margin);
    vec3 r = texelFetch(room0, p, 0).rgb * roomGain.x + texelFetch(room1, p, 0).rgb * roomGain.y + texelFetch(room2, p, 0).rgb * roomGain.z;
    b = min(b + b * min(r, vec3(1.5)), vec3(1.0));
    vec3 g = texelFetch(halo0, p, 0).rgb * haloGain.x + texelFetch(halo1, p, 0).rgb * haloGain.y + texelFetch(halo2, p, 0).rgb * haloGain.z;
    g = clamp(g, 0.0, 1.0);
    o = vec4(1.0 - (1.0 - b) * (1.0 - g), 1.0);
}
"#;

/// Brightness from which a pixel inside a light source gives off light: the
/// objects already say what burns, so the whole flame shines.
const LIGHT_THRESHOLD: f32 = 0.6;

/// One group of light sources (candles, enemies' death fire, fire attacks)
/// and how it lights: `intensity` 0..2 (1 natural) scales its halo and room
/// light, `radius` is the halo's size, `room` how much it lights the
/// surroundings and `reach` how far, both radius and reach in native pixels.
#[derive(Clone, Copy, Default)]
struct LightGroup {
    enabled: bool,
    intensity: f32,
    radius: f32,
    room: f32,
    reach: f32,
}

const LIGHT_GROUPS: usize = 3;

struct Glow {
    vao: glow::VertexArray,
    fbo: glow::Framebuffer,
    means_prog: glow::Program,
    colours_prog: glow::Program,
    extract_prog: glow::Program,
    blur_prog: glow::Program,
    composite_prog: glow::Program,
    /// The sources' mean light (64 x 1).
    means: Option<(glow::Texture, u32, u32)>,
    /// Remembered colours by kind (LIGHT_KEYS x 1), this frame's and the last.
    colours: [Option<(glow::Texture, u32, u32)>; 2],
    /// With the margin: a group's light, a blur's first pass, then each
    /// group's halo and room light.
    temp: [Option<(glow::Texture, u32, u32)>; 2 + 2 * LIGHT_GROUPS],
    out: Option<(glow::Texture, u32, u32)>,
}

enum Uniform<'a> {
    Tex(&'a str, glow::Texture),
    Int2(&'a str, [i32; 2]),
    Int(&'a str, i32),
    Float(&'a str, f32),
    Vec4s(&'a str, &'a [f32]),
    Ints(&'a str, &'a [i32]),
    Vec3(&'a str, [f32; 3]),
}

/// A full-screen pass of `prog` into `target` (`w`x`h`).
unsafe fn draw_pass(gl: &glow::Context, vao: glow::VertexArray, fbo: glow::Framebuffer, prog: glow::Program, target: glow::Texture, w: u32, h: u32, uniforms: &[Uniform]) {
    gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, Some(fbo));
    gl.framebuffer_texture_2d(glow::DRAW_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(target), 0);
    gl.viewport(0, 0, w as i32, h as i32);
    gl.use_program(Some(prog));
    let mut unit = 0;
    for u in uniforms {
        match u {
            Uniform::Tex(name, tex) => {
                gl.active_texture(glow::TEXTURE0 + unit);
                gl.bind_texture(glow::TEXTURE_2D, Some(*tex));
                gl.bind_sampler(unit, None);
                gl.uniform_1_i32(gl.get_uniform_location(prog, name).as_ref(), unit as i32);
                unit += 1;
            }
            Uniform::Int2(name, [x, y]) => gl.uniform_2_i32(gl.get_uniform_location(prog, name).as_ref(), *x, *y),
            Uniform::Int(name, v) => gl.uniform_1_i32(gl.get_uniform_location(prog, name).as_ref(), *v),
            Uniform::Float(name, v) => gl.uniform_1_f32(gl.get_uniform_location(prog, name).as_ref(), *v),
            Uniform::Vec4s(name, v) => {
                if !v.is_empty() {
                    gl.uniform_4_f32_slice(gl.get_uniform_location(prog, name).as_ref(), v)
                }
            }
            Uniform::Ints(name, v) => {
                if !v.is_empty() {
                    gl.uniform_1_i32_slice(gl.get_uniform_location(prog, name).as_ref(), v)
                }
            }
            Uniform::Vec3(name, [x, y, z]) => gl.uniform_3_f32(gl.get_uniform_location(prog, name).as_ref(), *x, *y, *z),
        }
    }
    gl.bind_vertex_array(Some(vao));
    gl.draw_arrays(glow::TRIANGLES, 0, 3);
}

impl Glow {
    unsafe fn new(gl: &glow::Context) -> Result<Glow, String> {
        Ok(Glow {
            vao: gl.create_vertex_array()?,
            fbo: gl.create_framebuffer()?,
            means_prog: program(gl, LIGHT_MEANS_FS)?,
            colours_prog: program(gl, LIGHT_COLOURS_FS)?,
            extract_prog: program(gl, LIGHT_EXTRACT_FS)?,
            blur_prog: program(gl, LIGHT_BLUR_FS)?,
            composite_prog: program(gl, GLOW_COMPOSITE_FS)?,
            means: None,
            colours: [None, None],
            temp: Default::default(),
            out: None,
        })
    }

    unsafe fn destroy(&self, gl: &glow::Context) {
        gl.delete_vertex_array(self.vao);
        gl.delete_framebuffer(self.fbo);
        gl.delete_program(self.means_prog);
        gl.delete_program(self.colours_prog);
        gl.delete_program(self.extract_prog);
        gl.delete_program(self.blur_prog);
        gl.delete_program(self.composite_prog);
        for tex in self.temp.into_iter().chain([self.out, self.means]).chain(self.colours).flatten() {
            gl.delete_texture(tex.0);
        }
    }

    /// `src` (`w`x`h`) with the light of each group's sources added.
    #[allow(clippy::too_many_arguments)]
    unsafe fn apply(&mut self, gl: &glow::Context, src: glow::Texture, w: u32, h: u32, groups: &[LightGroup; LIGHT_GROUPS], rects: &[f32], rect_groups: &[i32], rect_keys: &[i32], hud: &[f32]) -> Option<glow::Texture> {
        let (ew, eh) = (w + 2 * LIGHT_MARGIN as u32, h + 2 * LIGHT_MARGIN as u32);
        let mut tex = [src; 2 + 2 * LIGHT_GROUPS];
        for (i, slot) in self.temp.iter_mut().enumerate() {
            tex[i] = Interp::float_texture(gl, slot, ew, eh)?;
        }
        let means = Interp::float_texture(gl, &mut self.means, 64, 1)?;
        let fresh = self.colours[0].is_none();
        let previous = Interp::float_texture(gl, &mut self.colours[0], LIGHT_KEYS, 1)?;
        let colours = Interp::float_texture(gl, &mut self.colours[1], LIGHT_KEYS, 1)?;
        if fresh {
            gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, Some(self.fbo));
            gl.framebuffer_texture_2d(glow::DRAW_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(previous), 0);
            gl.clear_color(0.0, 0.0, 0.0, 0.0);
            gl.clear(glow::COLOR_BUFFER_BIT);
        }
        let out = ensure(gl, &mut self.out, w, h)?;
        gl.disable(glow::BLEND);
        gl.disable(glow::SCISSOR_TEST);
        gl.disable(glow::DEPTH_TEST);
        gl.disable(glow::CULL_FACE);
        gl.disable(glow::FRAMEBUFFER_SRGB);
        let size = [w as i32, h as i32];
        let esize = [ew as i32, eh as i32];
        let count = (rects.len() / 4) as i32;
        let (emitted, scratch) = (tex[0], tex[1]);
        let (mut halo_gain, mut room_gain) = ([0f32; 3], [0f32; 3]);
        use Uniform::*;
        let hud_count = (hud.len() / 4) as i32;
        let sources = || {
            [
                Int("rectCount", count),
                Vec4s("rects", rects),
                Ints("rectGroup", rect_groups),
                Ints("rectKey", rect_keys),
                Float("threshold", LIGHT_THRESHOLD),
                Int("hudCount", hud_count),
                Vec4s("hud", hud),
            ]
        };
        let mut uniforms = vec![Tex("src", src), Int2("size", size)];
        uniforms.extend(sources());
        draw_pass(gl, self.vao, self.fbo, self.means_prog, means, 64, 1, &uniforms);
        draw_pass(gl, self.vao, self.fbo, self.colours_prog, colours, LIGHT_KEYS, 1, &[Tex("means", means), Tex("previous", previous), Int("rectCount", count), Ints("rectKey", rect_keys)]);
        self.colours.swap(0, 1);
        for (g, group) in groups.iter().enumerate() {
            let used = rect_groups.iter().any(|&r| r == g as i32);
            if !group.enabled || group.intensity <= 0.0 || !used {
                continue;
            }
            let mut extract = vec![Tex("src", src), Tex("means", means), Tex("colours", colours), Int2("size", size), Int("margin", LIGHT_MARGIN), Int("group", g as i32)];
            extract.extend(sources());
            draw_pass(gl, self.vao, self.fbo, self.extract_prog, emitted, ew, eh, &extract);
            // Blurring spreads a small source thin: the halo's gain grows with
            // its radius, and the room light's with the area it covers.
            let sigma = group.radius.clamp(0.5, 16.0);
            let wide = group.reach.clamp(4.0, 40.0);
            let mut passes = vec![(tex[2 + g], sigma)];
            if group.room > 0.0 {
                passes.push((tex[2 + LIGHT_GROUPS + g], wide));
            }
            for (target, blur) in passes {
                for (dst, input, dir) in [(scratch, emitted, [1, 0]), (target, scratch, [0, 1])] {
                    draw_pass(gl, self.vao, self.fbo, self.blur_prog, dst, ew, eh, &[Tex("src", input), Int2("size", esize), Int2("dir", dir), Float("sigma", blur)]);
                }
            }
            // 1 is the halo's natural strength (0.53 of a full screen blend).
            halo_gain[g] = group.intensity * 0.53 * (1.0 + sigma * 0.5);
            if group.room > 0.0 {
                room_gain[g] = group.room * wide * wide * 0.25 * group.intensity;
            }
        }
        let (n, r) = (2, 2 + LIGHT_GROUPS);
        draw_pass(
            gl,
            self.vao,
            self.fbo,
            self.composite_prog,
            out,
            w,
            h,
            &[
                Tex("base", src),
                Tex("halo0", tex[n]),
                Tex("halo1", tex[n + 1]),
                Tex("halo2", tex[n + 2]),
                Tex("room0", tex[r]),
                Tex("room1", tex[r + 1]),
                Tex("room2", tex[r + 2]),
                Vec3("haloGain", halo_gain),
                Vec3("roomGain", room_gain),
                Int("margin", LIGHT_MARGIN),
            ],
        );
        Some(out)
    }
}

impl Context {
    /// The preset's input: `tex` as is, or with emissive light when it is on.
    unsafe fn lit(&mut self, gl: &glow::Context, tex: glow::Texture, w: u32, h: u32) -> Result<glow::Texture, String> {
        if !self.light_groups.iter().any(|g| g.enabled && g.intensity > 0.0) || self.light_rects.is_empty() {
            return Ok(tex);
        }
        if self.glow.is_none() {
            self.glow = Some(Glow::new(gl)?);
        }
        // Rectangles are given in the full picture; this one starts below the crop.
        let top = self.crop_top as f32;
        let list = &self.light_rects[..self.light_rects.len().min(64)];
        let rects: Vec<f32> = list.iter().flat_map(|(r, _, _)| [r[0], r[1] - top, r[2], r[3] - top]).collect();
        let groups: Vec<i32> = list.iter().map(|(_, g, _)| *g).collect();
        let keys: Vec<i32> = list.iter().map(|(_, _, k)| *k).collect();
        let hud: Vec<f32> = self.light_hud.iter().take(8).flat_map(|r| [r[0], r[1] - top, r[2], r[3] - top]).collect();
        let light_groups = self.light_groups;
        self.glow
            .as_mut()
            .unwrap()
            .apply(gl, tex, w, h, &light_groups, &rects, &groups, &keys, &hud)
            .ok_or_else(|| "cannot create the light textures".to_string())
    }
}

/// Emissive light on the native picture, before the preset, for one group of
/// light sources (0 candles and torches, 1 enemies' death fire, 2 fire
/// attacks): off with `enabled` false; `intensity` 0..2 (1 natural) scales its
/// halo and room light; `radius` is the halo's size and `reach` how far the
/// room light goes, in native pixels; `room` 0..2 how much it lights the
/// surroundings.
#[no_mangle]
pub unsafe extern "C" fn cvgl_set_light_group(ctx: *mut Context, group: u32, enabled: bool, intensity: f32, radius: f32, room: f32, reach: f32) {
    if let Some(ctx) = ctx.as_mut() {
        if let Some(g) = ctx.light_groups.get_mut(group as usize) {
            *g = LightGroup { enabled, intensity: intensity.max(0.0), radius: radius.max(0.5), room: room.max(0.0), reach: reach.clamp(4.0, 40.0) };
        }
    }
}

thread_local! {
    static LAST_ERROR: std::cell::RefCell<String> = const { std::cell::RefCell::new(String::new()) };
}

fn set_error(e: impl std::fmt::Display) {
    LAST_ERROR.with(|l| *l.borrow_mut() = e.to_string());
}

/// The last error as UTF-8, valid until the next call on this thread.
#[no_mangle]
pub extern "C" fn cvgl_last_error(buf: *mut c_char, len: usize) -> usize {
    LAST_ERROR.with(|l| {
        let s = l.borrow();
        if !buf.is_null() && len > 0 {
            let n = s.len().min(len - 1);
            unsafe {
                std::ptr::copy_nonoverlapping(s.as_ptr(), buf as *mut u8, n);
                *buf.add(n) = 0;
            }
        }
        s.len()
    })
}

/// Compiles `preset` for the current OpenGL context. Null on failure (see
/// `cvgl_last_error`).
#[no_mangle]
pub unsafe extern "C" fn cvgl_create(preset: *const c_char) -> *mut Context {
    let result = (|| -> Result<Context, String> {
        let path = CStr::from_ptr(preset).to_str().map_err(|e| e.to_string())?;
        let loader = gl_loader().ok_or("no GLX or EGL loader")?;
        let gl = Arc::new(glow::Context::from_loader_function_cstr(|name| loader(name)));
        let saved = SavedState::save(&gl);
        let options = FilterChainOptions { glsl_version: 0, use_dsa: false, force_no_mipmaps: false, disable_cache: false };
        let features = ShaderFeatures::ORIGINAL_ASPECT_UNIFORMS | ShaderFeatures::FRAMETIME_UNIFORMS;
        let preset = ShaderPreset::try_parse_with_driver_context(Path::new(path), features, VideoDriver::GlCore)
            .map_err(|e| e.to_string())?;
        let fullscreen = wants_fullscreen(&preset);
        let chain = FilterChain::load_from_preset(preset, gl.clone(), Some(&options));
        let made = (|| {
            let chain = chain.map_err(|e| e.to_string())?;
            let read_fbo = gl.create_framebuffer()?;
            let draw_fbo = gl.create_framebuffer()?;
            let pbo = [gl.create_buffer()?, gl.create_buffer()?];
            Ok::<_, String>(Context {
                gl: gl.clone(),
                chain,
                read_fbo,
                draw_fbo,
                staging: [None, None],
                current: 0,
                prev_ok: false,
                output: None,
                mid_output: None,
                interp: None,
                light_groups: [LightGroup::default(); LIGHT_GROUPS],
                light_rects: Vec::new(),
                light_hud: Vec::new(),
                crop_top: 0,
                glow: None,
                readback: Readback { pbo, fence: [None, None], size: [(0, 0); 2], next: 0 },
                crop: (0, 0),
                streak: 0,
                crop_width: 0,
                frame: 0,
                fullscreen,
            })
        })();
        saved.restore(&gl);
        made
    })();
    match result {
        Ok(ctx) => Box::into_raw(Box::new(ctx)),
        Err(e) => {
            set_error(e);
            std::ptr::null_mut()
        }
    }
}

/// Whether a preset draws the screen in its own place inside the whole window,
/// as RetroArch with a Mega Bezel preset does: a Mega Bezel chain whose aspect
/// mode is not "Full" (6). MoonDoes_Lite sets "Full" and is given the CRT area
/// instead; any other preset gets the CRT area too, like RetroArch with the
/// core's aspect ratio.
fn wants_fullscreen(preset: &ShaderPreset) -> bool {
    let mega_bezel = preset.passes.iter().any(|p| {
        let path = p.path.to_string_lossy().to_ascii_lowercase();
        path.contains("mega_bezel") || path.contains("mega-bezel")
    });
    let aspect_mode =
        preset.parameters.iter().find(|p| p.name.as_str() == "HSM_ASPECT_RATIO_MODE").map(|p| p.value).unwrap_or(0.0);
    mega_bezel && (aspect_mode - 6.0).abs() > 0.5
}

/// Whether the loaded preset wants the whole window (see `wants_fullscreen`).
#[no_mangle]
pub unsafe extern "C" fn cvgl_fullscreen_framing(ctx: *const Context) -> bool {
    ctx.as_ref().is_some_and(|c| c.fullscreen)
}

/// Light sources for the emissive light: `count` records of six floats (x0,
/// y0, x1, y1 in the full native picture, the group, and the kind whose colour
/// is remembered, 0 to 255, or -1), at most 64. Only the bright pixels inside
/// them emit; the part off screen shines with the kind's remembered colour. A
/// rectangle given right to left (x0 > x1) only emits fire-coloured pixels.
#[no_mangle]
pub unsafe extern "C" fn cvgl_set_light_rects(ctx: *mut Context, rects: *const f32, count: u32) {
    let Some(ctx) = ctx.as_mut() else { return };
    ctx.light_rects = if rects.is_null() {
        Vec::new()
    } else {
        std::slice::from_raw_parts(rects, count.min(64) as usize * 6).chunks_exact(6).map(|r| ([r[0], r[1], r[2], r[3]], r[4] as i32, r[5] as i32)).collect()
    };
}

/// The HUD's gauges: `count` rectangles (x0, y0, x1, y1 floats in the full
/// native picture), at most 8. They still take light; what is drawn there
/// only gives off light when it has a candle flame's own hue, so a gauge drawn
/// over a flame does not become a light source.
#[no_mangle]
pub unsafe extern "C" fn cvgl_set_light_hud(ctx: *mut Context, rects: *const f32, count: u32) {
    let Some(ctx) = ctx.as_mut() else { return };
    ctx.light_hud = if rects.is_null() {
        Vec::new()
    } else {
        std::slice::from_raw_parts(rects, count.min(8) as usize * 4).chunks_exact(4).map(|r| [r[0], r[1], r[2], r[3]]).collect()
    };
}

/// Sets a preset parameter. False if the preset has no such parameter.
#[no_mangle]
pub unsafe extern "C" fn cvgl_set_param(ctx: *mut Context, name: *const c_char, value: f32) -> bool {
    let (Some(ctx), Ok(name)) = (ctx.as_mut(), CStr::from_ptr(name).to_str()) else { return false };
    ctx.chain.parameters().set_parameter_value(name, value).is_some()
}

impl Context {
    /// Starts reading `tex` back and looks at the previous read: whether the
    /// lines a crop of `top`/`bottom` would remove are black while the rest
    /// holds picture. Returns the crop to use, after `AGREE_FRAMES`.
    unsafe fn decide_crop(&mut self, tex: glow::Texture, w: u32, h: u32, top: u32, bottom: u32, lines: u32) -> (u32, u32) {
        let gl = &self.gl;
        let rb = &mut self.readback;
        let slot = rb.next;
        rb.next ^= 1;
        let mut found = None;
        // The read started two calls ago in this slot.
        if let Some(fence) = rb.fence[slot].take() {
            let ready = gl.client_wait_sync(fence, 0, 0);
            gl.delete_sync(fence);
            let (rw, rh) = rb.size[slot];
            if (ready == glow::ALREADY_SIGNALED || ready == glow::CONDITION_SATISFIED) && rh == lines && top + bottom < rh {
                gl.bind_buffer(glow::PIXEL_PACK_BUFFER, Some(rb.pbo[slot]));
                let bytes = (rw * rh * 4) as usize;
                let ptr = gl.map_buffer_range(glow::PIXEL_PACK_BUFFER, 0, bytes as i32, glow::MAP_READ_BIT);
                if !ptr.is_null() {
                    let px = std::slice::from_raw_parts(ptr, bytes);
                    let row = (rw * 4) as usize;
                    let lit = |r: &[u8]| r.chunks_exact(4).any(|p| p[..3].iter().any(|&c| c > BLACK));
                    let lit_pixels = |r: &[u8]| r.chunks_exact(4).filter(|p| p[..3].iter().any(|&c| c > BLACK)).count();
                    let bands_black =
                        lit_pixels(&px[..top as usize * row]) + lit_pixels(&px[(rh - bottom) as usize * row..]) < BAND_NOISE;
                    let inside = px[top as usize * row..(rh - bottom) as usize * row].chunks(row).any(lit);
                    // A black frame (fade) decides nothing.
                    if inside {
                        found = Some(if bands_black { (top, bottom) } else { (0, 0) });
                    }
                    gl.unmap_buffer(glow::PIXEL_PACK_BUFFER);
                }
            }
        }
        // Start this frame's read.
        if h == lines && top + bottom > 0 {
            gl.bind_framebuffer(glow::READ_FRAMEBUFFER, Some(self.read_fbo));
            gl.framebuffer_texture_2d(glow::READ_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(tex), 0);
            gl.bind_buffer(glow::PIXEL_PACK_BUFFER, Some(rb.pbo[slot]));
            let bytes = (w * h * 4) as i32;
            if rb.size[slot] != (w, h) {
                gl.buffer_data_size(glow::PIXEL_PACK_BUFFER, bytes, glow::STREAM_READ);
                rb.size[slot] = (w, h);
            }
            gl.pixel_store_i32(glow::PACK_ALIGNMENT, 4);
            gl.read_pixels(0, 0, w as i32, h as i32, glow::RGBA, glow::UNSIGNED_BYTE, glow::PixelPackData::BufferOffset(0));
            rb.fence[slot] = gl.fence_sync(glow::SYNC_GPU_COMMANDS_COMPLETE, 0).ok();
        }
        let new_screen = w != self.crop_width;
        self.crop_width = w;
        if h != lines {
            self.crop = (0, 0);
            self.streak = 0;
        } else if let Some(found) = found {
            if found == self.crop {
                self.streak = 0;
            } else {
                self.streak += 1;
                let needed = if found != (0, 0) {
                    CROP_FRAMES
                } else if !new_screen && self.streak_slow() {
                    UNCROP_FRAMES
                } else {
                    AGREE_FRAMES
                };
                if self.streak >= needed {
                    self.crop = found;
                    self.streak = 0;
                }
            }
        }
        self.crop
    }

    /// Dropping a crop that is in place is slow, unless the screen changed.
    fn streak_slow(&self) -> bool {
        self.crop != (0, 0)
    }
}

/// Looks at the native picture `tex` (`w`x`h`) and returns the crop to use
/// this frame, packed as `top << 16 | bottom`: `top`/`bottom` lines while
/// they are black in a `lines`-line picture, 0 otherwise.
#[no_mangle]
pub unsafe extern "C" fn cvgl_analyze(ctx: *mut Context, tex: u32, w: u32, h: u32, top: u32, bottom: u32, lines: u32) -> u32 {
    let (Some(ctx), Some(tex)) = (ctx.as_mut(), native_texture(tex)) else { return 0 };
    let saved = SavedState::save(&ctx.gl);
    let (t, b) = ctx.decide_crop(tex, w, h, top, bottom, lines);
    saved.restore(&ctx.gl);
    (t << 16) | b
}

/// Runs the preset on `tex` (`w`x`h`, minus `top`/`bottom` lines) into an
/// `out_w`x`out_h` texture owned by the context and returns its id, 0 on
/// failure.
#[no_mangle]
pub unsafe extern "C" fn cvgl_render(
    ctx: *mut Context,
    tex: u32,
    w: u32,
    h: u32,
    top: u32,
    bottom: u32,
    out_w: u32,
    out_h: u32,
) -> u32 {
    let (Some(ctx), Some(tex)) = (ctx.as_mut(), native_texture(tex)) else { return 0 };
    if out_w == 0 || out_h == 0 || top + bottom >= h {
        return 0;
    }
    let gl = ctx.gl.clone();
    let saved = SavedState::save(&gl);
    let result = (|| -> Result<u32, String> {
        let lines = h - top - bottom;
        // Cropped (or any) input goes through a staging texture of the exact
        // picture, so Original is the picture and nothing else.
        let next = ctx.current ^ 1;
        ctx.prev_ok = ctx.staging[ctx.current].is_some_and(|(_, sw, sh)| (sw, sh) == (w, lines));
        ctx.current = next;
        let staging = ensure(&gl, &mut ctx.staging[next], w, lines).ok_or("cannot create the staging texture")?;
        gl.bind_framebuffer(glow::READ_FRAMEBUFFER, Some(ctx.read_fbo));
        gl.framebuffer_texture_2d(glow::READ_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(tex), 0);
        gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, Some(ctx.draw_fbo));
        gl.framebuffer_texture_2d(glow::DRAW_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(staging), 0);
        gl.disable(glow::SCISSOR_TEST);
        gl.disable(glow::FRAMEBUFFER_SRGB);
        gl.blit_framebuffer(
            0,
            top as i32,
            w as i32,
            (h - bottom) as i32,
            0,
            0,
            w as i32,
            lines as i32,
            glow::COLOR_BUFFER_BIT,
            glow::NEAREST,
        );
        let output = ensure(&gl, &mut ctx.output, out_w, out_h).ok_or("cannot create the output texture")?;
        ctx.crop_top = top;
        let staging = ctx.lit(&gl, staging, w, lines)?;
        let input = GLImage { handle: Some(staging), format: glow::RGBA8, size: Size::new(w, lines) };
        let out = GLImage { handle: Some(output), format: glow::RGBA8, size: Size::new(out_w, out_h) };
        let viewport = Viewport { x: 0.0, y: 0.0, mvp: None, output: &out, size: Size::new(out_w, out_h) };
        let options = FrameOptions { frames_per_second: 60.0, frametime_delta: 16, ..Default::default() };
        ctx.chain.frame(&input, &viewport, ctx.frame, Some(&options)).map_err(|e| e.to_string())?;
        ctx.frame += 1;
        Ok(output.0.get())
    })();
    saved.restore(&gl);
    result.unwrap_or_else(|e| {
        set_error(e);
        0
    })
}

/// Generates the picture halfway between the previous and the current frame
/// given to `cvgl_render` and runs the preset on it into an `out_w`x`out_h`
/// texture. 0 when there is no previous frame of the same size, or on error.
#[no_mangle]
pub unsafe extern "C" fn cvgl_interpolate(ctx: *mut Context, out_w: u32, out_h: u32) -> u32 {
    let Some(ctx) = ctx.as_mut() else { return 0 };
    if !ctx.prev_ok || out_w == 0 || out_h == 0 {
        return 0;
    }
    let (Some((cur, w, h)), Some((prev, _, _))) = (ctx.staging[ctx.current], ctx.staging[ctx.current ^ 1]) else { return 0 };
    let gl = ctx.gl.clone();
    let saved = SavedState::save(&gl);
    let result = (|| -> Result<u32, String> {
        if ctx.interp.is_none() {
            ctx.interp = Some(Interp::new(&gl)?);
        }
        let mid = ctx.interp.as_mut().unwrap().generate(&gl, prev, cur, w, h).ok_or("cannot create the interpolation textures")?;
        let output = ensure(&gl, &mut ctx.mid_output, out_w, out_h).ok_or("cannot create the output texture")?;
        let mid = ctx.lit(&gl, mid, w, h)?;
        let input = GLImage { handle: Some(mid), format: glow::RGBA8, size: Size::new(w, h) };
        let out = GLImage { handle: Some(output), format: glow::RGBA8, size: Size::new(out_w, out_h) };
        let viewport = Viewport { x: 0.0, y: 0.0, mvp: None, output: &out, size: Size::new(out_w, out_h) };
        let options = FrameOptions { frames_per_second: 120.0, frametime_delta: 8, ..Default::default() };
        ctx.chain.frame(&input, &viewport, ctx.frame, Some(&options)).map_err(|e| e.to_string())?;
        ctx.frame += 1;
        Ok(output.0.get())
    })();
    saved.restore(&gl);
    result.unwrap_or_else(|e| {
        set_error(e);
        0
    })
}

/// Clears the default framebuffer to black and copies `tex` (`w`x`h`, row 0
/// at the top) 1:1 to (`x`, `y`) of a `screen_w`x`screen_h` window, measured
/// from its top-left corner. The caller swaps buffers afterwards.
#[no_mangle]
pub unsafe extern "C" fn cvgl_present(ctx: *mut Context, tex: u32, x: i32, y: i32, w: i32, h: i32, screen_w: i32, screen_h: i32) {
    let (Some(ctx), Some(tex)) = (ctx.as_mut(), native_texture(tex)) else { return };
    let gl = ctx.gl.clone();
    let saved = SavedState::save(&gl);
    gl.bind_framebuffer(glow::DRAW_FRAMEBUFFER, None);
    gl.disable(glow::SCISSOR_TEST);
    gl.disable(glow::FRAMEBUFFER_SRGB);
    gl.viewport(0, 0, screen_w, screen_h);
    gl.clear_color(0.0, 0.0, 0.0, 1.0);
    gl.clear(glow::COLOR_BUFFER_BIT);
    gl.bind_framebuffer(glow::READ_FRAMEBUFFER, Some(ctx.read_fbo));
    gl.framebuffer_texture_2d(glow::READ_FRAMEBUFFER, glow::COLOR_ATTACHMENT0, glow::TEXTURE_2D, Some(tex), 0);
    // Rows go up in OpenGL's window space: the top row lands at screen_h - y.
    gl.blit_framebuffer(0, 0, w, h, x, screen_h - y, x + w, screen_h - y - h, glow::COLOR_BUFFER_BIT, glow::NEAREST);
    saved.restore(&gl);
}

#[no_mangle]
pub unsafe extern "C" fn cvgl_destroy(ctx: *mut Context) {
    if ctx.is_null() {
        return;
    }
    let ctx = Box::from_raw(ctx);
    let gl = ctx.gl.clone();
    for tex in [ctx.staging[0], ctx.staging[1], ctx.output, ctx.mid_output].into_iter().flatten() {
        gl.delete_texture(tex.0);
    }
    if let Some(light) = &ctx.glow {
        light.destroy(&gl);
    }
    if let Some(interp) = &ctx.interp {
        interp.destroy(&gl);
    }
    gl.delete_framebuffer(ctx.read_fbo);
    gl.delete_framebuffer(ctx.draw_fbo);
    for (i, pbo) in ctx.readback.pbo.iter().enumerate() {
        if let Some(f) = ctx.readback.fence[i] {
            gl.delete_sync(f);
        }
        gl.delete_buffer(*pbo);
    }
    drop(ctx);
}
