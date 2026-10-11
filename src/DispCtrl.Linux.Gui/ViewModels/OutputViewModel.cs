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

    public void Update(XRandROutput output, int number, string? model)
    {
        _output = output;
        _number = number;
        _model = model;
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
