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
    public Dictionary<string, float> Params { get; set; } = new();
    /// Lines the game leaves black above and below its gameplay picture;
    /// cropped (and the picture fills the height) only while they are black.
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
    /// Emissive light: bright pixels (candles, torches, magic) light up their
    /// surroundings, on the native picture before the CRT preset.
    public bool Glow { get; set; } = true;
    /// Strength of the light, 0 to 2.
    public float GlowIntensity { get; set; } = 0.53f;
    /// Brightness from which a pixel gives off light, 0.5 to 1.
    public float GlowThreshold { get; set; } = 0.95f;
    /// How far the light reaches, in PSX pixels.
    public float GlowRadius { get; set; } = 3.2f;
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint InterpolateFn(IntPtr ctx, uint outW, uint outH);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetGlowFn(IntPtr ctx, [MarshalAs(UnmanagedType.I1)] bool enabled, float intensity, float threshold, float radius);
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
    private InterpolateFn? interpolate;
    private PresentFn? present;
    private SetGlowFn? setGlow;
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
    }

    public void DrawSettings()
    {
        bool enabled = config.Enabled;
        if (ImGui.Checkbox("CRT enabled (fullscreen)", ref enabled))
        {
            config.Enabled = enabled;
            SaveConfig();
        }
        float mask = config.Params.TryGetValue("maskstr", out var m) ? m : 0.3f;
        if (ImGui.SliderFloat("Mask strength", ref mask, 0f, 1f, "%.2f"))
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
        bool glowOn = config.Glow;
        if (ImGui.Checkbox("Emissive light (candles, torches, magic)", ref glowOn))
        {
            config.Glow = glowOn;
            ApplyGlow();
            SaveConfig();
        }
        if (!config.Glow) ImGui.BeginDisabled();
        float glowIntensity = config.GlowIntensity, glowThreshold = config.GlowThreshold, glowRadius = config.GlowRadius;
        bool glowChanged = ImGui.SliderFloat("Light intensity", ref glowIntensity, 0f, 2f, "%.2f");
        glowChanged |= ImGui.SliderFloat("Threshold (what glows)", ref glowThreshold, 0.5f, 1f, "%.2f");
        glowChanged |= ImGui.SliderFloat("Radius (reach)", ref glowRadius, 1f, 16f, "%.1f px");
        if (!config.Glow) ImGui.EndDisabled();
        if (glowChanged)
        {
            (config.GlowIntensity, config.GlowThreshold, config.GlowRadius) = (glowIntensity, glowThreshold, glowRadius);
            ApplyGlow();
            SaveConfig();
        }
        bool integer = config.IntegerScale;
        if (ImGui.Checkbox("Integer vertical scale", ref integer))
        {
            config.IntegerScale = integer;
            SaveConfig();
        }
        ImGui.TextDisabled(Path.GetFileName(config.Preset));
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
        config.Preset = Environment.ExpandEnvironmentVariables(config.Preset.Replace("~", Environment.GetEnvironmentVariable("HOME") ?? "~"));
        // A relative preset (the shared package) lives inside the mod folder.
        if (config.Preset.Length > 0 && !Path.IsPathRooted(config.Preset)) config.Preset = Path.Combine(root, config.Preset);
    }

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
        interpolate = Export<InterpolateFn>("cvgl_interpolate");
        present = Export<PresentFn>("cvgl_present");
        setGlow = Export<SetGlowFn>("cvgl_set_glow");
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
        foreach (var (name, value) in config.Params) setParam!(ctx, Utf8(name), value);
        ApplyGlow();
        status = $"{Path.GetFileName(config.Preset)} compiled in {(DateTime.Now - started).TotalMilliseconds:0} ms";
        Console.WriteLine("[crtvision] " + status);
        return true;
    }

    private void ApplyGlow()
    {
        if (ctx == IntPtr.Zero) return;
        setGlow!(ctx, config.Glow, config.GlowIntensity, config.GlowThreshold, config.GlowRadius);
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

    private void OnVSync(VSyncEvent e) => Interlocked.Increment(ref vsyncs);

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

            uint crop = analyze!(ctx, id, (uint)w, (uint)h, (uint)config.CropTop, (uint)config.CropBottom, (uint)config.CropLines);
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
            var size = new Vector2(width, height);

            // One run per game frame: the host may redraw faster than the PSX.
            int frame = Volatile.Read(ref vsyncs);
            bool same = lastRender == (id, w, h, top, bottom, size);
            if (same && frame == renderedVsync && outTex != 0) diagRepeat++;
            if (!same || frame != renderedVsync || outTex == 0)
            {
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
