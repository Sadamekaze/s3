using System.Drawing.Drawing2D;

namespace SuikodenText.Desktop.Helpers
{
    public class CustomLabel : Label
    {
        public CustomLabel()
        {
            OutlineForeColor = Color.Black;
            OutlineWidth = 1.5f;

            AutoSize = true;
            BackColor = Color.Transparent;
        }

        public Color OutlineForeColor { get; set; }

        public float OutlineWidth { get; set; }

        public override Size GetPreferredSize(Size proposedSize)
        {
            using Graphics g = CreateGraphics();
            using GraphicsPath gp = new();

            using StringFormat sf =
                (StringFormat)StringFormat.GenericTypographic.Clone();

            float fontSize = g.DpiY * Font.Size / 72f;

            gp.AddString(
                Text,
                Font.FontFamily,
                (int)Font.Style,
                fontSize,
                Point.Empty,
                sf
            );

            RectangleF bounds = gp.GetBounds();

            int width = (int)Math.Ceiling(
                bounds.Width + OutlineWidth * 2
            );

            int height = (int)Math.Ceiling(
                bounds.Height + OutlineWidth * 2
            );

            return new Size(width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (BackColor != Color.Transparent)
            {
                using SolidBrush bg = new(BackColor);
                e.Graphics.FillRectangle(bg, ClientRectangle);
            }

            using GraphicsPath gp = new();

            using StringFormat sf =
                (StringFormat)StringFormat.GenericTypographic.Clone();

            float fontSize =
                e.Graphics.DpiY * Font.Size / 72f;

            // Buat path sementara untuk mengetahui posisi asli glyph
            gp.AddString(
                Text,
                Font.FontFamily,
                (int)Font.Style,
                fontSize,
                Point.Empty,
                sf
            );

            RectangleF bounds = gp.GetBounds();

            // Geser supaya glyph benar-benar dimulai dari kiri atas control
            using Matrix matrix = new();

            matrix.Translate(
                OutlineWidth - bounds.X,
                OutlineWidth - bounds.Y
            );

            gp.Transform(matrix);

            using Pen outline = new(
                OutlineForeColor,
                OutlineWidth
            )
            {
                LineJoin = LineJoin.Round
            };

            using Brush foreBrush =
                new SolidBrush(ForeColor);

            e.Graphics.DrawPath(outline, gp);
            e.Graphics.FillPath(foreBrush, gp);
        }
    }
}