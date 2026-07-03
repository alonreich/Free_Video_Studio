using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using FortniteVideoSoftware.Core.Media;

namespace FortniteVideoSoftware.App;

public class MpvVideoView : OpenGlControlBase
{
    private nint _mpvHandle;
    private nint _renderContext;
    private LibMpvInterop.GetProcAddressCb? _getProcAddressCb;
    private LibMpvInterop.MpvRenderUpdateFn? _renderUpdateCb;

    // Cached unmanaged pointers for the render loop
    private nint _fboPtr = nint.Zero;
    private nint _flipYPtr = nint.Zero;
    private LibMpvInterop.mpv_render_param[]? _renderParams;

    public MpvIpcClient? IpcClient { get; private set; }

    public void InitializeMpv(string mpvPath)
    {
        _mpvHandle = MpvWrapper.mpv_create();

        // CRITICAL FIX 1: 'wid=0' strictly forbids the OS from ever spawning a detached window.
        MpvWrapper.mpv_set_option_string(_mpvHandle, "wid", "0");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "vo", "libmpv");

        // The embedded preview renders through libmpv's OpenGL FBO path. Letting mpv
        // probe hwdec interop here produces noisy failed D3D/DXVA/CUDA/Vulkan attempts
        // before falling back, so preview decode is kept explicit and deterministic.
        MpvWrapper.mpv_set_option_string(_mpvHandle, "hwdec", "no");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "gpu-hwdec-interop", "no");
        // CRITICAL FIX 2: Opaque background prevents Avalonia from discarding fully transparent FBOs.
        MpvWrapper.mpv_set_option_string(_mpvHandle, "background", "#FF000000");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "keep-open", "yes");
        MpvWrapper.mpv_set_option_string(_mpvHandle, "idle", "yes");

        // Dev mode verbose MPV logging
        if (RuntimeLog.IsDevMode && RuntimeLog.DevLogDir != null)
        {
            string mpvLogPath = System.IO.Path.Combine(RuntimeLog.DevLogDir, "mpv_debug.log");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "yes");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "msg-level", "all=v");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "log-file", mpvLogPath);
        }
        else
        {
            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "no");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "msg-level", "all=warn");
        }

        MpvWrapper.mpv_set_option_string(_mpvHandle, "force-window", "no");

        // Disable MPV's built-in OSD seek/progress bar overlay. By default MPV shows a grey
        // progress bar in the center of the video on every seek (click-to-seek, arrow keys,
        // drag). This app renders its own custom Avalonia timeline/overlays, so MPV's native
        // OSD bar is redundant and visually intrusive. Disabling it here covers ALL MPV video
        // previews app-wide (Main, Granular, Crop Tool, Video Merger, Music Wizard).
        MpvWrapper.mpv_set_option_string(_mpvHandle, "osd-bar", "no");

        MpvWrapper.mpv_initialize(_mpvHandle);
        IpcClient = new MpvIpcClient(_mpvHandle);

        // Render context will be created inside OnOpenGlRender where the GL
        // context is actually current on the render thread.
        RequestNextFrameRendering();
    }

    public Task StartMpvProcessAsync(string mpvPath)
    {
        InitializeMpv(mpvPath);
        return Task.CompletedTask;
    }

    public void DisposeMpv()
    {
        if (_renderContext != nint.Zero)
        {
            LibMpvInterop.mpv_render_context_free(_renderContext);
            _renderContext = nint.Zero;
        }
        IpcClient?.Dispose();
        IpcClient = null;
        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.mpv_terminate_destroy(_mpvHandle);
            _mpvHandle = nint.Zero;
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        // ════════════════════════════════════════════════════════════════
        // CRITICAL: Render context MUST be created HERE, inside
        // OnOpenGlRender, where the Avalonia OpenGL context is CURRENT
        // on this thread.
        // ════════════════════════════════════════════════════════════════
        if (_renderContext == nint.Zero && _mpvHandle != nint.Zero)
        {
            // CRITICAL FIX 3: Avalonia throws exceptions for missing GL extensions.
            // If this bubbles into unmanaged libmpv C code, it instantly kills the render context.
            _getProcAddressCb = (ctx, name) =>
            {
                try
                {
                    return gl.GetProcAddress(name);
                }
                catch
                {
                    return nint.Zero;
                }
            };

            // Pre-allocate render loop pointers
            if (_fboPtr == nint.Zero)
            {
                _fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvInterop.mpv_opengl_fbo>());
                _flipYPtr = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(_flipYPtr, 1); // Avalonia flipY is always 1

                _renderParams = new LibMpvInterop.mpv_render_param[]
                {
                    new LibMpvInterop.mpv_render_param { type = LibMpvInterop.MPV_RENDER_PARAM_OPENGL_FBO, data = _fboPtr },
                    new LibMpvInterop.mpv_render_param { type = LibMpvInterop.MPV_RENDER_PARAM_FLIP_Y, data = _flipYPtr },
                    new LibMpvInterop.mpv_render_param { type = 0, data = nint.Zero }
                };
            }

            var initParams = new LibMpvInterop.mpv_opengl_init_params
            {
                get_proc_address = Marshal.GetFunctionPointerForDelegate(_getProcAddressCb),
                get_proc_address_ctx = nint.Zero
            };

            nint initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpvInterop.mpv_opengl_init_params>());
            Marshal.StructureToPtr(initParams, initParamsPtr, false);
            nint apiTypePtr = Marshal.StringToHGlobalAnsi("opengl");

            var ctxParams = new LibMpvInterop.mpv_render_param[]
            {
                new() { type = LibMpvInterop.MPV_RENDER_PARAM_API_TYPE, data = apiTypePtr },
                new() { type = LibMpvInterop.MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, data = initParamsPtr },
                new() { type = 0, data = nint.Zero }
            };

            LibMpvInterop.mpv_render_context_create(out _renderContext, _mpvHandle, ctxParams);

            Marshal.FreeHGlobal(initParamsPtr);
            Marshal.FreeHGlobal(apiTypePtr);

            if (_renderContext != nint.Zero)
            {
                _renderUpdateCb = (ctx) =>
                {
                    try
                    {
                        if (_renderContext != nint.Zero)
                        {
                            ulong flags = LibMpvInterop.mpv_render_context_update(_renderContext);
                            if ((flags & LibMpvInterop.MPV_RENDER_UPDATE_FRAME) != 0)
                                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
                        }
                    }
                    catch { }
                };
                LibMpvInterop.mpv_render_context_set_update_callback(_renderContext, _renderUpdateCb, nint.Zero);
            }
        }

        if (_renderContext == nint.Zero)
        {
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
            return;
        }

        LibMpvInterop.mpv_render_context_update(_renderContext);

        var scale = VisualRoot?.RenderScaling ?? 1.0;
        int width = (int)(Bounds.Width * scale);
        int height = (int)(Bounds.Height * scale);

        if (width <= 0 || height <= 0)
        {
            // CRITICAL FIX: Pump the render context with an empty parameter array to prevent the fatal
            // 'mpv_render_context_render() not being called or stuck' crash during Avalonia occlusion culling.
            var emptyParams = new LibMpvInterop.mpv_render_param[] { new LibMpvInterop.mpv_render_param { type = 0, data = nint.Zero } };
            LibMpvInterop.mpv_render_context_render(_renderContext, emptyParams);

            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
            return;
        }

        // CRITICAL FIX 4: Explicitly define GL_RGBA8. Passing 0 forces mpv to query the FBO,
        // which silently fails on Windows ANGLE contexts and aborts the video draw pass.
        var fbo = new LibMpvInterop.mpv_opengl_fbo
        {
            fbo = fb,
            w = width,
            h = height,
            internal_format = 32856
        };

        // Overwrite the pre-allocated FBO memory block with the new dimensions
        Marshal.StructureToPtr(fbo, _fboPtr, false);

        // Command MPV to draw onto the Avalonia FBO layer using cached params
        LibMpvInterop.mpv_render_context_render(_renderContext, _renderParams!);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_renderContext != nint.Zero)
        {
            LibMpvInterop.mpv_render_context_free(_renderContext);
            _renderContext = nint.Zero;
        }
        if (_mpvHandle != nint.Zero)
        {
            IpcClient?.Dispose();
            IpcClient = null;
            MpvWrapper.mpv_terminate_destroy(_mpvHandle);
            _mpvHandle = nint.Zero;
        }

        // Free cached unmanaged memory
        if (_fboPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_fboPtr);
            _fboPtr = nint.Zero;
        }
        if (_flipYPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_flipYPtr);
            _flipYPtr = nint.Zero;
        }

        base.OnOpenGlDeinit(gl);
    }
}
