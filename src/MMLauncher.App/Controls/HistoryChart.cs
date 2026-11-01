using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MMLauncher.Core.History;

namespace MMLauncher.App.Controls;

/// <summary>
/// Player count over the last day or week: an orange area per stretch the server was up,
/// grey bands where it was down, and the exact count under the mouse.
/// </summary>
public sealed class HistoryChart : Control
{
    public static readonly StyledProperty<PlayerHistoryReply?> HistoryProperty =
        AvaloniaProperty.Register<HistoryChart, PlayerHistoryReply?>(nameof(History));
    public static readonly StyledProperty<int> RangeHoursProperty =
        AvaloniaProperty.Register<HistoryChart, int>(nameof(RangeHours), 24);

    public PlayerHistoryReply? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public int RangeHours { get => GetValue(RangeHoursProperty); set => SetValue(RangeHoursProperty, value); }

    static HistoryChart() => AffectsRender<HistoryChart>(HistoryProperty, RangeHoursProperty);

    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A00"));
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#FF7A00"), 0.22);
    private static readonly IBrush DownFill = new SolidColorBrush(Color.Parse("#2A2A31"), 0.55);
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A8A93"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#ECECEF"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#2A2A31")), 1);
    private static readonly IPen MaxPen = new Pen(new SolidColorBrush(Color.Parse("#4A4A55")), 1, new DashStyle([4, 4], 0));
    private static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#FF7A00")), 2) { LineJoin = PenLineJoin.Round };
    private static readonly IPen Cursor = new Pen(new SolidColorBrush(Color.Parse("#ECECEF"), 0.5), 1);

    private const double Left = 34, Right = 10, Top = 26, Bottom = 22;
    private Point? _mouse;

    protected override void OnPointerMoved(PointerEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); base.OnPointerMoved(e); }
    protected override void OnPointerExited(PointerEventArgs e) { _mouse = null; InvalidateVisual(); base.OnPointerExited(e); }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Brushes.Transparent, new Rect(size)); // so the mouse is seen everywhere
        var h = History;
        var plot = new Rect(Left, Top, Math.Max(10, size.Width - Left - Right), Math.Max(10, size.Height - Top - Bottom));
        if (h == null || h.Players.Count == 0) return;

        var now = DateTimeOffset.Now;
        var from = now.AddHours(-Math.Max(1, RangeHours));
        double X(DateTimeOffset t) => plot.Left + (t - from).TotalSeconds / (now - from).TotalSeconds * plot.Width;

        // Samples in range.
        var points = new List<(DateTimeOffset T, int P)>();
        for (int i = 0; i < h.Players.Count; i++)
        {
            var t = h.TimeAt(i);
            if (t >= from && t <= now.AddMinutes(1)) points.Add((t, h.Players[i]));
        }
        int peak = points.Count == 0 ? 0 : points.Max(p => p.P);
        int yMax = Math.Max(1, h.MaxPlayers > 0 ? Math.Max(h.MaxPlayers, peak) : NiceCeiling(peak));
        double Y(int p) => plot.Bottom - (double)p / yMax * plot.Height;

        // Grid: 0, half, full.
        foreach (var v in new[] { 0, yMax / 2, yMax }.Distinct())
        {
            ctx.DrawLine(Grid, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            Text(ctx, v.ToString(CultureInfo.CurrentCulture), new Point(plot.Left - 6, Y(v)), Muted, 10, alignRight: true, center: true);
        }
        if (h.MaxPlayers > 0)
            ctx.DrawLine(MaxPen, new Point(plot.Left, Y(h.MaxPlayers)), new Point(plot.Right, Y(h.MaxPlayers)));

        // Time labels: every 3 hours for a day, each midnight for a week.
        if (RangeHours <= 48)
        {
            var t = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, from.Offset).AddHours(1);
            while (t.Hour % 3 != 0) t = t.AddHours(1);
            for (; t < now; t = t.AddHours(3))
                Text(ctx, t.ToString("h tt", CultureInfo.InvariantCulture), new Point(X(t), plot.Bottom + 4), Muted, 10, centerX: true);
        }
        else
        {
            for (var d = new DateTimeOffset(from.Date.AddDays(1), from.Offset); d < now; d = d.AddDays(1))
            {
                ctx.DrawLine(Grid, new Point(X(d), plot.Top), new Point(X(d), plot.Bottom));
                double labelX = X(d.AddHours(12));
                if (labelX < plot.Right - 12)
                    Text(ctx, d.ToString("ddd", CultureInfo.CurrentCulture), new Point(labelX, plot.Bottom + 4), Muted, 10, centerX: true);
            }
        }

        // Down time as grey bands; each run of readings as an orange area.
        double step = h.StepSeconds / (now - from).TotalSeconds * plot.Width;
        var run = new List<Point>();
        void FlushRun()
        {
            if (run.Count == 0) return;
            if (run.Count == 1) run.Add(new Point(run[0].X + Math.Max(2, step), run[0].Y));
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(run[0].X, plot.Bottom), true);
                foreach (var p in run) g.LineTo(p);
                g.LineTo(new Point(run[^1].X, plot.Bottom));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Fill, null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(run[0], false);
                foreach (var p in run.Skip(1)) g.LineTo(p);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, Line, line);
            run.Clear();
        }
        bool week = RangeHours > 48;
        for (int i = 0; i < points.Count; i++)
        {
            var (t, p) = points[i];
            // On the week view a single missed reading (a quick restart) would just be noise.
            if (p < 0 && week && i > 0 && i < points.Count - 1 && points[i - 1].P >= 0 && points[i + 1].P >= 0) continue;
            if (p < 0)
            {
                FlushRun();
                ctx.FillRectangle(DownFill, new Rect(X(t), plot.Top, Math.Max(1, step), plot.Height));
                continue;
            }
            run.Add(new Point(X(t), Y(p)));
        }
        FlushRun();

        // What's under the mouse.
        if (_mouse is { } m && plot.Contains(m) && points.Count > 0)
        {
            var near = points.MinBy(p => Math.Abs(X(p.T) - m.X));
            double x = X(near.T);
            ctx.DrawLine(Cursor, new Point(x, plot.Top), new Point(x, plot.Bottom));
            if (near.P >= 0) ctx.DrawEllipse(Orange, null, new Point(x, Y(near.P)), 4, 4);
            var when = near.T.ToLocalTime().ToString(RangeHours <= 48 ? "h:mm tt" : "ddd h:mm tt", CultureInfo.CurrentCulture);
            var what = near.P < 0 ? "server down" : $"{near.P} player{(near.P == 1 ? "" : "s")}";
            Text(ctx, $"{when}  ·  {what}", new Point(Math.Clamp(x, plot.Left + 70, plot.Right - 70), 4), TextBrush, 12, centerX: true);
        }
    }

    private static int NiceCeiling(int v) => v <= 10 ? 10 : (int)(Math.Ceiling(v / 10.0) * 10);

    private static void Text(DrawingContext ctx, string text, Point at, IBrush brush, double size,
                             bool alignRight = false, bool center = false, bool centerX = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        double x = alignRight ? at.X - ft.Width : centerX ? at.X - ft.Width / 2 : at.X;
        double y = center ? at.Y - ft.Height / 2 : at.Y;
        ctx.DrawText(ft, new Point(x, y));
    }
}
