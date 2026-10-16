using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Rendering;

/// <summary>
/// Binds a DXGI swap chain to a WinUI 3 SwapChainPanel via ISwapChainPanelNative
/// (microsoft.ui.xaml.media.dxinterop.h). Called through the raw vtable to avoid depending on
/// built-in COM interop with CsWinRT objects. Must run on the UI thread.
/// </summary>
internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        IntPtr unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var iid = IidSwapChainPanelNative;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr native));
        try
        {
            // IUnknown occupies slots 0-2; SetSwapChain is slot 3.
            var vtable = *(IntPtr**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
