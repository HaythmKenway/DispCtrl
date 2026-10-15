using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Gui.ViewModels;

/// <summary>One of a monitor's own controls over DDC/CI: a slider for a level
/// (contrast, volume, a colour gain) or a list for a choice (input, colour
/// preset, picture mode). Only codes the monitor lists and DispCtrl may write
/// ever become one (<see cref="Ddcutil.IsWritable"/>).</summary>
public sealed class ControlRowViewModel : INotifyPropertyChanged
{
    private readonly CommandSender _sender;
    private readonly int _display;
    private double _value, _confirmedValue;
    private int _selected, _confirmedSelected;
    private string _status = "";

    /// <summary>Like the brightness rows, the value read from the monitor comes
    /// through the constructor, so a binding's first assignment is not
    /// written straight back.</summary>
    public ControlRowViewModel(CommandSender sender, int display, DdcFeature feature, DdcVcpValue value)
    {
        _sender = sender;
        _display = display;
        Code = feature.Code;
        Title = TitleOf(feature);
        Options = feature.Values.Select(v => v.Name).ToList();
        Values = feature.Values.Select(v => v.Value).ToList();
        Maximum = value.Maximum is > 0 ? value.Maximum.Value : 100;
        _value = value.Current;
        _selected = Values.IndexOf((byte)value.Current);
        _confirmedValue = _value;
        _confirmedSelected = _selected;
    }

    public byte Code { get; }
    public string Title { get; }
    public bool IsList => Options.Count > 0;
    public bool IsLevel => !IsList;
    public List<string> Options { get; }
    public List<byte> Values { get; }
    public double Maximum { get; }

    public string ValueText => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_value)}");

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Round(_value) == Math.Round(value)) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
            Send(((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture), () => _confirmedValue = value, () =>
            {
                _value = _confirmedValue;
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(ValueText));
            });
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected || value < 0 || value >= Values.Count) return;
            _selected = value;
            OnPropertyChanged();
            Send($"0x{Values[value]:X2}", () => _confirmedSelected = value, () =>
            {
                _selected = _confirmedSelected;
                OnPropertyChanged(nameof(SelectedIndex));
            });
        }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => _status.Length > 0;

    // Optimistic: the control shows the new value at once and goes back to the
    // last one the monitor took if the write is refused.
    private void Send(string value, Action confirmed, Action rollBack) =>
        _sender.Post($"ddc:{_display}:{Code:X2}",
            ["monitor", _display.ToString(CultureInfo.InvariantCulture), "set", $"0x{Code:X2}", value],
            error =>
            {
                Status = error ?? "";
                if (error is null) confirmed(); else rollBack();
            });

    private static string TitleOf(DdcFeature f) => f.Code switch
    {
        Ddcutil.VcpContrast => "Contrast",
        Ddcutil.VcpColourPreset => "Colour preset",
        Ddcutil.VcpInput => "Input source",
        Ddcutil.VcpVolume => "Volume",
        Ddcutil.VcpMute => "Mute",
        Ddcutil.VcpSharpness => "Sharpness",
        Ddcutil.VcpPictureMode => "Picture mode",
        0x16 => "Red",
        0x18 => "Green",
        0x1A => "Blue",
        _ => f.Name,
    };

    /// <summary>The codes offered on a display's card, in this order.</summary>
    public static readonly byte[] Offered =
        [Ddcutil.VcpInput, Ddcutil.VcpContrast, Ddcutil.VcpPictureMode, Ddcutil.VcpColourPreset, 0x16, 0x18, 0x1A,
         Ddcutil.VcpSharpness, Ddcutil.VcpVolume, Ddcutil.VcpMute];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
