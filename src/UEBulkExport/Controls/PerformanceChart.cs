using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Gui.Controls;

/// <summary>Which measures a chart plots; each chart has one unit, never two axes.</summary>
public enum PerformanceChartKind { Disk, Load }

/// <summary>
/// An area chart of a run's hardware readings. Thin 2px lines over a faint wash, hairline grid,
/// clean axis ticks, and a crosshair that snaps to the nearest reading and lists every series.
/// Colours come from theme resources, so light and dark each get their own validated steps.
/// </summary>
public sealed class PerformanceChart : Control
{
    public static readonly StyledProperty<PerformanceViewModel?> SourceProperty =
        AvaloniaProperty.Register<PerformanceChart, PerformanceViewModel?>(nameof(Source));
    public static readonly StyledProperty<PerformanceChartKind> KindProperty =
        AvaloniaProperty.Register<PerformanceChart, PerformanceChartKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> CpuBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(CpuBrush));
    public static readonly StyledProperty<IBrush?> MemoryBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(MemoryBrush));
    public static readonly StyledProperty<IBrush?> GpuBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(GpuBrush));
    public static readonly StyledProperty<IBrush?> ReadBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(ReadBrush));
    public static readonly StyledProperty<IBrush?> WriteBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(WriteBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<PerformanceChart, IBrush?>(nameof(OutlineBrush));

    public PerformanceViewModel? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public PerformanceChartKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? CpuBrush { get => GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }
    public IBrush? MemoryBrush { get => GetValue(MemoryBrushProperty); set => SetValue(MemoryBrushProperty, value); }
    public IBrush? GpuBrush { get => GetValue(GpuBrushProperty); set => SetValue(GpuBrushProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? WriteBrush { get => GetValue(WriteBrushProperty); set => SetValue(WriteBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IBrush? OutlineBrush { get => GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty, value); }

    private const double AxisFontSize = 11;
    private const double TopPad = 10, RightPad = 6, BottomGutter = 22, LabelGap = 8;
    private double? _hoverX;

    private sealed record Series(string Title, IBrush Brush, Func<HardwareSample, double?> Value);

    static PerformanceChart()
    {
        AffectsRender<PerformanceChart>(KindProperty, CpuBrushProperty, MemoryBrushProperty, GpuBrushProperty,
            ReadBrushProperty, WriteBrushProperty, GridBrushProperty, TextBrushProperty, MutedBrushProperty,
            SurfaceBrushProperty, OutlineBrushProperty);
    }

    public PerformanceChart()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;

        if (change.OldValue is PerformanceViewModel old) old.SamplesChanged -= InvalidateVisual;
        if (change.NewValue is PerformanceViewModel source) source.SamplesChanged += InvalidateVisual;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Source is { } source) source.SamplesChanged -= InvalidateVisual;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Source is { } source)
        {
            source.SamplesChanged -= InvalidateVisual;
            source.SamplesChanged += InvalidateVisual;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- series

    private List<Series> SeriesFor(PerformanceViewModel source)
    {
        var loc = Loc.Instance;
        if (Kind == PerformanceChartKind.Disk)
            return
            [
                new(loc["Perf.Read"], ReadBrush ?? Brushes.SeaGreen, s => s.DiskReadBytesPerSecond / 1_000_000),
                new(loc["Perf.Write"], WriteBrush ?? Brushes.SlateBlue, s => s.DiskWriteBytesPerSecond / 1_000_000)
            ];

        var series = new List<Series>
        {
            new(loc["Perf.Cpu"], CpuBrush ?? Brushes.RoyalBlue, s => s.CpuPercent),
            new(loc["Perf.Memory"], MemoryBrush ?? Brushes.HotPink, s => s.MemoryPercent)
        };
        if (source.HasGpu) series.Add(new(loc["Perf.Gpu"], GpuBrush ?? Brushes.Goldenrod, s => s.GpuPercent));
        return series;
    }

    private string FormatValue(double value) => Kind == PerformanceChartKind.Disk
        ? PerformanceViewModel.Rate(value * 1_000_000)
        : PerformanceViewModel.Percent(value);

    private string FormatTick(double value) => value.ToString(value < 10 && value % 1 != 0 ? "0.#" : "0",
        CultureInfo.CurrentUICulture);

    /// <summary>A ceiling on a 1-2-2.5-5 ladder, so the ticks read as round numbers.</summary>
    private static double NiceCeiling(double max)
    {
        if (max <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= max) return step * magnitude;
        return 10 * magnitude;
    }

    // ---------------------------------------------------------------- drawing

    private FormattedText Text(string text, double size, IBrush? brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, weight), size,
            brush ?? Brushes.Gray);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 80 || bounds.Height < 60 || Source is not { } source) return;

        var samples = source.Samples;
        var series = SeriesFor(source);

        // Scale: percentages run 0-100 in quarters; throughput takes a round step (1-2-2.5-5 ladder)
        // and as many of them as the data needs, at most five, so every tick is a clean number.
        var dataMax = samples.Count == 0 ? 0 : series.Max(s => samples.Max(p => s.Value(p) ?? 0));
        double yMax;
        int ticks;
        if (Kind == PerformanceChartKind.Load)
        {
            (yMax, ticks) = (100, 4);
        }
        else
        {
            var top = Math.Max(dataMax * 1.05, 1);
            var step = NiceCeiling(top / 5);
            ticks = Math.Max(2, (int)Math.Ceiling(top / step));
            yMax = step * ticks;
        }

        var widestTick = Enumerable.Range(0, ticks + 1)
            .Max(i => Text(FormatTick(yMax / ticks * i), AxisFontSize, MutedBrush).Width);
        var plot = new Rect(widestTick + LabelGap, TopPad,
            Math.Max(10, bounds.Width - widestTick - LabelGap - RightPad),
            Math.Max(10, bounds.Height - TopPad - BottomGutter));

        double Y(double value) => plot.Bottom - Math.Clamp(value / yMax, 0, 1) * plot.Height;

        // Hairline grid and its ticks: recessive, solid, one step off the surface.
        var gridPen = new Pen(GridBrush ?? Brushes.LightGray, 1);
        for (var i = 0; i <= ticks; i++)
        {
            var value = yMax / ticks * i;
            var y = Math.Round(Y(value)) + 0.5;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(FormatTick(value), AxisFontSize, MutedBrush);
            context.DrawText(label, new Point(plot.Left - LabelGap - label.Width, y - label.Height / 2));
        }

        if (samples.Count == 0)
        {
            var waiting = Text(Loc.Instance["Perf.Waiting"], 12, MutedBrush);
            context.DrawText(waiting, new Point(plot.Center.X - waiting.Width / 2, plot.Center.Y - waiting.Height / 2));
            return;
        }

        // Time runs from the start of the run to the latest reading.
        var span = Math.Max(1, samples[^1].Elapsed.TotalSeconds);
        double X(HardwareSample s) => plot.Left + s.Elapsed.TotalSeconds / span * plot.Width;

        foreach (var (seconds, align) in new[] { (0.0, 0.0), (span / 2, 0.5), (span, 1.0) })
        {
            var label = Text(Converters.Format.Duration(TimeSpan.FromSeconds(seconds)), AxisFontSize, MutedBrush);
            var x = plot.Left + seconds / span * plot.Width - label.Width * align;
            context.DrawText(label, new Point(x, plot.Bottom + 5));
        }

        using (context.PushClip(plot.Inflate(new Thickness(0, 2, 0, 0))))
        {
            // Washes first, lines on top, so no fill ever covers another series' line.
            foreach (var s in series) DrawArea(context, samples, s, X, Y, plot);
            foreach (var s in series) DrawLine(context, samples, s, X, Y);
        }

        if (_hoverX is { } hover && hover >= plot.Left - 4 && hover <= plot.Right + 4)
            DrawCrosshair(context, samples, series, X, Y, plot, hover);
    }

    private static IEnumerable<Point> Points(IReadOnlyList<HardwareSample> samples, Series series,
        Func<HardwareSample, double> x, Func<double, double> y)
    {
        foreach (var sample in samples)
            if (series.Value(sample) is { } value)
                yield return new Point(x(sample), y(value));
    }

    private static Color ColorOf(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    private static void DrawArea(DrawingContext context, IReadOnlyList<HardwareSample> samples, Series series,
        Func<HardwareSample, double> x, Func<double, double> y, Rect plot)
    {
        var points = Points(samples, series, x, y).ToList();
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(points[0].X, plot.Bottom), true);
            foreach (var p in points) g.LineTo(p);
            g.LineTo(new Point(points[^1].X, plot.Bottom));
            g.EndFigure(true);
        }

        // A wash, never a block: the hue fades from about a sixth to almost nothing.
        var color = ColorOf(series.Brush);
        var wash = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(46, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(6, color.R, color.G, color.B), 1)
            }
        };
        context.DrawGeometry(wash, null, geometry);
    }

    private static void DrawLine(DrawingContext context, IReadOnlyList<HardwareSample> samples, Series series,
        Func<HardwareSample, double> x, Func<double, double> y)
    {
        var points = Points(samples, series, x, y).ToList();
        var pen = new Pen(series.Brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (points.Count == 1)
        {
            context.DrawEllipse(series.Brush, null, points[0], 3, 3);
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            for (var i = 1; i < points.Count; i++) g.LineTo(points[i]);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// The crosshair finds the X: a hairline snapped to the nearest reading, a ringed dot on every
    /// series, and one readout listing them all, values first.
    /// </summary>
    private void DrawCrosshair(DrawingContext context, IReadOnlyList<HardwareSample> samples, List<Series> series,
        Func<HardwareSample, double> x, Func<double, double> y, Rect plot, double hover)
    {
        var nearest = samples.MinBy(s => Math.Abs(x(s) - hover))!;
        var cx = Math.Round(x(nearest)) + 0.5;

        var muted = ColorOf(MutedBrush ?? Brushes.Gray);
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(140, muted.R, muted.G, muted.B)), 1),
            new Point(cx, plot.Top), new Point(cx, plot.Bottom));

        var surface = SurfaceBrush ?? Brushes.White;
        var ring = new Pen(surface, 2);
        foreach (var s in series)
            if (s.Value(nearest) is { } value)
                context.DrawEllipse(s.Brush, ring, new Point(cx, y(value)), 4.5, 4.5);

        // Readout: the time, then one row per series with a short line key.
        var header = Text(Converters.Format.Duration(nearest.Elapsed), 11, MutedBrush);
        var rows = series
            .Where(s => s.Value(nearest) is not null)
            .Select(s => (s.Brush,
                Value: Text(FormatValue(s.Value(nearest)!.Value), 13, TextBrush, FontWeight.SemiBold),
                Title: Text(s.Title, 12, MutedBrush)))
            .ToList();

        const double pad = 10, keyWidth = 12, gap = 8, rowGap = 4;
        var valueWidth = rows.Max(r => r.Value.Width);
        var width = pad * 2 + Math.Max(header.Width, keyWidth + gap + valueWidth + gap + rows.Max(r => r.Title.Width));
        var rowHeight = rows.Max(r => Math.Max(r.Value.Height, r.Title.Height));
        var height = pad * 2 + header.Height + 6 + rows.Count * rowHeight + (rows.Count - 1) * rowGap;

        var left = cx + 14 + width <= Bounds.Width ? cx + 14 : cx - 14 - width;
        var top = Math.Clamp(plot.Top + 4, 0, Math.Max(0, Bounds.Height - height));
        var box = new Rect(Math.Max(0, left), top, width, height);

        context.DrawRectangle(surface, new Pen(OutlineBrush ?? Brushes.LightGray, 1), new RoundedRect(box, 8),
            new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 14, Color = Color.FromArgb(40, 0, 0, 0) }));
        context.DrawText(header, new Point(box.X + pad, box.Y + pad));

        var rowTop = box.Y + pad + header.Height + 6;
        foreach (var (brush, value, title) in rows)
        {
            var mid = rowTop + rowHeight / 2;
            context.DrawLine(new Pen(brush, 2.5, lineCap: PenLineCap.Round),
                new Point(box.X + pad, mid), new Point(box.X + pad + keyWidth, mid));
            context.DrawText(value, new Point(box.X + pad + keyWidth + gap, rowTop + (rowHeight - value.Height) / 2));
            context.DrawText(title, new Point(box.X + pad + keyWidth + gap + valueWidth + gap,
                rowTop + (rowHeight - title.Height) / 2));
            rowTop += rowHeight + rowGap;
        }
    }
}
