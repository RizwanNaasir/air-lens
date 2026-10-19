using AirLens.Core.Status;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AirLens.App.Pages;

/// <summary>The status dot colours, from the Windows theme so they follow light, dark and contrast themes.</summary>
internal static class StatusBrushes
{
    public static Brush For(StatusTone tone) => (Brush)Application.Current.Resources[tone switch
    {
        StatusTone.Good => "SystemFillColorSuccessBrush",
        StatusTone.Busy => "SystemFillColorAttentionBrush",
        StatusTone.Attention => "SystemFillColorCautionBrush",
        _ => "SystemFillColorNeutralBrush",
    }];
}
