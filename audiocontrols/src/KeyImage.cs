using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace AudioControls
{
    /// <summary>Draws the Switch Output Device key: a coloured tile with the switch glyph and the device name.</summary>
    internal static class KeyImage
    {
        const int Size = 144;  // the @2x key size; Stream Dock scales it down for the hardware

        // Text box under the glyph, and the font size range it's fitted into (pixels at 144x144).
        static readonly RectangleF TextArea = new RectangleF(6, 74, Size - 12, 62);
        const float MaxFontSize = 18;
        const float MinFontSize = 13;

        /// <summary>One colour per position in the key's device list: 1st device green, 2nd blue, and so on.</summary>
        static readonly Color[] Palette =
        {
            Color.FromArgb(46, 160, 90),    // green (matches the other Audio Controls icons)
            Color.FromArgb(40, 110, 200),   // blue
            Color.FromArgb(215, 110, 30),   // orange
            Color.FromArgb(135, 75, 195),   // purple
            Color.FromArgb(20, 140, 150),   // teal
            Color.FromArgb(200, 60, 75)     // red
        };
        static readonly Color Inactive = Color.FromArgb(70, 70, 70);

        /// <summary>
        /// Renders the key as a PNG data URL for setImage. colourIndex is the device's position in the key's
        /// list; -1 (current output isn't in the list) draws the tile grey.
        /// </summary>
        public static string Render(string text, int colourIndex)
        {
            Color background = colourIndex < 0 ? Inactive : Palette[colourIndex % Palette.Length];

            using (var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (GraphicsPath tile = RoundedRect(new RectangleF(4, 4, Size - 8, Size - 8), 24))
                using (var brush = new SolidBrush(background))
                    g.FillPath(brush, tile);

                DrawGlyph(g, 33, 14);
                DrawText(g, text);

                using (var png = new MemoryStream())
                {
                    bmp.Save(png, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
                }
            }
        }

        /// <summary>Speaker with two swap arrows, 78x52 at the given offset (same shape as images/outputSwitch.png).</summary>
        static void DrawGlyph(Graphics g, float x, float y)
        {
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 7))
            {
                g.FillPolygon(white, new[]
                {
                    new PointF(x, y + 14), new PointF(x + 14, y + 14), new PointF(x + 32, y),
                    new PointF(x + 32, y + 52), new PointF(x + 14, y + 38), new PointF(x, y + 38)
                });

                float ax = x + 44, aw = 34, head = 9, y1 = y + 15, y2 = y + 37;
                g.DrawLine(pen, ax, y1, ax + aw - 2, y1);
                g.FillPolygon(white, new[]
                {
                    new PointF(ax + aw + 2, y1), new PointF(ax + aw - head, y1 - head), new PointF(ax + aw - head, y1 + head)
                });
                g.DrawLine(pen, ax + 2, y2, ax + aw, y2);
                g.FillPolygon(white, new[]
                {
                    new PointF(ax - 2, y2), new PointF(ax + head, y2 - head), new PointF(ax + head, y2 + head)
                });
            }
        }

        /// <summary>
        /// Uses the largest font size at which every word fits on a line (no mid-word breaks) and all lines fit
        /// the text area. Names too long even at the smallest size are cut off with "…".
        /// </summary>
        static void DrawText(Graphics g, string text)
        {
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisWord
            })
            using (var white = new SolidBrush(Color.White))
            {
                for (float size = MaxFontSize; ; size -= 1)
                {
                    using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        if (size <= MinFontSize || Fits(g, text, font, format))
                        {
                            g.DrawString(text, font, white, TextArea, format);
                            return;
                        }
                    }
                }
            }
        }

        static bool Fits(Graphics g, string text, Font font, StringFormat format)
        {
            foreach (string word in text.Split(' '))
                if (g.MeasureString(word, font).Width > TextArea.Width) return false;
            return g.MeasureString(text, font, (int)TextArea.Width, format).Height <= TextArea.Height;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
