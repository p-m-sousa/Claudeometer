using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeUsage.WinForms
{
    /// <summary>
    /// Draws the window and notification-area icon at runtime, so the portable build stays a
    /// couple of files with no binary assets to keep in sync.
    /// </summary>
    internal static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static Icon Create()
        {
            try
            {
                using (var bitmap = new Bitmap(32, 32))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.Clear(Color.Transparent);
                        using (var background = new SolidBrush(Color.FromArgb(24, 122, 154)))
                        using (var path = RoundedSquare(1, 1, 30, 30, 6))
                        {
                            graphics.FillPath(background, path);
                        }

                        using (var bars = new SolidBrush(Color.White))
                        {
                            graphics.FillRectangle(bars, 7, 19, 5, 7);
                            graphics.FillRectangle(bars, 14, 13, 5, 13);
                            graphics.FillRectangle(bars, 21, 7, 5, 19);
                        }
                    }

                    return Icon.FromHandle(bitmap.GetHicon());
                }
            }
            catch
            {
                return null;
            }
        }

        internal static void Release(Icon icon)
        {
            if (icon == null) return;
            try
            {
                var handle = icon.Handle;
                icon.Dispose();
                DestroyIcon(handle);
            }
            catch
            {
                // Releasing a drawing handle at shutdown is best effort.
            }
        }

        private static GraphicsPath RoundedSquare(int x, int y, int width, int height, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
