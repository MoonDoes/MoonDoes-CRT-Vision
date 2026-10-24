using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ImGuiNET;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;
using Sotn;

// MoonDoes CRT-Vision inside SymphonyRecomp.
//
// The recomp hands its native PSX picture (256/320/512 x 240...) to the
// Output panel through OutputPanel.SetTexture. This mod runs a libretro
// .slangp preset on that picture with librashader's OpenGL runtime
// (libcrtvision_gl.so, same thread, same context) and draws the result 1:1 over
// the whole window while the game is in fullscreen. Nothing is captured or
// resampled, there is no Vulkan translation and no compositor in between.
//
// While the mouse is on the menu bar or a menu is open, the recomp's own UI is
// shown as it is. Settings live in crtvision.json next to this file.

public sealed class CrtVisionConfig
{
    public bool Enabled { get; set; } = true;
    public string Preset { get; set; } = "";
    /// The bundled preset the "Default" button goes back to. `Params` only
    /// apply to it; any other preset loads exactly as it is configured.
    public string DefaultPreset { get; set; } = "";
    /// Folder the preset browser opened last.
    public string BrowseFolder { get; set; } = "";
    public Dictionary<string, float> Params { get; set; } = new();
    /// Lines the game leaves black above and below its gameplay picture;
    /// cropped (and the picture fills the height) only while they are black.
    /// Crop those bands in real time; off shows the whole 4:3 picture always.
    public bool CropBands { get; set; } = true;
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLines { get; set; } = 240;
    /// Whole multiples of the picture's lines when it is not cropped.
    public bool IntegerScale { get; set; } = true;
    public bool OnlyFullscreen { get; set; } = true;
    /// Height of the strip at the top where the mouse brings the menu bar back.
    public int MenuHotZone { get; set; } = 40;
    /// 120 presentations per second from the game's 60: a picture generated
    /// between each pair of frames (motion estimated on the native picture),
    /// shown half a frame before the real one. The game's speed is unchanged.
    /// Experimental, Linux, needs Variable Refresh Rate (VRR / FreeSync /
    /// G-Sync): may flicker now and then. Off by default.
    public bool FrameGeneration { get; set; }
    /// Dynamic light from the game's fire, on the native picture before the
    /// CRT preset, in three groups that are turned on and tuned separately:
    /// the room's candles and torches, the fire destroyed enemies and broken
    /// candles burst into, and every fire attack, the player's and the
    /// enemies' (fire only, not magic).
    /// Light the built-in rules do not name after fire is added by name: the
    /// castle entrance's stone cup fire and the lightning bolts light like
    /// lamps; the flames dead Mermen burn in are death fire; the Merman's fire
    /// splash and Hellfire's flames are attacks.
    public LightGroupConfig CandleLight { get; set; } = new()
    {
        Enabled = true, Intensity = 0.95f, Radius = 3.4f, Room = 1.62f, Reach = 19f,
        Always = new() { "NP3_EntityBackgroundBlock", "NO3_EntityBackgroundBlock", "LightningThunder" },
    };
    public LightGroupConfig DeathFireLight { get; set; } = new()
    {
        Enabled = false, Intensity = 0.51f, Radius = 2.6f, Room = 0.80f, Reach = 9f,
        Always = new() { "DeadMerman" },
    };
    public LightGroupConfig FireAttackLight { get; set; } = new()
    {
        Enabled = false, Intensity = 0.53f, Radius = 2.6f, Room = 0.85f, Reach = 10f,
        Always = new() { "MediumWaterSplash", "UnkId24" },
    };
    /// Debug (crtvision.json only): draws every object's name over the
    /// picture, coloured by the group it gives off light in (grey: none), with
    /// the light areas, and lists each kind of object met in light-objects.txt
    /// next to this file.
    public bool DebugLights { get; set; }
    /// Objects that never give off light, by a fragment of their name as the
    /// debug view shows it. Wins over every group's `Always` and the built-in
    /// rules.
    public List<string> LightNever { get; set; } = new();
    /// Lines between the game's object coordinates and the native picture.
    public float LightOffsetY { get; set; }
}

/// One group of light sources and how it lights.
public sealed class LightGroupConfig
{
    public bool Enabled { get; set; }
    /// Strength of the group's light (its halo and room light), 0 to 2; 1 is
    /// natural.
    public float Intensity { get; set; } = 1f;
    /// Size of the halo around each source itself, in PSX pixels.
    public float Radius { get; set; } = 3.2f;
    /// How much the sources light up the surfaces around them, 0 to 2.
    public float Room { get; set; } = 0.6f;
    /// How far that light goes, in PSX pixels.
    public float Reach { get; set; } = 24f;
    /// Objects that always give off light in this group (any colour, still or
    /// not), by a fragment of their name as the debug view shows it
    /// ("FlameAttack", "EntityFoo_no0"). Case does not matter. "stage:name"
    /// ("no0:func_801B2F4C") limits it to one stage, as light-objects.txt
    /// names it.
    public List<string> Always { get; set; } = new();
}

public class CrtVisionMod : IMod
{
    private const string ModId = "crtvision";

    // libcrtvision_gl.so
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateFn(byte[] preset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool SetParamFn(IntPtr ctx, byte[] name, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AnalyzeFn(IntPtr ctx, uint tex, uint w, uint h, uint top, uint bottom, uint lines);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RenderFn(IntPtr ctx, uint tex, uint w, uint h, uint top, uint bottom, uint outW, uint outH);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool FullscreenFramingFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint InterpolateFn(IntPtr ctx, uint outW, uint outH);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLightGroupFn(IntPtr ctx, uint group, [MarshalAs(UnmanagedType.I1)] bool enabled, float intensity, float radius, float room, float reach);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLightRectsFn(IntPtr ctx, float[] rects, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLightHudFn(IntPtr ctx, float[] rects, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PresentFn(IntPtr ctx, uint tex, int x, int y, int w, int h, int screenW, int screenH);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr LastErrorFn(byte[] buf, UIntPtr len);

    private delegate void SetTextureOrig(uint id, int w, int h, float aspect);

    private static CrtVisionMod? instance;

    private string root = "";
    private CrtVisionConfig config = new();
    private IntPtr native, ctx;
    private CreateFn? create;
    private SetParamFn? setParam;
    private AnalyzeFn? analyze;
    private RenderFn? render;
    private DestroyFn? destroy;
    private FullscreenFramingFn? fullscreenFraming;
    private InterpolateFn? interpolate;
    private PresentFn? present;
    private SetLightGroupFn? setLightGroup;
    private SetLightRectsFn? setLightRects;
    private SetLightHudFn? setLightHud;
    private LastErrorFn? lastError;
    private readonly List<object> hooks = new();
    private string status = "";

    // Last picture handed to the Output panel, and what was made of it.
    private uint outTex;
    /// Shown by the recomp's own swap: the generated picture with frame
    /// generation, the real one otherwise.
    private uint shownTex;
    /// Real picture still to present half a frame later (frame generation).
    private uint pendingTex;
    private Vector2 outPos, outSize;
    private int vsyncs, renderedVsync = -1;
    private (uint tex, int w, int h, uint top, uint bottom, Vector2 size) lastRender;
    private bool drawThisFrame;
    /// The loaded preset wants the whole window (Mega Bezel bezels).
    private bool presetFullscreen;
    /// Preset chosen in the browser, compiled on the next game frame.
    private volatile string? pendingPreset;
    private (uint, uint) lastCrop;
    private long lastRealAt;

    // Reflection on the host window (fullscreen state).
    private object? window;
    private PropertyInfo? windowState, windowBorder;
    private object? glContext;
    private MethodInfo? swapBuffers;

    // The recomp's frame clock: length of a PSX frame.
    private FieldInfo? frameMsField;

    // Rates over the last few seconds, for the settings panel and the log.
    private readonly System.Diagnostics.Stopwatch statsClock = System.Diagnostics.Stopwatch.StartNew();
    private int statVsyncs, statRenders, statDraws, statPresents;
    // Diagnostics: frames drawn without the CRT and why, frames generation skipped.
    private int diagNoCrt, diagUi, diagRepeat, diagNoMid, diagSlow, diagStall;
    // Intervals between presentations: mean, spread, extremes (ms).
    private long lastPresentAt;
    private double gapSum, gapSq, gapMin = double.MaxValue, gapMax;
    private int gapCount;

    private void MarkPresent()
    {
        statPresents++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastPresentAt != 0)
        {
            double ms = (now - lastPresentAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 12) diagSlow++;
            if (ms < 100)
            {
                gapSum += ms; gapSq += ms * ms; gapCount++;
                gapMin = Math.Min(gapMin, ms); gapMax = Math.Max(gapMax, ms);
            }
        }
        lastPresentAt = now;
    }
    private double statRenderMs;
    private string stats = "";

    private Socket? stateSocket;
    private string? socketPath;

    public void OnLoad()
    {
        instance = this;
        root = ModLoader.LoadedMods.FirstOrDefault(m => m.Id == ModId)?.SourcePath ?? AppContext.BaseDirectory;
        LoadConfig();
        try
        {
            LoadNative();
            InstallHooks();
            Event.AddListener<VSyncEvent>(OnVSync);
            StartStateSocket();
            StartKeepAwake();
            status = "ready";
        }
        catch (Exception e)
        {
            status = "error: " + e.Message;
            Console.Error.WriteLine("[crtvision] " + e);
        }
        Console.WriteLine($"[crtvision] {status}; preset {config.Preset}");
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        foreach (var h in hooks) (h as IDisposable)?.Dispose();
        hooks.Clear();
        if (ctx != IntPtr.Zero) destroy?.Invoke(ctx);
        ctx = IntPtr.Zero;
        StopStateSocket();
        StopKeepAwake();
    }

    // ------------------------------------------------------------ keep awake

    // GNOME blanks and locks the screen after a while with no keyboard or mouse
    // input; a gamepad does not count, and the game does not ask it not to.
    // While the mod is loaded, gnome-session-inhibit holds an idle inhibition;
    // it waits on the game's process, so it ends with the game even if the
    // game is killed, and when the mod is turned off.
    private System.Diagnostics.Process? keepAwake;

    private void StartKeepAwake()
    {
        if (keepAwake is { HasExited: false } || !OperatingSystem.IsLinux()) return;
        string? inhibit = new[] { "/usr/bin/gnome-session-inhibit", "/bin/gnome-session-inhibit" }.FirstOrDefault(File.Exists);
        if (inhibit == null) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(inhibit) { UseShellExecute = false };
            foreach (var arg in new[] { "--inhibit=idle", "--app-id=sotn", "--reason=Playing Castlevania: Symphony of the Night",
                         "tail", $"--pid={Environment.ProcessId}", "-f", "/dev/null" })
                start.ArgumentList.Add(arg);
            keepAwake = System.Diagnostics.Process.Start(start);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[crtvision] cannot keep the screen awake: " + e.Message);
        }
    }

    private void StopKeepAwake()
    {
        try { if (keepAwake is { HasExited: false }) keepAwake.Kill(true); }
        catch { }
        keepAwake = null;
    }

    public void DrawSettings()
    {
        bool enabled = config.Enabled;
        if (ImGui.Checkbox("CRT enabled (fullscreen)", ref enabled))
        {
            config.Enabled = enabled;
            SaveConfig();
        }
        // Mask strength belongs to the default preset; others load as they are.
        if (!IsDefaultPreset) ImGui.BeginDisabled();
        float mask = config.Params.TryGetValue("maskstr", out var m) ? m : 0.3f;
        bool maskChanged = ImGui.SliderFloat("Mask strength", ref mask, 0f, 1f, "%.2f");
        if (!IsDefaultPreset) ImGui.EndDisabled();
        if (maskChanged)
        {
            config.Params["maskstr"] = mask;
            if (ctx != IntPtr.Zero) setParam!(ctx, Utf8("maskstr"), mask);
            SaveConfig();
        }
        bool generation = config.FrameGeneration;
        if (ImGui.Checkbox("Frame generation 60 -> 120 (experimental)", ref generation))
        {
            config.FrameGeneration = generation;
            SaveConfig();
        }
        if (ImGui.IsItemHovered() || config.FrameGeneration)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "Experimental, Linux only. Requires Variable Refresh Rate\n(VRR / FreeSync / G-Sync) enabled in the system and the monitor.\nMay cause occasional flicker and adds ~8 ms of latency.");
        ImGui.Separator();
        ImGui.TextUnformatted("Dynamic light");
        for (int g = 0; g < LightGroups; g++) DrawLightGroup(g);
        ImGui.Separator();
        bool cropBands = config.CropBands;
        if (ImGui.Checkbox("Crop the game's black bands (gameplay fills the screen height)", ref cropBands))
        {
            config.CropBands = cropBands;
            renderedVsync = -1;
            SaveConfig();
        }
        bool integer = config.IntegerScale;
        if (ImGui.Checkbox("Integer vertical scale", ref integer))
        {
            config.IntegerScale = integer;
            SaveConfig();
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Preset: " + Path.GetFileNameWithoutExtension(config.Preset));
        if (ImGui.Button("Browse...")) OpenBrowser();
        DrawBrowser();
        ImGui.SameLine();
        if (IsDefaultPreset) ImGui.BeginDisabled();
        if (ImGui.Button("Default")) pendingPreset = config.DefaultPreset;
        if (IsDefaultPreset) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Restore all defaults")) ImGui.OpenPopup("Restore defaults?###crtvision-reset");
        if (ImGui.BeginPopup("Restore defaults?###crtvision-reset"))
        {
            ImGui.TextUnformatted("Every option and the default preset come back.\nYour light lists (always / lightNever) are reset too.");
            if (ImGui.Button("Restore")) { RestoreDefaults(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (pendingPreset != null) ImGui.TextDisabled("Loading " + Path.GetFileName(pendingPreset) + "...");
        ImGui.TextWrapped(status);
        if (stats.Length > 0) ImGui.TextDisabled(stats);
    }

    // ------------------------------------------------------------ config

    private string ConfigPath => Path.Combine(root, "crtvision.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                config = JsonSerializer.Deserialize<CrtVisionConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[crtvision] cannot read {ConfigPath}: {e.Message}");
        }
        if (config.DefaultPreset.Length == 0) config.DefaultPreset = config.Preset;
        config.Preset = ResolvePath(config.Preset);
        config.DefaultPreset = ResolvePath(config.DefaultPreset);
    }

    /// `~` and variables expanded; a relative path (the shared package) lives
    /// inside the mod folder.
    private string ResolvePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Replace("~", Environment.GetEnvironmentVariable("HOME") ?? "~"));
        return path.Length > 0 && !Path.IsPathRooted(path) ? Path.GetFullPath(Path.Combine(root, path)) : path;
    }

    private bool IsDefaultPreset => string.Equals(Path.GetFullPath(config.Preset), Path.GetFullPath(config.DefaultPreset), StringComparison.Ordinal);

    private void SaveConfig()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions)); }
        catch (Exception e) { Console.Error.WriteLine($"[crtvision] cannot save {ConfigPath}: {e.Message}"); }
    }

    // ------------------------------------------------------------ native

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(native, name));

    private void LoadNative()
    {
        native = NativeLibrary.Load(Path.Combine(root, "native", "libcrtvision_gl.so"));
        create = Export<CreateFn>("cvgl_create");
        setParam = Export<SetParamFn>("cvgl_set_param");
        analyze = Export<AnalyzeFn>("cvgl_analyze");
        render = Export<RenderFn>("cvgl_render");
        destroy = Export<DestroyFn>("cvgl_destroy");
        fullscreenFraming = Export<FullscreenFramingFn>("cvgl_fullscreen_framing");
        interpolate = Export<InterpolateFn>("cvgl_interpolate");
        present = Export<PresentFn>("cvgl_present");
        setLightGroup = Export<SetLightGroupFn>("cvgl_set_light_group");
        setLightRects = Export<SetLightRectsFn>("cvgl_set_light_rects");
        setLightHud = Export<SetLightHudFn>("cvgl_set_light_hud");
        lastError = Export<LastErrorFn>("cvgl_last_error");
    }

    private string NativeError()
    {
        var buf = new byte[1024];
        lastError!(buf, (UIntPtr)buf.Length);
        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    /// The chain is compiled lazily, on the render thread with the context current.
    private bool EnsureChain()
    {
        if (pendingPreset != null) SwitchPreset(pendingPreset);
        if (ctx != IntPtr.Zero) return true;
        if (status.StartsWith("error")) return false;
        if (!File.Exists(config.Preset))
        {
            status = "error: preset not found: " + config.Preset;
            return false;
        }
        var started = DateTime.Now;
        ctx = create!(Utf8(config.Preset));
        if (ctx == IntPtr.Zero)
        {
            status = "error: " + NativeError();
            Console.Error.WriteLine("[crtvision] " + status);
            return false;
        }
        if (IsDefaultPreset)
            foreach (var (name, value) in config.Params) setParam!(ctx, Utf8(name), value);
        presetFullscreen = fullscreenFraming!(ctx);
        ApplyGlow();
        status = $"{Path.GetFileName(config.Preset)} compiled in {(DateTime.Now - started).TotalMilliseconds:0} ms";
        Console.WriteLine("[crtvision] " + status);
        return true;
    }

    /// Compiles `path` and, if it works, replaces the running preset with it.
    /// On the game's thread, with its OpenGL context current.
    /// Every option back to its default, and the default preset; what the
    /// CRT-Vision profile sets (the crop, fullscreen only) stays.
    private void RestoreDefaults()
    {
        var old = config;
        config = new CrtVisionConfig
        {
            Preset = old.DefaultPreset,
            DefaultPreset = old.DefaultPreset,
            BrowseFolder = old.BrowseFolder,
            CropTop = old.CropTop,
            CropBottom = old.CropBottom,
            CropLines = old.CropLines,
            OnlyFullscreen = old.OnlyFullscreen,
            MenuHotZone = old.MenuHotZone,
            Params = new() { ["maskstr"] = 0.3f },
        };
        pendingPreset = config.DefaultPreset;
        ApplyGlow();
        renderedVsync = -1;
        SaveConfig();
    }

    private void SwitchPreset(string path)
    {
        pendingPreset = null;
        if (!File.Exists(path))
        {
            status = "error: preset not found: " + path;
            return;
        }
        var started = DateTime.Now;
        IntPtr next = create!(Utf8(path));
        if (next == IntPtr.Zero)
        {
            // Keep the preset that works; report why the new one does not.
            status = $"cannot load {Path.GetFileName(path)}: {NativeError()}";
            Console.Error.WriteLine("[crtvision] " + status);
            return;
        }
        if (ctx != IntPtr.Zero) destroy!(ctx);
        ctx = next;
        config.Preset = path;
        SaveConfig();
        if (IsDefaultPreset)
            foreach (var (name, value) in config.Params) setParam!(ctx, Utf8(name), value);
        presetFullscreen = fullscreenFraming!(ctx);
        ApplyGlow();
        outTex = 0;
        lastRender = default;
        status = $"{Path.GetFileName(path)} compiled in {(DateTime.Now - started).TotalMilliseconds:0} ms" +
                 (presetFullscreen ? " (full-window preset)" : "");
        Console.WriteLine("[crtvision] " + status);
    }

    private void DrawLightGroup(int g)
    {
        var c = Group(g);
        bool on = c.Enabled;
        if (ImGui.Checkbox(GroupTitles[g], ref on))
        {
            c.Enabled = on;
            ApplyGlow();
            SaveConfig();
        }
        if (!c.Enabled) return;
        ImGui.Indent();
        float intensity = c.Intensity, radius = c.Radius, room = c.Room, reach = c.Reach;
        bool changed = ImGui.SliderFloat($"Intensity##light{g}", ref intensity, 0f, 2f, "%.2f");
        changed |= ImGui.SliderFloat($"Halo radius##light{g}", ref radius, 1f, 16f, "%.1f px");
        changed |= ImGui.SliderFloat($"Room lighting##light{g}", ref room, 0f, 2f, "%.2f");
        changed |= ImGui.SliderFloat($"Light reach##light{g}", ref reach, 4f, 40f, "%.0f px");
        ImGui.Unindent();
        if (changed)
        {
            (c.Intensity, c.Radius, c.Room, c.Reach) = (intensity, radius, room, reach);
            ApplyGlow();
            SaveConfig();
        }
    }

    private void ApplyGlow()
    {
        if (ctx == IntPtr.Zero) return;
        for (int g = 0; g < LightGroups; g++)
        {
            var c = Group(g);
            setLightGroup!(ctx, (uint)g, c.Enabled, c.Intensity, c.Radius, c.Room, c.Reach);
        }
        renderedVsync = -1; // show the change even while the game is paused
    }

    // ------------------------------------------------------------ hooks

    private static object NewHook(MethodBase target, Delegate impl)
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MonoMod.RuntimeDetour")
                  ?? Assembly.Load("MonoMod.RuntimeDetour");
        var hook = asm.GetType("MonoMod.RuntimeDetour.Hook")!;
        return hook.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) })!.Invoke(new object[] { target, impl });
    }

    private void InstallHooks()
    {
        var asm = typeof(IMod).Assembly;
        var panel = asm.GetType("RecompOne.Runtime.Host.Window.OutputPanel") ?? throw new Exception("OutputPanel not found");
        var setTexture = panel.GetMethod("SetTexture", BindingFlags.Public | BindingFlags.Static) ?? throw new Exception("SetTexture not found");
        var draw = panel.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance) ?? throw new Exception("Draw not found");
        hooks.Add(NewHook(setTexture, new Action<SetTextureOrig, uint, int, int, float>(OnSetTexture)));
        hooks.Add(NewHook(draw, new Action<Action<object>, object>(OnDraw)));
        var clock = asm.GetType("RecompOne.Runtime.Host.FrameClock") ?? throw new Exception("FrameClock not found");
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        frameMsField = clock.GetField("FrameMs", flags);
        var throttle = clock.GetMethod("Throttle", BindingFlags.Public | BindingFlags.Static) ?? throw new Exception("Throttle not found");
        hooks.Add(NewHook(throttle, new Action<Action>(OnThrottle)));

        var host = asm.GetType("RecompOne.Runtime.Host.HostWindow")!;
        window = host.GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private void OnVSync(VSyncEvent e)
    {
        Interlocked.Increment(ref vsyncs);
    }

    private bool Fullscreen()
    {
        if (!config.OnlyFullscreen) return true;
        var asm = typeof(IMod).Assembly;
        window ??= asm.GetType("RecompOne.Runtime.Host.HostWindow")!
            .GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (window == null) return false;
        windowState ??= window.GetType().GetProperty("WindowState");
        windowBorder ??= window.GetType().GetProperty("WindowBorder");
        string state = windowState?.GetValue(window)?.ToString() ?? "";
        string border = windowBorder?.GetValue(window)?.ToString() ?? "";
        return state == "Fullscreen" || (border == "Hidden" && state != "Minimized");
    }

    /// The recomp's UI is in use: a menu/popup open, or the mouse moved onto
    /// the menu bar in the last two seconds (a pointer left resting at the
    /// top edge does not keep the CRT off).
    private bool UiInUse()
    {
        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return true;
        var io = ImGui.GetIO();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (io.MouseDelta != Vector2.Zero && io.MousePos.Y > -1e6f) mouseMovedAt = now;
        bool recent = mouseMovedAt != 0 && (now - mouseMovedAt) < 2 * System.Diagnostics.Stopwatch.Frequency;
        return recent && io.MousePos.Y >= 0 && io.MousePos.Y < config.MenuHotZone;
    }

    /// Timestamp of the last mouse movement, 0 if it never moved.
    private long mouseMovedAt;

    private void OnSetTexture(SetTextureOrig orig, uint id, int w, int h, float aspect)
    {
        orig(id, w, h, aspect);
        drawThisFrame = false;
        try
        {
            if (!config.Enabled || id == 0 || w <= 0 || h <= 0 || !Fullscreen() || !EnsureChain()) return;
            var display = ImGui.GetIO().DisplaySize;
            if (display.X < 1 || display.Y < 1) return;

            // A full-window preset places the screen itself: no crop, the whole
            // window as its viewport.
            uint crop = presetFullscreen || !config.CropBands ? 0 : analyze!(ctx, id, (uint)w, (uint)h, (uint)config.CropTop, (uint)config.CropBottom, (uint)config.CropLines);
            uint top = crop >> 16, bottom = crop & 0xffff;
            if ((top, bottom) != lastCrop)
            {
                Console.WriteLine($"[crtvision] crop {lastCrop.Item1}/{lastCrop.Item2} -> {top}/{bottom} at {w}x{h} (vsync {Volatile.Read(ref vsyncs)})");
                lastCrop = (top, bottom);
            }
            int lines = h - (int)top - (int)bottom;
            // The recomp's aspect is the whole picture's; cropped lines keep
            // their shape, so the picture becomes relatively wider.
            float pictureAspect = aspect * h / lines;
            float height;
            if (top + bottom > 0 || !config.IntegerScale) height = display.Y;
            else height = Math.Max(1, MathF.Floor(display.Y / lines)) * lines;
            float width = MathF.Round(height * pictureAspect);
            if (width > display.X)
            {
                width = display.X;
                height = MathF.Round(width / pictureAspect);
            }
            if (presetFullscreen) (width, height) = (display.X, display.Y);
            var size = new Vector2(width, height);

            // One run per game frame: the host may redraw faster than the PSX.
            int frame = Volatile.Read(ref vsyncs);
            bool same = lastRender == (id, w, h, top, bottom, size);
            if (same && frame == renderedVsync && outTex != 0) diagRepeat++;
            if (!same || frame != renderedVsync || outTex == 0)
            {
                UpdateLights(w);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                uint tex = render!(ctx, id, (uint)w, (uint)h, top, bottom, (uint)width, (uint)height);
                statRenderMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                statRenders++;
                if (tex == 0)
                {
                    status = "error: " + NativeError();
                    Console.Error.WriteLine("[crtvision] " + status);
                    config.Enabled = false;
                    return;
                }
                outTex = tex;
                shownTex = tex;
                pendingTex = 0;
                // After a stall (a room loading) the next real frame is already
                // late: a generated one before it would go out right with it.
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                double sinceReal = lastRealAt == 0 ? 0 : (nowTicks - lastRealAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lastRealAt = nowTicks;
                bool stalled = sinceReal > 25;
                if (stalled) diagStall++;
                if (config.FrameGeneration && !stalled)
                {
                    uint mid = interpolate!(ctx, (uint)width, (uint)height);
                    statRenders++;
                    if (mid == 0) diagNoMid++;
                    if (mid != 0)
                    {
                        shownTex = mid;
                        pendingTex = tex;
                    }
                }
                renderedVsync = frame;
                lastRender = (id, w, h, top, bottom, size);
            }
            outSize = size;
            outPos = new Vector2(MathF.Floor((display.X - width) / 2), MathF.Floor((display.Y - height) / 2));
            drawThisFrame = true;
        }
        catch (Exception e)
        {
            status = "error: " + e.Message;
            config.Enabled = false;
            Console.Error.WriteLine("[crtvision] " + e);
        }
    }

    private void OnDraw(Action<object> orig, object self)
    {
        orig(self);
        statDraws++;
        if (statsClock.Elapsed.TotalSeconds >= 5)
        {
            double secs = statsClock.Elapsed.TotalSeconds;
            int v = Volatile.Read(ref vsyncs);
            stats = $"game {(v - statVsyncs) / secs:0.0} fps, presented {statPresents / secs:0.0}/s, shader {statRenders / secs:0.0}/s " +
                    $"({(statRenders > 0 ? statRenderMs / statRenders : 0):0.00} ms CPU), drawn {statDraws / secs:0.0}/s, " +
                    $"crop {lastRender.top}/{lastRender.bottom}, {lastRender.w}x{lastRender.h} -> {lastRender.size.X}x{lastRender.size.Y}";
            if (gapCount > 0)
            {
                double mean = gapSum / gapCount;
                double sd = Math.Sqrt(Math.Max(0, gapSq / gapCount - mean * mean));
                stats += $", interval {mean:0.00} ms (±{sd:0.00}, {gapMin:0.00}-{gapMax:0.00})";
            }
            stats += $", diag noCRT={diagNoCrt} ui={diagUi} repeated={diagRepeat} noGenerated={diagNoMid} slow={diagSlow} stalls={diagStall}";
            diagNoCrt = diagUi = diagRepeat = diagNoMid = diagSlow = diagStall = 0;
            gapSum = gapSq = gapMax = 0; gapMin = double.MaxValue; gapCount = 0;
            Console.WriteLine("[crtvision] " + stats);
            statVsyncs = v; statRenders = 0; statDraws = 0; statPresents = 0; statRenderMs = 0;
            statsClock.Restart();
        }
        bool ui = drawThisFrame && shownTex != 0 && UiInUse();
        if (ui) diagUi++;
        else if (!drawThisFrame || shownTex == 0) diagNoCrt++;
        if (!drawThisFrame || shownTex == 0 || ui)
        {
            pendingTex = 0;
            return;
        }
        MarkPresent();
        // Over everything, at whole pixels and at its own size: copied 1:1.
        // Row 0 is the top of the picture, as in the recomp's own textures.
        var list = ImGui.GetForegroundDrawList();
        list.AddRectFilled(Vector2.Zero, ImGui.GetIO().DisplaySize, 0xFF000000);
        list.AddImage((IntPtr)shownTex, outPos, outPos + outSize);
        if (config.DebugLights) DrawLightAreas(list);
    }

    /// The recomp waits here for the next PSX frame, right after its swap. With
    /// frame generation the real picture is presented half way through.
    private void OnThrottle(Action orig)
    {
        uint tex = pendingTex;
        pendingTex = 0;
        if (tex != 0 && ctx != IntPtr.Zero)
        {
            try
            {
                // Half a game frame after the generated picture went out with
                // the recomp's own swap (just after OnDraw), not half way
                // through the clock: the game's logic runs first in each frame.
                if (frameMsField?.GetValue(null) is double period && lastPresentAt != 0)
                {
                    long due = lastPresentAt + (long)(period / 2 * System.Diagnostics.Stopwatch.Frequency / 1000);
                    while (true)
                    {
                        double left = (due - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (left <= 0) break;
                        if (left > 1.5) Thread.Sleep(1); else Thread.SpinWait(200);
                    }
                }
                var display = ImGui.GetIO().DisplaySize;
                present!(ctx, tex, (int)outPos.X, (int)outPos.Y, (int)outSize.X, (int)outSize.Y, (int)display.X, (int)display.Y);
                Swap();
                MarkPresent();
            }
            catch (Exception e)
            {
                status = "error: " + e.Message;
                config.FrameGeneration = false;
                Console.Error.WriteLine("[crtvision] " + e);
            }
        }
        orig();
    }

    private void Swap()
    {
        if (swapBuffers == null)
        {
            glContext = window?.GetType().GetProperty("GLContext")?.GetValue(window);
            swapBuffers = glContext?.GetType().GetMethod("SwapBuffers", Type.EmptyTypes) ?? throw new Exception("SwapBuffers not found");
        }
        swapBuffers.Invoke(glContext, null);
    }

    // ------------------------------------------------------------ light sources

    /// SOTN function names by address, from the recomp's symbol table, with
    /// the overlay each belongs to: stage overlays share addresses.
    private Dictionary<uint, List<(string overlay, string name)>>? functionNames;

    /// Name fragments of entity update functions that burn: only fire gives
    /// off light (not magic, water or after-images).
    private static readonly string[] FireFunctions =
    {
        "Fire", "Flame", "Explosion", "Hellfire", "Candle", "Torch", "Lamp", "Burn", "Blaze", "Ember", "Lava", "Magma", "Meteor",
        "CtulhuDeath",
    };
    /// Fragments that are not fire even next to one of the above: smoke. The
    /// fiery puffs enemies burst into (ExplosionPuffOpaque) are fire.
    private static readonly string[] NotFire = { "SmokePuff", "GreyPuff", "DeathPuff", "Smoke" };

    private enum LightKind { None, Fire, FireColoured }

    /// Stage object 1 is the breakable: candles (orange or blue flames), and
    /// also vases and busts. The symbol table names it in some stages only.
    private const int BreakableId = 1;
    private static bool IsBreakable(string? name, Entity e)
    {
        // Object 1 means the breakable only among the stage's own objects; the
        // player's and the weapons' objects number theirs apart.
        if (name == null || name.StartsWith("func_", StringComparison.Ordinal)) return e.EntityId == BreakableId && e.Update >= StageOverlayBase;
        int at = name.IndexOf("EntityBreakable", StringComparison.Ordinal);
        if (at < 0) return false;
        string rest = name[(at + "EntityBreakable".Length)..];
        return rest.Length == 0 || rest[0] == '_' || rest.StartsWith("NZ0", StringComparison.Ordinal);
    }

    /// x0, y0, x1, y1, group and remembered-colour kind (-1: none) of each
    /// light source.
    private const int RectFloats = 6;
    private float[] lightRects = new float[64 * RectFloats];
    private int lightCount;

    private Dictionary<uint, List<(string overlay, string name)>> FunctionNames()
    {
        if (functionNames != null) return functionNames;
        functionNames = new();
        try
        {
            var registry = typeof(IMod).Assembly.GetType("RecompOne.Runtime.Modding.SymbolRegistry")!;
            registry.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            var byAddress = (System.Collections.IDictionary)registry.GetField("_byAddress", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            foreach (System.Collections.DictionaryEntry e in byAddress)
            {
                var (overlay, address) = ((string, uint))e.Key;
                if (!functionNames.TryGetValue(address, out var list)) functionNames[address] = list = new();
                list.Add((overlay.ToLowerInvariant(), (e.Value as MethodInfo)?.Name ?? ""));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[crtvision] no symbol table, light sources by blend mode only: " + e.Message);
        }
        return functionNames;
    }

    /// The overlay of the stage being played ("nz0", "st0"...), from its disc file.
    private Stage? overlayStage;
    private string stageOverlay = "";
    private string StageOverlay()
    {
        var stage = Stages.Current;
        if (overlayStage != stage)
        {
            overlayStage = stage;
            try { stageOverlay = Path.GetFileNameWithoutExtension(Stages.DiscFile(stage) ?? "").ToLowerInvariant(); }
            catch { stageOverlay = ""; }
        }
        return stageOverlay;
    }

    /// Stage overlays load from this address on; below it are the game's
    /// shared code, the player's and the weapons'.
    private const uint StageOverlayBase = 0x80180000;

    /// The name of an object's update function in the overlay it runs from,
    /// or null when the symbol table does not name it there.
    private string? FunctionName(uint address)
    {
        if (!FunctionNames().TryGetValue(address, out var list)) return null;
        string? found = null;
        if (address >= StageOverlayBase)
        {
            string stage = StageOverlay();
            foreach (var (overlay, name) in list)
                if (overlay == stage) found = name;
            return found;
        }
        foreach (var (overlay, name) in list)
            if (!IsStageOverlay(overlay)) found = found == null ? name : found + "|" + name;
        return found;
    }

    private static readonly HashSet<string> SharedOverlays = new() { "main", "dra", "ric", "sel", "tt_000", "tt_001", "tt_002", "tt_003", "tt_004" };
    private static bool IsStageOverlay(string overlay) =>
        !SharedOverlays.Contains(overlay) && !overlay.StartsWith("w0", StringComparison.Ordinal) && !overlay.StartsWith("tt_", StringComparison.Ordinal);

    // Only fire gives off light, in three groups:
    //  0 the room's candles and torches: breakables (candles, vases, busts)
    //    while they are additive (PlayStation "B+F") and keep animating (only
    //    a flame flickers, whatever its colour), and objects named Candle,
    //    Torch or Lamp;
    //  1 the fire enemies burst into when destroyed, and the flames a candle
    //    bursts into when broken;
    //  2 every other fire: objects named after fire (flames, fireballs,
    //    explosions, Hellfire), and unnamed stage objects that are additive
    //    and animating or are enemies' projectiles, through their
    //    fire-coloured pixels only.
    // Objects named after anything else (water, after-images, magic) never
    // give off light. A broken candle's flames and an enemy's death fire are
    // the same explosions as an attack's: they are told apart by being born
    // where a candle has just broken or an enemy has just died.
    private const int LightGroups = 3, CandleGroup = 0, DeathGroup = 1, AttackGroup = 2;
    private static readonly string[] GroupTitles =
    {
        "Candles and torches (any flame colour)",
        "Death fire (destroyed enemies and broken candles)",
        "Fire attacks (player and enemies, not magic)",
    };
    private static readonly string[] GroupTags = { "candle", "death", "attack" };
    private LightGroupConfig Group(int g) => g switch { CandleGroup => config.CandleLight, DeathGroup => config.DeathFireLight, _ => config.FireAttackLight };
    private bool AnyLightGroup => config.CandleLight.Enabled || config.DeathFireLight.Enabled || config.FireAttackLight.Enabled;

    private (LightKind kind, int group) GivesLight(int slot, Entity e, string? name)
    {
        string shown = name ?? $"func_{e.Update:X8}";
        if (Matches(shown, config.LightNever)) return (LightKind.None, -1);
        for (int g = 0; g < LightGroups; g++)
            if (Matches(shown, Group(g).Always)) return (LightKind.Fire, g);
        bool flickers = (e.BlendMode & 0x30) == 0x30 && Animating(slot, e);
        // A lit candle stays lit while it lasts (the game stops animating
        // objects as they leave the screen), and a breakable of a kind seen
        // burning in this stage is lit at once, even before it is in view.
        if (IsBreakable(name, e))
        {
            if (flickers) CandleKind(e, learn: true);
            return flickers || tracked[slot].candle || CandleKind(e) >= 0 ? (LightKind.Fire, CandleGroup) : (LightKind.None, -1);
        }
        LightKind kind;
        if (name != null && !name.StartsWith("func_", StringComparison.Ordinal))
        {
            if (!IsFire(name)) return (LightKind.None, -1);
            if (Matches(name, Lamps)) return (LightKind.Fire, CandleGroup);
            kind = LightKind.Fire;
        }
        // Unnamed: a glowing stage object, or an enemy's projectile (it hurts
        // but has no life of its own), through its fire-coloured pixels.
        else if (e.Update >= StageOverlayBase && (flickers || e.EnemyId != 0 && e.HitPoints <= 0)) kind = LightKind.FireColoured;
        else return (LightKind.None, -1);
        return (kind, BornFrom(slot, e));
    }

    private static readonly List<string> Lamps = new() { "Candle", "Torch", "Lamp" };
    /// Per slot: what it held last frame (a lit candle, a living enemy), which
    /// breakable (its params) and where.
    private readonly (bool candle, bool enemy, int param, float x, float y)[] tracked = new (bool, bool, int, float, float)[Entities.Count];
    /// Where and when a lit candle broke or an enemy died, on screen.
    private readonly List<(float x, float y, int at)> brokenCandles = new(), deadEnemies = new();
    /// Per slot: the fire object it holds, the group it was born in, and where,
    /// on which animation frame and when it was last seen.
    private readonly (uint update, int group, int x, int y, int frame, int at)[] born =
        new (uint, int, int, int, int, int)[Entities.Count];
    /// The game reuses a slot as soon as an object ends, often for the same
    /// kind of explosion: a new one is told by its position jumping, its
    /// animation starting over, or a gap since the slot was last seen.
    private const int NewObjectJump = 24;
    private const int BirthWindow = 90, BirthDistanceX = 40;
    /// A broken candle's flames rise from it; an enemy's fire is around where
    /// it stood. Vertical range around the event: (above, below).
    private static readonly (int above, int below) CandleRange = (72, 16), EnemyRange = (72, 40);

    /// Notes the candles that break and the enemies that die this frame.
    private void TrackBirths()
    {
        int now = Volatile.Read(ref vsyncs);
        brokenCandles.RemoveAll(b => now - b.at > BirthWindow);
        deadEnemies.RemoveAll(b => now - b.at > BirthWindow);
        for (int slot = 1; slot < Entities.Count; slot++)
        {
            var e = Entities.At(slot);
            bool alive = e.IsValid && e.EntityId != 0;
            if (!alive) born[slot] = default;
            bool breakable = alive && IsBreakable(FunctionName(e.Update), e);
            ref var was = ref tracked[slot];
            // A lit candle stays one until the breakable goes away: a flame
            // pausing between frames is not a candle breaking.
            bool candle = breakable && (was.candle && was.param == e.Params || Flickered(slot, e) || CandleKind(e) >= 0);
            bool enemy = alive && !breakable && e.EnemyId != 0 && e.HitPoints > 0 && e.HitPoints < short.MaxValue;
            // Objects leaving at the screen's edges scroll out; they do not break or die.
            bool onScreen = was.x >= 8 && was.x <= 248 && was.y >= 0 && was.y <= 240;
            if (was.candle && !breakable && onScreen) brokenCandles.Add((was.x, was.y, now));
            // Scrolled out: the game drops it, but its light still reaches in.
            if (was.candle && !breakable && !onScreen && candleArea[slot].key >= 0) KeepOffScreen(candleArea[slot]);
            if (was.enemy && !enemy && onScreen) deadEnemies.Add((was.x, was.y, now));
            was = (candle, enemy, alive ? e.Params : -1, alive ? e.PosX : 0, alive ? e.PosY : 0);
        }
    }

    /// The group a fire object was born in, decided when it first appears.
    private int BornFrom(int slot, Entity e)
    {
        ref var seen = ref born[slot];
        int now = Volatile.Read(ref vsyncs), frame = e.AnimCurFrame;
        bool reborn = seen.update != e.Update
            || Math.Abs(e.PosX - seen.x) > NewObjectJump || Math.Abs(e.PosY - seen.y) > NewObjectJump
            || frame < seen.frame
            || now - seen.at > 6;
        int group = reborn
            ? Near(brokenCandles, e, CandleRange) || Near(deadEnemies, e, EnemyRange) ? DeathGroup : AttackGroup
            : seen.group;
        seen = (e.Update, group, e.PosX, e.PosY, frame, now);
        return group;
    }

    private static bool Near(List<(float x, float y, int at)> events, Entity e, (int above, int below) range)
    {
        foreach (var b in events)
            if (Math.Abs(b.x - e.PosX) <= BirthDistanceX && e.PosY >= b.y - range.above && e.PosY <= b.y + range.below) return true;
        return false;
    }

    /// Whether the object in this slot has been seen flickering lately.
    private bool Flickered(int slot, Entity e)
    {
        var seen = animSeen[slot];
        return seen.update == e.Update && seen.changes >= AnimChanges && Volatile.Read(ref vsyncs) - seen.changed <= AnimWindow;
    }

    private bool Matches(string name, List<string>? fragments)
    {
        if (fragments == null) return false;
        foreach (var entry in fragments)
        {
            string part = entry.Trim();
            int colon = part.IndexOf(':');
            if (colon >= 0)
            {
                if (!string.Equals(part[..colon].Trim(), StageOverlay(), StringComparison.OrdinalIgnoreCase)) continue;
                part = part[(colon + 1)..].Trim();
            }
            if (part.Length > 0 && name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsFire(string name)
    {
        foreach (var part in NotFire)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in FireFunctions)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// Per slot: which object it holds, its last animation frame, how many
    /// times that changed recently and the game frame it last changed.
    private readonly (uint update, int param, int frame, int changes, int changed)[] animSeen =
        new (uint, int, int, int, int)[Entities.Count];
    /// A flame changes frame several times a second; a still object never.
    private const int AnimWindow = 45;
    /// Changes seen before an object counts as animated: a still object can
    /// change once or twice while it is being set up as it scrolls in.
    private const int AnimChanges = 4;

    private bool Animating(int slot, Entity e)
    {
        int now = Volatile.Read(ref vsyncs);
        int frame = (e.AnimCurFrame << 16) ^ e.Pose;
        ref var seen = ref animSeen[slot];
        if (seen.update != e.Update || seen.param != e.Params)
        {
            // A new object in this slot: not proven to animate yet.
            seen = (e.Update, e.Params, frame, 0, now);
            return false;
        }
        if (seen.frame != frame)
        {
            // Changes far apart are not a flicker: count again.
            int changes = now - seen.changed > AnimWindow ? 1 : Math.Min(seen.changes + 1, AnimChanges);
            seen = (e.Update, e.Params, frame, changes, now);
        }
        return seen.changes >= AnimChanges && now - seen.changed <= AnimWindow;
    }

    /// Collects the areas of the objects that give off light this frame, in
    /// the native picture's pixels, and hands them to the light pass.
    private void UpdateLights(int pictureWidth)
    {
        lightCount = 0;
        if (AnyLightGroup || config.DebugLights)
        {
            try
            {
                if (Game.Available && Game.InGame && !Game.IsLoading)
                {
                    float sx = pictureWidth / 256f;
                    if (config.DebugLights) labelCount = 0;
                    // The camera is in 16.16 fixed point.
                    var camera = (x: Game.CameraX >> 16, y: Game.CameraY >> 16);
                    TrackBirths();
                    for (int slot = 1; slot < Entities.Count && lightCount < 64; slot++)
                    {
                        var e = Entities.At(slot);
                        if (!e.IsValid || e.EntityId == 0) continue;
                        string? fn = FunctionName(e.Update);
                        var (kind, group) = GivesLight(slot, e, fn);
                        bool lit = kind != LightKind.None && Group(group).Enabled;
                        if (config.DebugLights) NoteObject(e, fn, kind, group, lit, sx);
                        if (kind == LightKind.None) continue;
                        float cx = e.PosX + e.HitboxOffX, cy = e.PosY + e.HitboxOffY;
                        float hw = e.HitboxWidth > 0 ? e.HitboxWidth : 12, hh = e.HitboxHeight > 0 ? e.HitboxHeight : 12;
                        bool candle = IsBreakable(fn, e);
                        int key = candle ? CandleKind(e) : -1;
                        if (candle) candleArea[slot] = (cx + camera.x, cy + camera.y, hw, hh, key, CurrentRoom());
                        if (!lit) continue;
                        // Explosions draw far past their small hitbox.
                        if (kind == LightKind.Fire && !candle) (hw, hh) = (Math.Max(hw, 12), Math.Max(hh, 12));
                        // A lightning bolt has no hitbox and is drawn well around its
                        // position; it only exists for the flash (about 12 frames).
                        if (fn != null && fn.Contains("LightningThunder", StringComparison.Ordinal)) (hw, hh) = (48, 44);
                        AddLight(cx, cy, hw, hh, kind, group, key, sx, pictureWidth, candle);
                    }
                    if (config.CandleLight.Enabled) LightOffScreen(camera, sx, pictureWidth);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[crtvision] light sources: " + e.Message);
            }
        }
        setLightRects!(ctx, lightRects, (uint)lightCount);
        UpdateHud(pictureWidth);
    }

    // The HUD's gauges. They take light like everything else, but a gauge
    // drawn over a candle's flame must not become a light source itself: what
    // is drawn there only emits with the flame's own hue. Native pixels, 256
    // wide.
    private static readonly (float x0, float y0, float x1, float y1)[] AlucardHud = { (0, 18, 76, 44) };
    private static readonly (float x0, float y0, float x1, float y1)[] RichterHud = { (3, 20, 100, 41), (3, 20, 13, 118) };
    /// The boss's life gauge, shown while a boss is fighting.
    private static readonly (float x0, float y0, float x1, float y1) RichterBossGauge = (227, 20, 246, 118);
    private readonly float[] hudRects = new float[8 * 4];

    /// A boss is on the field: an enemy far tougher than the castle's others.
    private static bool BossFighting()
    {
        for (int slot = 1; slot < Entities.Count; slot++)
        {
            var e = Entities.At(slot);
            if (e.IsValid && e.EntityId != 0 && e.EnemyId != 0 && e.HitPoints >= 200 && e.HitPoints < short.MaxValue) return true;
        }
        return false;
    }

    private void UpdateHud(int pictureWidth)
    {
        int count = 0;
        try
        {
            if (AnyLightGroup && Game.Available && Game.InGame && !Game.IsLoading)
            {
                float sx = pictureWidth / 256f;
                // The prologue plays Richter while the game still reports Alucard.
                bool richter = Game.InPrologue() || Game.Character == PlayableCharacter.Richter;
                var hud = richter ? RichterHud : AlucardHud;
                if (richter && BossFighting()) hud = hud.Append(RichterBossGauge).ToArray();
                foreach (var r in hud)
                {
                    int i = count++ * 4;
                    (hudRects[i], hudRects[i + 1], hudRects[i + 2], hudRects[i + 3]) = (r.x0 * sx, r.y0, r.x1 * sx, r.y1);
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[crtvision] light HUD: " + e.Message);
        }
        setLightHud!(ctx, hudRects, (uint)count);
    }

    /// The light pass works this far around the picture (native pixels).
    private const int LightMargin = 128;

    private void AddLight(float cx, float cy, float hw, float hh, LightKind kind, int group, int key, float sx, int pictureWidth, bool candle = false)
    {
        if (lightCount >= 64) return;
        const float margin = 4;
        float x0 = (cx - hw - margin) * sx, x1 = (cx + hw + margin) * sx;
        float y0 = cy - hh - margin + config.LightOffsetY, y1 = cy + hh + margin + config.LightOffsetY;
        // Tall candelabras burn in a band around the top of their hitbox: only
        // that band emits, so what passes in front of the stem (a character)
        // does not.
        if (candle && hh > 16) (y0, y1) = (cy - hh - 8 + config.LightOffsetY, cy - hh + 8 + config.LightOffsetY);
        // Other tall objects burn at the top: only that half emits.
        else if (hh > 16) y1 = cy + config.LightOffsetY;
        if (x1 < -LightMargin * sx || y1 < -LightMargin || x0 > pictureWidth + LightMargin * sx || y0 > 240 + LightMargin) return;
        int i = lightCount++ * RectFloats;
        // Given right to left: only its fire-coloured pixels emit.
        if (kind == LightKind.FireColoured) (x0, x1) = (x1, x0);
        (lightRects[i], lightRects[i + 1], lightRects[i + 2], lightRects[i + 3], lightRects[i + 4], lightRects[i + 5]) = (x0, y0, x1, y1, group, key);
    }

    // Candles off screen. The game drops objects that scroll out of view and
    // creates them again just as they come back, so the mod remembers each lit
    // candle where it stands in the room (screen position plus camera) and
    // keeps its light shining in from off screen; and remembers each kind of
    // candle seen burning in the stage (its params), so one of that kind is lit
    // at once, with that kind's flame colour, even before it is in view.
    private readonly Dictionary<(string stage, int param), int> candleKinds = new();
    private string CurrentRoom() => $"{StageOverlay()}:{Game.Room}";

    /// The kind of a breakable seen burning in this stage (0 to 255), or -1.
    private int CandleKind(Entity e, bool learn = false)
    {
        var id = (StageOverlay(), (int)e.Params);
        if (candleKinds.TryGetValue(id, out int key)) return key;
        if (!learn) return -1;
        key = candleKinds.Count % 256;
        candleKinds[id] = key;
        return key;
    }

    /// Per slot: where its candle stands in the room, its size, kind and room.
    private readonly (float wx, float wy, float hw, float hh, int key, string room)[] candleArea =
        new (float, float, float, float, int, string)[Entities.Count];
    /// Lit candles that scrolled out of view.
    private readonly List<(float wx, float wy, float hw, float hh, int key, string room)> offScreen = new();

    private void KeepOffScreen((float wx, float wy, float hw, float hh, int key, string room) candle)
    {
        offScreen.RemoveAll(c => Math.Abs(c.wx - candle.wx) < 8 && Math.Abs(c.wy - candle.wy) < 8);
        offScreen.Add(candle);
    }

    private void LightOffScreen((int x, int y) camera, float sx, int pictureWidth)
    {
        string room = CurrentRoom();
        offScreen.RemoveAll(c =>
        {
            float x = c.wx - camera.x, y = c.wy - camera.y;
            if (c.room != room || x < -LightMargin - 64 || x > 256 + LightMargin + 64 || y < -LightMargin - 64 || y > 240 + LightMargin + 64) return true;
            // Back in view: the game has created it again.
            for (int slot = 1; slot < Entities.Count; slot++)
            {
                var a = candleArea[slot];
                if (tracked[slot].candle && a.room == room && Math.Abs(a.wx - c.wx) < 8 && Math.Abs(a.wy - c.wy) < 8) return true;
            }
            return false;
        });
        foreach (var c in offScreen)
            AddLight(c.wx - camera.x, c.wy - camera.y, c.hw, c.hh, LightKind.Fire, CandleGroup, c.key, sx, pictureWidth, candle: true);
    }

    // Debug view: every object's name where it stands, and a list of the kinds
    // of objects met so far (light-objects.txt), to tell which ones should or
    // should not give off light.
    private readonly (float x, float y, string text, uint colour)[] labels = new (float, float, string, uint)[Entities.Count];
    /// Debug colours by group (ImGui ABGR): candles yellow, death fire orange, attacks red.
    private static readonly uint[] GroupColours = { 0xFF00FFFF, 0xFF0090FF, 0xFF4050FF };
    private const uint UnlitColour = 0xFFC8C8C8;
    private int labelCount;
    private readonly HashSet<string> objectsListed = new();

    private void NoteObject(Entity e, string? fn, LightKind kind, int group, bool lit, float sx)
    {
        string name = fn ?? $"func_{e.Update:X8}";
        if (e.PosX >= -8 && e.PosX <= 264 && e.PosY >= 0 && e.PosY <= 248 && labelCount < labels.Length)
        {
            string shortName = name.StartsWith("Entity", StringComparison.Ordinal) ? name[6..] : name;
            if (kind != LightKind.None) shortName += $" [{GroupTags[group]}{(lit ? "" : ", off")}]";
            labels[labelCount++] = (e.PosX * sx, e.PosY + config.LightOffsetY, shortName, lit ? GroupColours[group] : UnlitColour);
        }
        // Listed again when its group changes (a candle joins one once its
        // flame has flickered for a moment).
        string stage = StageOverlay();
        if (!objectsListed.Add($"{stage} {name} {group}")) return;
        try
        {
            string path = Path.Combine(root, "light-objects.txt");
            if (objectsListed.Count == 1)
                File.AppendAllText(path, $"# {DateTime.Now:yyyy-MM-dd HH:mm} stage | name | group | additive\n");
            bool additive = (e.BlendMode & 0x30) == 0x30;
            string light = kind == LightKind.None ? "none" : GroupTags[group] + (kind == LightKind.FireColoured ? " (fire colours)" : "");
            File.AppendAllText(path, $"{stage} | {name} | {light} | {(additive ? "yes" : "no")}\n");
        }
        catch { }
    }

    /// Debug: outlines of the light areas over the picture and object names.
    private void DrawLightAreas(ImDrawListPtr list)
    {
        int lines = lastRender.h - (int)lastRender.top - (int)lastRender.bottom;
        if (lastRender.w <= 0 || lines <= 0) return;
        float kx = outSize.X / lastRender.w, ky = outSize.Y / lines;
        // Readable at any resolution: about 1/45 of the picture's height.
        float fontSize = Math.Max(16f, outSize.Y / 45f), scale = fontSize / ImGui.GetFontSize();
        for (int k = 0; k < labelCount; k++)
        {
            var (x, y, text, colour) = labels[k];
            var at = outPos + new Vector2(x * kx, (y - lastRender.top) * ky);
            var size = ImGui.CalcTextSize(text) * scale;
            at -= new Vector2(size.X / 2, size.Y);
            list.AddRectFilled(at - new Vector2(3, 1), at + size + new Vector2(3, 1), 0xB0000000);
            list.AddText(ImGui.GetFont(), fontSize, at, colour, text);
        }
        for (int k = 0; k < lightCount; k++)
        {
            int i = k * RectFloats;
            float x0 = Math.Min(lightRects[i], lightRects[i + 2]), x1 = Math.Max(lightRects[i], lightRects[i + 2]);
            var a = outPos + new Vector2(x0 * kx, (lightRects[i + 1] - lastRender.top) * ky);
            var b = outPos + new Vector2(x1 * kx, (lightRects[i + 3] - lastRender.top) * ky);
            list.AddRect(a, b, GroupColours[(int)lightRects[i + 4]], 0, ImDrawFlags.None, 3f);
        }
    }

    // ------------------------------------------------------------ preset browser

    private bool browserRequested;
    private const string BrowserId = "Load a shader preset (.slangp)###crtvision-browser";
    private string browserDir = "";
    private List<string> browserDirs = new(), browserFiles = new();
    private string browserError = "";

    /// Shortcuts: the mod's presets, RetroArch's shader folders that exist,
    /// and the folders of the current and default presets.
    private IEnumerable<(string label, string path)> BrowserPlaces()
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        var places = new List<(string, string)>
        {
            ("Mod presets", Path.Combine(root, "presets")),
            ("Current", Path.GetDirectoryName(config.Preset) ?? ""),
            ("Default", Path.GetDirectoryName(config.DefaultPreset) ?? ""),
            ("RetroArch", Path.Combine(home, ".config/retroarch/shaders")),
            ("RetroArch (Flatpak)", Path.Combine(home, ".var/app/org.libretro.RetroArch/config/retroarch/shaders")),
            ("RetroArch (Snap)", Path.Combine(home, "snap/retroarch/current/.config/retroarch/shaders")),
            ("Home", home),
        };
        var seen = new HashSet<string>();
        foreach (var (label, path) in places)
            if (path.Length > 0 && Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                yield return (label, path);
    }

    private void OpenBrowser()
    {
        string start = Directory.Exists(config.BrowseFolder) ? config.BrowseFolder : Path.GetDirectoryName(config.Preset) ?? "/";
        BrowseTo(Directory.Exists(start) ? start : "/");
        browserRequested = true;
    }

    private void BrowseTo(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            browserDirs = info.EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).Select(d => d.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            browserFiles = info.EnumerateFiles("*.slangp").Select(f => f.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            browserDir = info.FullName;
            browserError = "";
        }
        catch (Exception e)
        {
            browserError = e.Message;
        }
    }

    /// A modal popup opened from the mod's settings, so it is stacked on top
    /// of the recomp's Mods window instead of behind it.
    private void DrawBrowser()
    {
        if (browserRequested)
        {
            ImGui.OpenPopup(BrowserId);
            browserRequested = false;
        }
        var io = ImGui.GetIO();
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(1000, io.DisplaySize.X * 0.8f), MathF.Min(700, io.DisplaySize.Y * 0.8f)), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(io.DisplaySize / 2, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        bool open = true;
        if (ImGui.BeginPopupModal(BrowserId, ref open, ImGuiWindowFlags.NoCollapse))
        {
            foreach (var (label, path) in BrowserPlaces())
            {
                if (ImGui.Button(label)) BrowseTo(path);
                ImGui.SameLine();
            }
            ImGui.NewLine();
            if (ImGui.Button("Up") && Directory.GetParent(browserDir) is { } parent) BrowseTo(parent.FullName);
            ImGui.SameLine();
            ImGui.TextWrapped(browserDir);
            if (browserError.Length > 0) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), browserError);
            ImGui.Separator();
            if (ImGui.BeginChild("##entries", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2)))
            {
                string? enter = null, pick = null;
                foreach (var d in browserDirs)
                    if (ImGui.Selectable("[" + d + "]", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        enter = d;
                foreach (var f in browserFiles)
                    if (ImGui.Selectable(f, false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        pick = f;
                if (enter != null) BrowseTo(Path.Combine(browserDir, enter));
                if (pick != null)
                {
                    pendingPreset = Path.Combine(browserDir, pick);
                    config.BrowseFolder = browserDir;
                    SaveConfig();
                    open = false;
                }
            }
            ImGui.EndChild();
            ImGui.TextDisabled("Double-click a folder to open it and a preset to load it. Presets load exactly as configured;");
            ImGui.TextDisabled("Mega Bezel presets with their own screen placement get the whole window. Only .slang presets work.");
            if (!open) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    // ------------------------------------------------------------ state socket

    /// CRT-Vision's cinema mode looks for a live control socket while a game
    /// runs; the Vulkan layer is not loaded here, so the mod answers instead.
    private void StartStateSocket()
    {
        string? dir = Environment.GetEnvironmentVariable("VKSLANG_SOCKET_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        socketPath = Path.Combine(dir, $"{Environment.ProcessId}.sock");
        try
        {
            File.Delete(socketPath);
            stateSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            stateSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
            stateSocket.Listen(4);
            var thread = new Thread(() =>
            {
                var reply = Encoding.UTF8.GetBytes("{\"type\":\"state\",\"process\":\"sotn\",\"mod\":\"crtvision\"}\n");
                while (stateSocket != null)
                {
                    try
                    {
                        using var client = stateSocket.Accept();
                        var buf = new byte[256];
                        client.ReceiveTimeout = 500;
                        client.Receive(buf);
                        client.Send(reply);
                    }
                    catch { if (stateSocket == null) break; }
                }
            }) { IsBackground = true, Name = "crtvision-state" };
            thread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopStateSocket();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[crtvision] state socket: {e.Message}");
        }
    }

    private void StopStateSocket()
    {
        var s = stateSocket;
        stateSocket = null;
        try { s?.Close(); } catch { }
        if (socketPath != null) try { File.Delete(socketPath); } catch { }
    }
}
