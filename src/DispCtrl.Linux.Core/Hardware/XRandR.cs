using System.Runtime.InteropServices;

namespace DispCtrl.Linux.Hardware;

/// <summary>One output as RandR reports it: the layout DispCtrl's CCD code
/// reads on Windows.</summary>
/// <param name="WidthMm">The panel's size, unrotated, as its EDID gives it;
/// zero when it gives none.</param>
/// <param name="Rotation">Degrees counter-clockwise: 90 is xrandr's "left".</param>
public sealed record XRandROutput(
    string Name,
    bool Connected,
    bool Primary,
    int WidthPx,
    int HeightPx,
    int PosX,
    int PosY,
    int WidthMm,
    int HeightMm,
    int Rotation = 0,
    double RefreshHz = 0)
{
    /// <summary>Lit and part of the desktop: connected with a mode. A
    /// connected output switched off has no geometry.</summary>
    public bool Active => Connected && WidthPx > 0 && HeightPx > 0;
}

/// <summary>A mode an output offers.</summary>
public sealed record XRandRMode(ulong Id, int Width, int Height, double RefreshHz, bool Preferred);

/// <summary>What one output is to become: lit with a mode at a place and a
/// turn, or switched off.</summary>
/// <param name="Rotation">Degrees counter-clockwise, as <see cref="XRandROutput.Rotation"/>.</param>
public sealed record OutputConfig(string Name, bool Enabled, int Width = 0, int Height = 0, double RefreshHz = 0,
    int X = 0, int Y = 0, int Rotation = 0)
{
    /// <summary>Its size on the desktop: a turned output swaps its sides.</summary>
    public (int W, int H) Extent => Rotation is 90 or 270 ? (Height, Width) : (Width, Height);
}

/// <summary>Reads and moves the X desktop's layout through libXrandr.</summary>
/// <remarks>
/// The library rather than the <c>xrandr</c> program: libXrandr is already
/// needed for the ramps, while the program is a separate package with a
/// different name on every distribution (x11-xserver-utils, xorg-xrandr,
/// xrandr), and missing from many minimal installs. Opens the same display
/// <see cref="GammaRamp"/> does, so the checks' unreachable display keeps
/// <see cref="SetPositions"/> off the real desk too.
/// </remarks>
public static unsafe class XRandR
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";
    private const ushort RRConnected = 0;
    private const int RRSetConfigSuccess = 0;
    private const ulong ModeFlagInterlace = 0x10, ModeFlagDoubleScan = 0x20;

    /// <summary>Whether an X server with RandR answers.</summary>
    public static bool IsAvailable => GammaRamp.IsAvailable(out _);

    /// <summary>Every output, connected or not, in the server's order. Empty
    /// when there is no X server.</summary>
    public static IReadOnlyList<XRandROutput> Query()
    {
        var results = new List<XRandROutput>();
        GammaRamp.WithDisplay(display =>
        {
            var root = XDefaultRootWindow(display);
            var resources = XRRGetScreenResourcesCurrent(display, root);
            if (resources == null) return;
            ulong primary = XRRGetOutputPrimary(display, root);
            try
            {
                for (int i = 0; i < resources->noutput; i++)
                {
                    var info = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                    if (info == null) continue;
                    try
                    {
                        string name = Marshal.PtrToStringUTF8((nint)info->name, info->nameLen);
                        if (info->connection != RRConnected)
                        {
                            results.Add(new XRandROutput(name, false, false, 0, 0, 0, 0, 0, 0));
                            continue;
                        }
                        int w = 0, h = 0, x = 0, y = 0, rotation = 0;
                        double hz = 0;
                        if (info->crtc != 0 && XRRGetCrtcInfo(display, resources, info->crtc) is var crtc && crtc != null)
                        {
                            try
                            {
                                if (crtc->mode != 0)
                                {
                                    (x, y, w, h) = (crtc->x, crtc->y, (int)crtc->width, (int)crtc->height);
                                    rotation = Degrees(crtc->rotation);
                                    hz = RefreshOf(resources, crtc->mode);
                                }
                            }
                            finally { XRRFreeCrtcInfo(crtc); }
                        }
                        results.Add(new XRandROutput(name, true, resources->outputs[i] == primary && w > 0,
                            w, h, x, y, (int)info->mm_width, (int)info->mm_height, rotation, hz));
                    }
                    finally { XRRFreeOutputInfo(info); }
                }
            }
            finally { XRRFreeScreenResources(resources); }
        });
        return results;
    }

    /// <summary>Each connected output's monitor name from its EDID, such as
    /// "DELL P2723DE"; outputs whose EDID names nothing are left out.</summary>
    public static Dictionary<string, string> MonitorNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        GammaRamp.WithDisplay(display =>
        {
            ulong edidAtom = X11.Xlib.XInternAtom(display, "EDID", 1);
            if (edidAtom == 0) return;
            var resources = XRRGetScreenResourcesCurrent(display, XDefaultRootWindow(display));
            if (resources == null) return;
            try
            {
                for (int i = 0; i < resources->noutput; i++)
                {
                    var info = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                    if (info == null) continue;
                    try
                    {
                        if (info->connection != RRConnected) continue;
                        string name = Marshal.PtrToStringUTF8((nint)info->name, info->nameLen);
                        // 128 32-bit units: the base block and its extensions.
                        if (XRRGetOutputProperty(display, resources->outputs[i], edidAtom, 0, 128, 0, 0, 0,
                                out _, out int format, out ulong items, out _, out byte* data) != 0 || data == null) continue;
                        try
                        {
                            if (format == 8 && EdidName(new ReadOnlySpan<byte>(data, (int)items).ToArray()) is { } model)
                                names[name] = model;
                        }
                        finally { X11.Xlib.XFree((nint)data); }
                    }
                    finally { XRRFreeOutputInfo(info); }
                }
            }
            finally { XRRFreeScreenResources(resources); }
        });
        return names;
    }

    /// <summary>
    /// Moves outputs to the given desktop positions, and makes
    /// <paramref name="primary"/> the primary one when given, in one change
    /// with the server grabbed.
    /// </summary>
    /// <remarks>
    /// What <c>xrandr --pos</c> does: the screen first grows to hold both the
    /// old and the new layout, every CRTC moves (keeping its mode, rotation
    /// and outputs), and then the screen shrinks to the new layout. A CRTC may
    /// never lie outside the screen, so moving first or shrinking first is
    /// refused by the server.
    /// </remarks>
    public static bool SetPositions(IReadOnlyDictionary<string, (int X, int Y)> positions, string? primary, out string? error)
    {
        string? failure = "no X display";
        GammaRamp.WithDisplay(display => failure = SetPositions(display, positions, primary));
        error = failure;
        return error is null;
    }

    private static string? SetPositions(nint display, IReadOnlyDictionary<string, (int X, int Y)> positions, string? primary)
    {
        var root = XDefaultRootWindow(display);
        XGrabServer(display);
        var resources = XRRGetScreenResourcesCurrent(display, root);
        try
        {
            if (resources == null) return "RandR gave no screen resources";

            // Every lit CRTC, where it is and where it is going.
            var crtcs = new List<(ulong Id, nint Info, int X, int Y, int W, int H, string Name)>();
            var outputIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
            try
            {
                for (int i = 0; i < resources->noutput; i++)
                {
                    var info = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                    if (info == null) continue;
                    string name = Marshal.PtrToStringUTF8((nint)info->name, info->nameLen);
                    outputIds[name] = resources->outputs[i];
                    ulong crtcId = info->crtc;
                    XRRFreeOutputInfo(info);
                    if (crtcId == 0 || crtcs.Any(c => c.Id == crtcId)) continue;
                    var crtc = XRRGetCrtcInfo(display, resources, crtcId);
                    if (crtc == null) continue;
                    if (crtc->mode == 0) { XRRFreeCrtcInfo(crtc); continue; }
                    var (x, y) = positions.TryGetValue(name, out var to) ? to : (crtc->x, crtc->y);
                    crtcs.Add((crtcId, (nint)crtc, x, y, (int)crtc->width, (int)crtc->height, name));
                }

                foreach (var name in positions.Keys)
                    if (!crtcs.Any(c => c.Name == name)) return $"{name} is not lit";
                if (primary is not null && !outputIds.ContainsKey(primary)) return $"no output '{primary}'";

                int screen = XDefaultScreen(display);
                int oldW = XDisplayWidth(display, screen), oldH = XDisplayHeight(display, screen);
                int oldMmW = XDisplayWidthMM(display, screen), oldMmH = XDisplayHeightMM(display, screen);
                var (newW, newH) = ScreenFor(crtcs.Select(c => (c.X, c.Y, c.W, c.H)));
                XRRGetScreenSizeRange(display, root, out int minW, out int minH, out int maxW, out int maxH);
                if (newW > maxW || newH > maxH) return $"the layout needs a {newW}x{newH} screen, and this one holds {maxW}x{maxH}";
                newW = Math.Max(newW, minW);
                newH = Math.Max(newH, minH);
                int growW = Math.Max(oldW, newW), growH = Math.Max(oldH, newH);

                if ((growW, growH) != (oldW, oldH)) SetScreen(display, root, growW, growH, oldW, oldH, oldMmW, oldMmH);
                foreach (var c in crtcs)
                {
                    var info = (XRRCrtcInfo*)c.Info;
                    if ((info->x, info->y) == (c.X, c.Y)) continue;
                    int status = XRRSetCrtcConfig(display, resources, c.Id, 0, c.X, c.Y, info->mode, info->rotation,
                        info->outputs, info->noutput);
                    if (status != RRSetConfigSuccess) return $"X refused to move {c.Name} (status {status})";
                }
                if ((newW, newH) != (growW, growH)) SetScreen(display, root, newW, newH, oldW, oldH, oldMmW, oldMmH);
                if (primary is not null) XRRSetOutputPrimary(display, root, outputIds[primary]);
                return null;
            }
            finally
            {
                foreach (var c in crtcs) XRRFreeCrtcInfo((XRRCrtcInfo*)c.Info);
            }
        }
        finally
        {
            if (resources != null) XRRFreeScreenResources(resources);
            XUngrabServer(display);
            XSync(display, 0);
        }
    }

    /// <summary>Every mode each connected output offers, best first: the
    /// largest, then the fastest.</summary>
    public static Dictionary<string, List<XRandRMode>> Modes()
    {
        var all = new Dictionary<string, List<XRandRMode>>(StringComparer.Ordinal);
        GammaRamp.WithDisplay(display =>
        {
            var resources = XRRGetScreenResourcesCurrent(display, XDefaultRootWindow(display));
            if (resources == null) return;
            try
            {
                for (int i = 0; i < resources->noutput; i++)
                {
                    var info = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                    if (info == null) continue;
                    try
                    {
                        if (info->connection != RRConnected) continue;
                        string name = Marshal.PtrToStringUTF8((nint)info->name, info->nameLen);
                        var modes = new List<XRandRMode>();
                        for (int m = 0; m < info->nmode; m++)
                        {
                            if (Mode(resources, info->modes[m]) is not { } mode) continue;
                            if ((mode.modeFlags & ModeFlagInterlace) != 0) continue;
                            modes.Add(new XRandRMode(mode.id, (int)mode.width, (int)mode.height, RefreshOf(resources, mode.id), m < info->npreferred));
                        }
                        all[name] = modes.OrderByDescending(x => (long)x.Width * x.Height).ThenByDescending(x => x.RefreshHz).ToList();
                    }
                    finally { XRRFreeOutputInfo(info); }
                }
            }
            finally { XRRFreeScreenResources(resources); }
        });
        return all;
    }

    /// <summary>
    /// Puts each output in <paramref name="plan"/> into its new state in one
    /// change with the server grabbed; outputs not in the plan are untouched.
    /// <paramref name="primary"/>, when given, becomes the main display.
    /// </summary>
    /// <remarks>
    /// xrandr's order, which the server's rule forces (no CRTC may lie outside
    /// the screen at any moment): the CRTCs that change are switched off
    /// first, the screen takes its new size, and they come back in their new
    /// modes and places. An output being lit that has no CRTC takes a free one
    /// it can drive.
    /// </remarks>
    public static bool Configure(IReadOnlyList<OutputConfig> plan, string? primary, out string? error)
    {
        string? failure = "no X display";
        GammaRamp.WithDisplay(display => failure = Configure(display, plan, primary));
        error = failure;
        return error is null;
    }

    private static string? Configure(nint display, IReadOnlyList<OutputConfig> plan, string? primary)
    {
        var root = XDefaultRootWindow(display);
        XGrabServer(display);
        var resources = XRRGetScreenResourcesCurrent(display, root);
        var crtcInfos = new Dictionary<ulong, nint>();
        try
        {
            if (resources == null) return "RandR gave no screen resources";
            XRRCrtcInfo* Crtc(ulong id)
            {
                if (!crtcInfos.TryGetValue(id, out var p)) crtcInfos[id] = p = (nint)XRRGetCrtcInfo(display, resources, id);
                return (XRRCrtcInfo*)p;
            }

            // Every output: its id, CRTC now, the CRTCs it may use and its modes.
            var outputs = new Dictionary<string, (ulong Id, ulong Crtc, ulong[] Possible, ulong[] Modes)>(StringComparer.Ordinal);
            for (int i = 0; i < resources->noutput; i++)
            {
                var info = XRRGetOutputInfo(display, resources, resources->outputs[i]);
                if (info == null) continue;
                try
                {
                    string name = Marshal.PtrToStringUTF8((nint)info->name, info->nameLen);
                    var possible = new ulong[info->ncrtc];
                    for (int c = 0; c < info->ncrtc; c++) possible[c] = info->crtcs[c];
                    var modes = new ulong[info->nmode];
                    for (int m = 0; m < info->nmode; m++) modes[m] = info->modes[m];
                    outputs[name] = (resources->outputs[i], info->crtc, possible, modes);
                }
                finally { XRRFreeOutputInfo(info); }
            }

            var used = new HashSet<ulong>(outputs.Values.Where(o => o.Crtc != 0).Select(o => o.Crtc));
            var targets = new List<(ulong Crtc, ulong Output, ulong Mode, ushort Rotation, OutputConfig Config)>();
            var off = new List<ulong>();
            foreach (var config in plan)
            {
                if (!outputs.TryGetValue(config.Name, out var o)) return $"no output '{config.Name}'";
                if (!config.Enabled)
                {
                    if (o.Crtc != 0) off.Add(o.Crtc);
                    continue;
                }
                ulong mode = PickMode(resources, o.Modes, config.Width, config.Height, config.RefreshHz);
                if (mode == 0) return $"{config.Name} offers no {config.Width}x{config.Height} mode";
                ulong crtc = o.Crtc;
                if (crtc == 0)
                {
                    crtc = o.Possible.FirstOrDefault(c => !used.Contains(c) && !targets.Any(t => t.Crtc == c));
                    if (crtc == 0) return $"no free display controller can drive {config.Name}; switch another display off first";
                }
                ushort rotation = RotationFlag(config.Rotation);
                var info = Crtc(crtc);
                if (info != null && (info->rotations & rotation) == 0) return $"{config.Name} cannot be turned {config.Rotation}°";
                targets.Add((crtc, o.Id, mode, rotation, config));
            }

            // The new screen: every target, and every lit CRTC left as it is.
            var extents = targets.Select(t => (t.Config.X, t.Config.Y, t.Config.Extent.W, t.Config.Extent.H)).ToList();
            foreach (var (name, o) in outputs)
            {
                if (o.Crtc == 0 || off.Contains(o.Crtc) || targets.Any(t => t.Crtc == o.Crtc)) continue;
                var info = Crtc(o.Crtc);
                if (info != null && info->mode != 0) extents.Add((info->x, info->y, (int)info->width, (int)info->height));
            }
            if (extents.Count == 0) return "that would leave no display lit";
            var (newW, newH) = ScreenFor(extents);
            XRRGetScreenSizeRange(display, root, out int minW, out int minH, out int maxW, out int maxH);
            if (newW > maxW || newH > maxH) return $"the layout needs a {newW}x{newH} screen, and this one holds {maxW}x{maxH}";
            newW = Math.Max(newW, minW);
            newH = Math.Max(newH, minH);

            int screen = XDefaultScreen(display);
            int oldW = XDisplayWidth(display, screen), oldH = XDisplayHeight(display, screen);
            int oldMmW = XDisplayWidthMM(display, screen), oldMmH = XDisplayHeightMM(display, screen);

            foreach (var crtc in off.Concat(targets.Select(t => t.Crtc)).Distinct())
            {
                var info = Crtc(crtc);
                if (info == null || info->mode == 0) continue;
                int status = XRRSetCrtcConfig(display, resources, crtc, 0, 0, 0, 0, 1, null, 0);
                if (status != RRSetConfigSuccess) return $"X refused to switch a display controller off (status {status})";
            }
            if ((newW, newH) != (oldW, oldH)) SetScreen(display, root, newW, newH, oldW, oldH, oldMmW, oldMmH);
            foreach (var t in targets)
            {
                ulong output = t.Output;
                int status = XRRSetCrtcConfig(display, resources, t.Crtc, 0, t.Config.X, t.Config.Y, t.Mode, t.Rotation, &output, 1);
                if (status != RRSetConfigSuccess) return $"X refused {t.Config.Name} at {t.Config.Width}x{t.Config.Height} (status {status})";
            }
            if (primary is not null && outputs.TryGetValue(primary, out var p)) XRRSetOutputPrimary(display, root, p.Id);
            return null;
        }
        finally
        {
            foreach (var info in crtcInfos.Values) if (info != 0) XRRFreeCrtcInfo((XRRCrtcInfo*)info);
            if (resources != null) XRRFreeScreenResources(resources);
            XUngrabServer(display);
            XSync(display, 0);
        }
    }

    /// <summary>The output's mode of that size nearest the asked refresh rate
    /// (the fastest when none is asked); 0 when it has none of that size.</summary>
    private static ulong PickMode(XRRScreenResources* resources, ulong[] modes, int width, int height, double hz)
    {
        ulong best = 0;
        double bestScore = double.MaxValue;
        foreach (var id in modes)
        {
            if (Mode(resources, id) is not { } m || m.width != width || m.height != height) continue;
            if ((m.modeFlags & ModeFlagInterlace) != 0) continue;
            double rate = RefreshOf(resources, id);
            double score = hz > 0 ? Math.Abs(rate - hz) : -rate;
            if (score < bestScore) { bestScore = score; best = id; }
        }
        return best;
    }

    private static XRRModeInfo? Mode(XRRScreenResources* resources, ulong id)
    {
        for (int i = 0; i < resources->nmode; i++)
            if (resources->modes[i].id == id) return resources->modes[i];
        return null;
    }

    internal static ushort RotationFlag(int degrees) => degrees switch
    {
        90 => 2,
        180 => 4,
        270 => 8,
        _ => 1,
    };

    /// <summary>The screen that holds every CRTC: from 0,0 to the furthest
    /// right and bottom edge.</summary>
    internal static (int Width, int Height) ScreenFor(IEnumerable<(int X, int Y, int W, int H)> crtcs)
    {
        int w = 0, h = 0;
        foreach (var c in crtcs)
        {
            w = Math.Max(w, c.X + c.W);
            h = Math.Max(h, c.Y + c.H);
        }
        return (w, h);
    }

    /// <summary>Resizes the screen, keeping its pixels the same physical size.</summary>
    private static void SetScreen(nint display, ulong root, int w, int h, int oldW, int oldH, int oldMmW, int oldMmH)
    {
        int mmW = oldW > 0 && oldMmW > 0 ? (int)Math.Round((double)oldMmW * w / oldW) : (int)Math.Round(w * 25.4 / 96);
        int mmH = oldH > 0 && oldMmH > 0 ? (int)Math.Round((double)oldMmH * h / oldH) : (int)Math.Round(h * 25.4 / 96);
        XRRSetScreenSize(display, root, w, h, mmW, mmH);
    }

    private static int Degrees(ushort rotation) => (rotation & 0xF) switch
    {
        2 => 90,
        4 => 180,
        8 => 270,
        _ => 0,
    };

    private static double RefreshOf(XRRScreenResources* resources, ulong mode)
    {
        for (int i = 0; i < resources->nmode; i++)
        {
            var m = resources->modes[i];
            if (m.id != mode) continue;
            double v = m.vTotal;
            if ((m.modeFlags & ModeFlagDoubleScan) != 0) v *= 2;
            if ((m.modeFlags & ModeFlagInterlace) != 0) v /= 2;
            return m.hTotal > 0 && v > 0 ? Math.Round(m.dotClock / (m.hTotal * v), 2) : 0;
        }
        return 0;
    }

    /// <summary>The monitor name descriptor (tag 0xFC) of a base EDID block.</summary>
    internal static string? EdidName(byte[] edid)
    {
        if (edid.Length < 128) return null;
        for (int at = 54; at <= 108; at += 18)
        {
            if (edid[at] != 0 || edid[at + 1] != 0 || edid[at + 3] != 0xFC) continue;
            var text = System.Text.Encoding.ASCII.GetString(edid, at + 5, 13);
            int end = text.IndexOf('\n');
            var name = (end >= 0 ? text[..end] : text).Trim();
            return name.Length > 0 ? name : null;
        }
        return null;
    }

    // Layouts from Xrandr.h, LP64.
    [StructLayout(LayoutKind.Sequential)]
    private struct XRRModeInfo
    {
        public ulong id;
        public uint width, height;
        public ulong dotClock;
        public uint hSyncStart, hSyncEnd, hTotal, hSkew;
        public uint vSyncStart, vSyncEnd, vTotal;
        public byte* name;
        public uint nameLength;
        public ulong modeFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRScreenResources
    {
        public ulong timestamp;
        public ulong configTimestamp;
        public int ncrtc;
        public ulong* crtcs;
        public int noutput;
        public ulong* outputs;
        public int nmode;
        public XRRModeInfo* modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRROutputInfo
    {
        public ulong timestamp;
        public ulong crtc;
        public byte* name;
        public int nameLen;
        public ulong mm_width;
        public ulong mm_height;
        public ushort connection;
        public ushort subpixel_order;
        public int ncrtc;
        public ulong* crtcs;
        public int nclone;
        public ulong* clones;
        public int nmode;
        public int npreferred;
        public ulong* modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRCrtcInfo
    {
        public ulong timestamp;
        public int x, y;
        public uint width, height;
        public ulong mode;
        public ushort rotation;
        public int noutput;
        public ulong* outputs;
        public ushort rotations;
        public int npossible;
        public ulong* possible;
    }

    [DllImport(LibX11)] private static extern ulong XDefaultRootWindow(nint display);
    [DllImport(LibX11)] private static extern int XDefaultScreen(nint display);
    [DllImport(LibX11)] private static extern int XDisplayWidth(nint display, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeight(nint display, int screen);
    [DllImport(LibX11)] private static extern int XDisplayWidthMM(nint display, int screen);
    [DllImport(LibX11)] private static extern int XDisplayHeightMM(nint display, int screen);
    [DllImport(LibX11)] private static extern int XGrabServer(nint display);
    [DllImport(LibX11)] private static extern int XUngrabServer(nint display);
    [DllImport(LibX11)] private static extern int XSync(nint display, int discard);

    [DllImport(LibXrandr)] private static extern XRRScreenResources* XRRGetScreenResourcesCurrent(nint display, ulong window);
    [DllImport(LibXrandr)] private static extern void XRRFreeScreenResources(XRRScreenResources* resources);
    [DllImport(LibXrandr)] private static extern XRROutputInfo* XRRGetOutputInfo(nint display, XRRScreenResources* resources, ulong output);
    [DllImport(LibXrandr)] private static extern void XRRFreeOutputInfo(XRROutputInfo* info);
    [DllImport(LibXrandr)] private static extern XRRCrtcInfo* XRRGetCrtcInfo(nint display, XRRScreenResources* resources, ulong crtc);
    [DllImport(LibXrandr)] private static extern void XRRFreeCrtcInfo(XRRCrtcInfo* info);
    [DllImport(LibXrandr)] private static extern ulong XRRGetOutputPrimary(nint display, ulong window);
    [DllImport(LibXrandr)] private static extern void XRRSetOutputPrimary(nint display, ulong window, ulong output);
    [DllImport(LibXrandr)]
    private static extern int XRRGetScreenSizeRange(nint display, ulong window, out int minWidth, out int minHeight, out int maxWidth, out int maxHeight);
    [DllImport(LibXrandr)]
    private static extern void XRRSetScreenSize(nint display, ulong window, int width, int height, int mmWidth, int mmHeight);
    [DllImport(LibXrandr)]
    private static extern int XRRSetCrtcConfig(nint display, XRRScreenResources* resources, ulong crtc, ulong timestamp,
        int x, int y, ulong mode, ushort rotation, ulong* outputs, int noutputs);
    [DllImport(LibXrandr)]
    private static extern int XRRGetOutputProperty(nint display, ulong output, ulong property, long offset, long length,
        int delete, int pending, ulong reqType, out ulong actualType, out int actualFormat, out ulong nitems,
        out ulong bytesAfter, out byte* prop);
}
