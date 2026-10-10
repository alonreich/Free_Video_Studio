// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Runtime.InteropServices;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Interop;

internal interface INativeTeardownInvoker
{
    bool WglMakeCurrent(nint hdc, nint hglrc);
    void MpvRenderContextSetUpdateCallback(nint ctx);
    void MpvRenderContextFree(nint ctx);
    void GlDeleteTextures(int n, uint[] textures);
    void GlDeleteFramebuffers(int n, uint[] framebuffers);
    bool WglDXUnregisterObjectNV(nint hDevice, nint hObject);
    bool WglDXCloseDeviceNV(nint hDevice);
    bool WglDeleteContext(nint hglrc);
    int ReleaseDC(nint hWnd, nint hDC);
    bool DestroyWindow(nint hWnd);
    void MpvTerminateDestroy(nint handle);
    void FreeNativeLibrary(nint lib);
}

internal unsafe sealed class DefaultNativeTeardownInvoker : INativeTeardownInvoker
{
    public static readonly DefaultNativeTeardownInvoker Instance = new();

    public bool WglMakeCurrent(nint hdc, nint hglrc) => WglInterop.wglMakeCurrent(hdc, hglrc);

    public void MpvRenderContextSetUpdateCallback(nint ctx)
    {
        LibMpvInterop.mpv_render_context_set_update_callback(ctx, null, nint.Zero);
    }

    public void MpvRenderContextFree(nint ctx)
    {
        LibMpvInterop.mpv_render_context_free(ctx);
    }

    public void GlDeleteTextures(int n, uint[] textures)
    {
        WglInterop.glDeleteTextures(n, textures);
    }

    public void GlDeleteFramebuffers(int n, uint[] framebuffers)
    {
        WglInterop.glDeleteFramebuffers?.Invoke(n, framebuffers);
    }

    public bool WglDXUnregisterObjectNV(nint hDevice, nint hObject)
    {
        return WglInterop.wglDXUnregisterObjectNV == null || WglInterop.wglDXUnregisterObjectNV(hDevice, hObject);
    }

    public bool WglDXCloseDeviceNV(nint hDevice)
    {
        return WglInterop.wglDXCloseDeviceNV == null || WglInterop.wglDXCloseDeviceNV(hDevice);
    }

    public bool WglDeleteContext(nint hglrc) => WglInterop.wglDeleteContext(hglrc);

    public int ReleaseDC(nint hWnd, nint hDC) => WglInterop.ReleaseDC(hWnd, hDC);

    public bool DestroyWindow(nint hWnd) => WglInterop.DestroyWindow(hWnd);

    public void MpvTerminateDestroy(nint handle)
    {
        MpvWrapper.mpv_terminate_destroy(handle);
    }

    public void FreeNativeLibrary(nint lib)
    {
        NativeLibrary.Free(lib);
    }
}
