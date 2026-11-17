using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace FreeVideoStudio.App;

public static class Win32FileDropInterop
{
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hWnd, uint message, uint action, nint pChangeFilterStruct);

    private static readonly List<SubclassProc> _keepAlive = new();

    public static void Attach(Window window, Action<string[]> onFilesDropped)
    {
        if (!OperatingSystem.IsWindows()) return;

        window.Opened += (_, _) =>
        {
            try
            {
                nint hwnd = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (hwnd == nint.Zero) return;

                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, nint.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, nint.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, nint.Zero);
                DragAcceptFiles(hwnd, true);

                SubclassProc proc = (h, msg, wParam, lParam, id, refData) =>
                {
                    if (msg == WM_DROPFILES)
                    {
                        try
                        {
                            uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                            var files = new List<string>((int)count);
                            for (uint i = 0; i < count; i++)
                            {
                                uint len = DragQueryFile(wParam, i, null, 0);
                                if (len == 0) continue;
                                var sb = new StringBuilder((int)len + 1);
                                if (DragQueryFile(wParam, i, sb, (uint)sb.Capacity) > 0)
                                {
                                    files.Add(sb.ToString());
                                }
                            }

                            if (files.Count > 0)
                            {
                                string[] payload = files.ToArray();
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    try { onFilesDropped(payload); }
                                    catch (Exception ex) { RuntimeLog.Fail("DND", ex); }
                                });
                            }
                        }
                        finally
                        {
                            DragFinish(wParam);
                        }

                        return 0;
                    }

                    return DefSubclassProc(h, msg, wParam, lParam);
                };

                _keepAlive.Add(proc);
                if (SetWindowSubclass(hwnd, proc, 1, 0))
                {
                    RuntimeLog.Info("DND", $"WM_DROPFILES fallback attached to '{window.Title}'.");
                    window.Closed += (s, ev) =>
                    {
                        RemoveWindowSubclass(hwnd, proc, 1);
                        _keepAlive.Remove(proc);
                    };
                }
                else
                {
                    _keepAlive.Remove(proc);
                    RuntimeLog.Fail("DND", $"Failed to attach WM_DROPFILES fallback to '{window.Title}'. Subclass registration failed.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("DND", ex);
            }
        };
    }
}
