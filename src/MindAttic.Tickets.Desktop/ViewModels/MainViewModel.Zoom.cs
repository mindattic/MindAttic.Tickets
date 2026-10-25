using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>Ctrl+Plus / Ctrl+Minus / Ctrl+0 scale the whole window up to 200% (WCAG 1.4.4); lists scroll and rows wrap,
/// so nothing is cut off as it grows.</summary>
public sealed partial class MainViewModel
{
    public const double MinZoom = 0.8, MaxZoom = 2.0, ZoomStep = 0.1;

    private double zoom = 1;
    private ICommand? zoomIn, zoomOut, zoomReset;

    public double Zoom
    {
        get => zoom;
        set
        {
            if (!Set(ref zoom, Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2))) return;
            Raise(nameof(ZoomText));
            settings.Zoom = zoom;
            settings.Save();
        }
    }

    /// <summary>"125%" while zoomed; empty at 100%.</summary>
    public string ZoomText => Math.Abs(Zoom - 1) < 0.001 ? "" : $"{Zoom * 100:0}%";

    public ICommand ZoomInCommand => zoomIn ??= new RelayCommand(() => Zoom += ZoomStep);
    public ICommand ZoomOutCommand => zoomOut ??= new RelayCommand(() => Zoom -= ZoomStep);
    public ICommand ZoomResetCommand => zoomReset ??= new RelayCommand(() => Zoom = 1);
}
