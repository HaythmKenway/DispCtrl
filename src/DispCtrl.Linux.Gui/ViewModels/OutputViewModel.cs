using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Gui.ViewModels;

/// <summary>One lit output on the Displays page: its card in "Each display"
/// and its tile in the arrangement.</summary>
public sealed class OutputViewModel : INotifyPropertyChanged
{
    private XRandROutput _output = new("", false, false, 0, 0, 0, 0, 0, 0);
    private int _number;
    private string? _model;
    private MonitorRowViewModel? _dim;
    private MonitorRowViewModel? _brightness;
    private List<XRandRMode> _modes = [];
    private IReadOnlyList<ControlRowViewModel> _controls = [];

    /// <summary>A resolution, refresh rate or turn was chosen on the card:
    /// (output, width, height, hertz, degrees). The page applies it and offers
    /// to keep it.</summary>
    public event Action<OutputViewModel, int, int, double, int>? ModeRequested;

    private (int W, int H) Unturned => _output.Rotation is 90 or 270 ? (_output.HeightPx, _output.WidthPx) : (_output.WidthPx, _output.HeightPx);

    /// <summary>Each size the output offers, largest first.</summary>
    public List<string> Resolutions => Sizes.Select(s => string.Create(CultureInfo.InvariantCulture,
        $"{s.W} × {s.H}{(_modes.Any(m => m.Width == s.W && m.Height == s.H && m.Preferred) ? " (recommended)" : "")}")).ToList();

    private List<(int W, int H)> Sizes => _modes.Select(m => (m.Width, m.Height)).Distinct().ToList();

    public int ResolutionIndex
    {
        get => Sizes.IndexOf(Unturned);
        set
        {
            if (value < 0 || value >= Sizes.Count || value == ResolutionIndex) return;
            var (w, h) = Sizes[value];
            ModeRequested?.Invoke(this, w, h, 0, _output.Rotation);
        }
    }

    private List<double> Rates => _modes.Where(m => (m.Width, m.Height) == Unturned).Select(m => m.RefreshHz).Distinct().ToList();

    public List<string> RefreshRates => Rates.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r:0.##} Hz")).ToList();

    public int RefreshIndex
    {
        get
        {
            var rates = Rates;
            if (rates.Count == 0) return -1;
            return rates.IndexOf(rates.OrderBy(r => Math.Abs(r - _output.RefreshHz)).First());
        }
        set
        {
            if (value < 0 || value >= Rates.Count || value == RefreshIndex) return;
            var (w, h) = Unturned;
            ModeRequested?.Invoke(this, w, h, Rates[value], _output.Rotation);
        }
    }

    public bool HasModes => _modes.Count > 0;

    public static List<string> Orientations { get; } = ["Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)"];
    private static readonly int[] OrientationDegrees = [0, 90, 180, 270];

    public int OrientationIndex
    {
        get => Array.IndexOf(OrientationDegrees, _output.Rotation);
        set
        {
            if (value < 0 || value >= OrientationDegrees.Length || value == OrientationIndex) return;
            var (w, h) = Unturned;
            ModeRequested?.Invoke(this, w, h, _output.RefreshHz, OrientationDegrees[value]);
        }
    }

    /// <summary>The monitor's own controls, when a DDC/CI monitor certainly
    /// belongs to this display.</summary>
    public IReadOnlyList<ControlRowViewModel> Controls
    {
        get => _controls;
        set { _controls = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasControls)); }
    }

    public bool HasControls => _controls.Count > 0;

    public XRandROutput Output => _output;

    /// <summary>The number on its tile and on Identify, from 1.</summary>
    public int Number => _number;

    /// <summary>The model name from the EDID; null when it names none, as
    /// most laptop panels do.</summary>
    public string? Model => _model;

    /// <summary>A laptop's own panel, by the connector types only a built-in
    /// panel uses.</summary>
    public bool IsBuiltIn => BuiltInConnector(_output.Name);

    public static bool BuiltInConnector(string output) =>
        output.StartsWith("eDP", StringComparison.Ordinal)
        || output.StartsWith("LVDS", StringComparison.Ordinal)
        || output.StartsWith("DSI", StringComparison.Ordinal);

    public string Title => $"{_number}   {Label}";

    /// <summary>What it is called everywhere but its card's number.</summary>
    public string Label => _model ?? (IsBuiltIn ? "Built-in display" : _output.Name);

    public string Details
    {
        get
        {
            var parts = new List<string> { _output.Name };
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_output.WidthPx} × {_output.HeightPx}"));
            if (_output.RefreshHz > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_output.RefreshHz:0.##} Hz"));
            if (_output.Primary) parts.Add("main display");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Pixel density from the size the panel reports, or why there is none.</summary>
    public string DensityText => DispCtrl.Linux.Arrangement.ArrangementEditor.PixelsPerInch(_output) is { } ppi
        ? string.Create(CultureInfo.InvariantCulture, $"{ppi} pixels per inch, {DispCtrl.Linux.Arrangement.ArrangementEditor.Orientation(_output.Rotation).ToLowerInvariant()}")
        : $"Size not reported by the display, {DispCtrl.Linux.Arrangement.ArrangementEditor.Orientation(_output.Rotation).ToLowerInvariant()}";

    public string ModeText => _output.RefreshHz > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{_output.WidthPx} × {_output.HeightPx} at {_output.RefreshHz:0.##} Hz")
        : string.Create(CultureInfo.InvariantCulture, $"{_output.WidthPx} × {_output.HeightPx}");

    public string OrientationText => _output.Rotation switch
    {
        90 => "Portrait (turned left)",
        180 => "Landscape (upside down)",
        270 => "Portrait (turned right)",
        _ => "Landscape",
    };

    public string PositionText => string.Create(CultureInfo.InvariantCulture, $"{_output.PosX}, {_output.PosY}");

    /// <summary>The panel's size in centimetres and inches, as the monitor
    /// reports it, or that it reports none (projectors, some TVs).</summary>
    public string SizeText
    {
        get
        {
            if (_output.WidthMm <= 0 || _output.HeightMm <= 0) return "Not reported by the display";
            double inches = Math.Sqrt((double)_output.WidthMm * _output.WidthMm + (double)_output.HeightMm * _output.HeightMm) / 25.4;
            return string.Create(CultureInfo.InvariantCulture,
                $"{_output.WidthMm / 10.0:0.#} × {_output.HeightMm / 10.0:0.#} cm, {inches:0.#}-inch diagonal");
        }
    }

    public bool IsPrimary => _output.Primary;
    public bool IsSecondary => !_output.Primary;

    public MonitorRowViewModel? Dim
    {
        get => _dim;
        set { if (_dim == value) return; _dim = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDim)); }
    }

    public bool HasDim => _dim is not null;

    /// <summary>The hardware brightness control that certainly belongs to this
    /// display; null when none does or the pairing is not certain.</summary>
    public MonitorRowViewModel? Brightness
    {
        get => _brightness;
        set { if (_brightness == value) return; _brightness = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBrightness)); }
    }

    public bool HasBrightness => _brightness is not null;

    public void Update(XRandROutput output, int number, string? model, List<XRandRMode>? modes = null)
    {
        _output = output;
        _number = number;
        _model = model;
        if (modes is not null) _modes = modes;
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
