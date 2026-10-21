using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Matriosk;

/// <summary>
/// Two-thumb slider: move both handles together for a fixed value, or drag them apart
/// to pick a random range. Dragging from a fixed value expands in the drag direction.
/// </summary>
public sealed class RangeSlider : Control
{
    private const double ThumbRadius = 5;
    private const double Pad = 6;

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x1E, 0x37, 0x46));
    private static readonly Brush ActiveBrush = Frozen(Color.FromRgb(0x9F, 0xD8, 0xEF));
    private static readonly Brush ThumbBrush = Frozen(Color.FromRgb(0xEA, 0xF7, 0xFF));

    private int dragging = -1;
    private bool expandFromFixed;

    public double Minimum { get; private set; }
    public double Maximum { get; private set; } = 1;
    public double Low { get; private set; }
    public double High { get; private set; }

    public event EventHandler? RangeChanged;

    public RangeSlider()
    {
        Height = 18;
        MinWidth = 60;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public void SetRange(double low, double high, double minimum, double maximum)
    {
        Minimum = minimum;
        Maximum = maximum > minimum ? maximum : minimum + 1e-6;
        Low = Math.Clamp(low, Minimum, Maximum);
        High = Math.Clamp(high, Minimum, Maximum);
        if (High < Low) (Low, High) = (High, Low);

        // Note: the "expand from fixed" direction is decided on mouse down, not here —
        // SetRange is also called mid-drag and must not reset it.
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= Pad * 2) return;

        var cy = ActualHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(Pad, cy - 2, width - Pad * 2, 4), 2, 2);

        var x1 = ToX(Low);
        var x2 = ToX(High);
        if (x2 - x1 > 0.5)
        {
            dc.DrawRoundedRectangle(ActiveBrush, null, new Rect(x1, cy - 2, x2 - x1, 4), 2, 2);
        }

        dc.DrawEllipse(ThumbBrush, null, new Point(x1, cy), ThumbRadius, ThumbRadius);
        dc.DrawEllipse(ThumbBrush, null, new Point(x2, cy), ThumbRadius, ThumbRadius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var value = ToValue(e.GetPosition(this).X);
        dragging = Math.Abs(value - Low) <= Math.Abs(value - High) ? 0 : 1;
        expandFromFixed = Math.Abs(High - Low) < 1e-9;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        DragTo(ToValue(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging < 0) return;
        dragging = -1;
        ReleaseMouseCapture();
    }

    private void DragTo(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);

        if (expandFromFixed)
        {
            // Both handles start together: grow in the direction of the drag.
            if (value >= Low) High = value;
            else Low = value;
        }
        else if (dragging == 0)
        {
            Low = Math.Min(value, High);
        }
        else
        {
            High = Math.Max(value, Low);
        }

        InvalidateVisual();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private double ToX(double value) =>
        Pad + (value - Minimum) / (Maximum - Minimum) * (ActualWidth - Pad * 2);

    private double ToValue(double x) =>
        Minimum + (x - Pad) / Math.Max(1, ActualWidth - Pad * 2) * (Maximum - Minimum);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
