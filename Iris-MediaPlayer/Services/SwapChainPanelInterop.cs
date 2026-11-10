using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;

namespace Iris_MediaPlayer.Services;

internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid SwapChainPanelNativeId = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    public static void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var interfaceId = SwapChainPanelNativeId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in interfaceId, out var nativeInterface));

        try
        {
            var vtable = *(IntPtr**)nativeInterface;
            var setSwapChain = (delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(nativeInterface, swapChain));
        }
        finally
        {
            Marshal.Release(nativeInterface);
        }
    }
}
