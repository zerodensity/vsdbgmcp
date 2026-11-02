using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Screenshots the debuggee's window.
    ///
    /// Uses PrintWindow with full-content rendering, which asks the window to draw
    /// itself. That keeps working when the window is behind others. It waits on the
    /// thread that owns the window, which a breakpoint may have suspended, so the
    /// capture runs on a thread of its own and the caller waits only so long.
    /// </summary>
    static class WindowCapture
    {
        const uint RenderFullContent = 0x00000002;

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);

        delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Captures on a thread of its own and waits at most <paramref name="limit"/>.
        /// A window whose thread is suspended keeps that thread waiting until it draws;
        /// it is left to finish on its own rather than holding the caller.
        /// </summary>
        public static CaptureResult Capture(int pid, int[] region, TimeSpan limit)
        {
            CaptureResult result = null;
            var thread = new System.Threading.Thread(() => result = Capture(pid, region))
            {
                IsBackground = true,
                Name = "vsdbgmcp capture"
            };
            thread.Start();
            if (thread.Join(limit)) return result;

            return new CaptureResult
            {
                Error = "The window did not draw itself within " + (int)limit.TotalSeconds + " seconds. A program " +
                        "stopped in the debugger cannot draw; resume it with go and capture while it runs."
            };
        }

        static CaptureResult Capture(int pid, int[] region)
        {
            try
            {
                var window = FindMainWindow(pid);
                if (window == IntPtr.Zero)
                    return new CaptureResult { Error = "That process has no visible top-level window." };

                if (!GetWindowRect(window, out var rect))
                    return new CaptureResult { Error = "Could not measure the window." };

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                    return new CaptureResult { Error = "The window has no size." };

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        var dc = graphics.GetHdc();
                        try
                        {
                            if (!PrintWindow(window, dc, RenderFullContent))
                                return new CaptureResult { Error = "The window refused to render itself." };
                        }
                        finally
                        {
                            graphics.ReleaseHdc(dc);
                        }
                    }

                    var output = region != null && region.Length == 4 ? Crop(bitmap, region) : bitmap;
                    try
                    {
                        using (var stream = new MemoryStream())
                        {
                            output.Save(stream, ImageFormat.Png);
                            return new CaptureResult
                            {
                                Format = "png",
                                Width = output.Width,
                                Height = output.Height,
                                Base64 = Convert.ToBase64String(stream.ToArray())
                            };
                        }
                    }
                    finally
                    {
                        if (!ReferenceEquals(output, bitmap)) output.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return new CaptureResult { Error = ex.Message };
            }
        }

        static Bitmap Crop(Bitmap source, int[] region)
        {
            var box = new Rectangle(
                Math.Max(0, region[0]),
                Math.Max(0, region[1]),
                Math.Min(region[2], source.Width - Math.Max(0, region[0])),
                Math.Min(region[3], source.Height - Math.Max(0, region[1])));

            if (box.Width <= 0 || box.Height <= 0) return source;
            return source.Clone(box, source.PixelFormat);
        }

        static IntPtr FindMainWindow(int pid)
        {
            var best = IntPtr.Zero;
            var bestArea = 0;

            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != (uint)pid) return true;
                if (!IsWindowVisible(window)) return true;
                if (GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                if (GetWindowTextLength(window) == 0) return true;

                if (!GetWindowRect(window, out var rect)) return true;
                var area = (rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area <= bestArea) return true;

                bestArea = area;
                best = window;
                return true;
            }, IntPtr.Zero);

            return best;
        }
    }
}
