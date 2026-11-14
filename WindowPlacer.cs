using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Test helper: -farcoop-window X,Y,W,H puts this game in a small window at a fixed spot,
    /// so several copies fit on one monitor (e.g. 4 x 500x300 on 1920x1080).
    /// </summary>
    public static class WindowPlacer
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private static bool pending;
        private static int x, y, w, h, tries;
        private static float nextTry;

        public static void Request(string spec)
        {
            var p = spec.Split(',');
            if (p.Length != 4 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) ||
                !int.TryParse(p[2], out w) || !int.TryParse(p[3], out h))
            {
                FarCoopPlugin.Logger.LogWarning("-farcoop-window needs X,Y,W,H (e.g. 0,0,500,300), got: " + spec);
                return;
            }
            pending = true;
            tries = 0;
            nextTry = 0f;
        }

        /// <summary>Call every frame; applies the size, then moves the window (needs a few frames).</summary>
        public static void Tick()
        {
            if (!pending || Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 0.5f;
            tries++;
            try
            {
                if (Screen.width != w || Screen.height != h || Screen.fullScreen)
                    Screen.SetResolution(w, h, FullScreenMode.Windowed);
                IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    // outer size = client size + borders/title bar
                    GetWindowRect(hwnd, out RECT wr);
                    GetClientRect(hwnd, out RECT cr);
                    int bw = (wr.Right - wr.Left) - (cr.Right - cr.Left);
                    int bh = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, w + bw, h + bh, 0x0004 /* NOZORDER */);
                }
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("Window placement: " + e.Message); pending = false; return; }
            // re-apply a few times: the game sets its own resolution while starting up
            if (tries >= 12) { pending = false; FarCoopPlugin.Logger.LogInfo($"Window placed at {x},{y} size {w}x{h}"); }
        }
    }
}
