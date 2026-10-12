using System.Drawing.Drawing2D;

namespace TimerResTuner;

/// <summary>Scatter plot of mean Sleep(1) delta and stdev against requested resolution.</summary>
internal sealed class ChartPanel : Control
{
    private IReadOnlyList<BenchPoint> _points = Array.Empty<BenchPoint>();
    private BenchPoint? _best;

    private static readonly Color MeanColor = Color.FromArgb(37, 99, 235);
    private static readonly Color StdColor = Color.FromArgb(234, 88, 12);
    private static readonly Color BestColor = Color.FromArgb(22, 163, 74);

    public ChartPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
    }

    public void SetData(IReadOnlyList<BenchPoint> points, BenchPoint? best)
    {
        _points = points;
        _best = best;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var plot = new Rectangle(64, 28, Width - 64 - 20, Height - 28 - 44);
        if (plot.Width < 50 || plot.Height < 50) return;

        using var axisPen = new Pen(Color.FromArgb(160, 160, 160));
        using var gridPen = new Pen(Color.FromArgb(232, 232, 232));
        using var textBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
        using var small = new Font(Font.FontFamily, 8f);

        if (_points.Count == 0)
        {
            TextRenderer.DrawText(g, "Run a benchmark to see results", Font, plot, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        double xMin = _points.Min(p => p.RequestedMs), xMax = _points.Max(p => p.RequestedMs);
        if (xMax - xMin < 1e-6) { xMin -= 0.01; xMax += 0.01; }
        double yMax = Math.Max(_points.Max(p => Math.Max(p.MeanDelta, p.StdDev)), 0.01) * 1.1;
        double yMin = Math.Min(0, _points.Min(p => p.MeanDelta));

        float X(double v) => plot.Left + (float)((v - xMin) / (xMax - xMin) * plot.Width);
        float Y(double v) => plot.Bottom - (float)((v - yMin) / (yMax - yMin) * plot.Height);

        // Grid + axis labels
        for (int i = 0; i <= 5; i++)
        {
            double yv = yMin + (yMax - yMin) * i / 5;
            float y = Y(yv);
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            g.DrawString($"{yv:0.000}", small, textBrush, 4, y - 7);

            double xv = xMin + (xMax - xMin) * i / 5;
            float x = X(xv);
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            g.DrawString($"{xv:0.000}", small, textBrush, x - 16, plot.Bottom + 4);
        }
        g.DrawRectangle(axisPen, plot);
        g.DrawString("Requested resolution (ms)", Font, textBrush, plot.Left + plot.Width / 2f - 70, plot.Bottom + 22);
        g.DrawString("ms", small, textBrush, 4, plot.Top - 20);

        using var meanBrush = new SolidBrush(MeanColor);
        using var stdBrush = new SolidBrush(StdColor);
        foreach (var p in _points.Where(p => !p.Masked))
        {
            float x = X(p.RequestedMs);
            float r = p.Phase == "confirm" ? 4.5f : 3f;
            g.FillEllipse(meanBrush, x - r, Y(p.MeanDelta) - r, r * 2, r * 2);
            g.FillEllipse(stdBrush, x - r, Y(p.StdDev) - r, r * 2, r * 2);
        }

        if (_best is not null)
        {
            using var bestPen = new Pen(BestColor, 2f) { DashStyle = DashStyle.Dash };
            using var bestBrush = new SolidBrush(BestColor);
            float bx = X(_best.RequestedMs);
            g.DrawLine(bestPen, bx, plot.Top, bx, plot.Bottom);
            g.DrawString($"best {_best.RequestedMs:0.0000} ms", Font, bestBrush, bx + 4, plot.Top + 2);
        }

        // Legend
        float lx = plot.Right - 210, ly = 6;
        g.FillEllipse(meanBrush, lx, ly + 3, 8, 8);
        g.DrawString("mean Sleep(1) delta", small, textBrush, lx + 12, ly);
        g.FillEllipse(stdBrush, lx + 120, ly + 3, 8, 8);
        g.DrawString("stdev", small, textBrush, lx + 132, ly);
    }
}
