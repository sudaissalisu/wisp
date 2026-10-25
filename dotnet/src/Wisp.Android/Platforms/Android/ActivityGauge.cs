using Microsoft.Maui.Graphics;

namespace Wisp.Android;

/// <summary>
/// Draws the header activity gauge: a progress ring while transfers are active,
/// or a simple presence dot when the engine is idle.
/// </summary>
public sealed class ActivityGaugeDrawable : IDrawable
{
    private float _progress;
    private bool _active;
    private bool _online;
    private int _fileCount;
    private float _pulse;

    public void SetState(bool active, bool online, float progress, int fileCount, float pulse)
    {
        _active = active;
        _online = online;
        _progress = Math.Clamp(progress, 0f, 1f);
        _fileCount = fileCount;
        _pulse = pulse;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var size = Math.Min(dirtyRect.Width, dirtyRect.Height);
        var center = new PointF(dirtyRect.Center.X, dirtyRect.Center.Y);

        if (!_active)
        {
            var dotRadius = size * 0.20f;
            canvas.FillColor = _online ? Color.FromArgb("#45E3B4") : Color.FromArgb("#66726F");
            canvas.FillCircle(center.X, center.Y, dotRadius);
            return;
        }

        var radius = size * 0.40f;
        var stroke = size * 0.10f;

        // Track
        canvas.StrokeColor = _online
            ? Color.FromArgb("#26FFFFFF")
            : Color.FromArgb("#4066726F");
        canvas.StrokeSize = stroke;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawArc(
            center.X - radius, center.Y - radius,
            radius * 2, radius * 2,
            -90, 360, false, false);

        // Progress arc
        if (_progress > 0.001f)
        {
            canvas.StrokeColor = Color.FromArgb("#45E3B4");
            canvas.StrokeSize = stroke;
            canvas.DrawArc(
                center.X - radius, center.Y - radius,
                radius * 2, radius * 2,
                -90, 360f * _progress, false, false);
        }

        // Breathing pulse behind the ring
        if (_pulse > 0.01f)
        {
            canvas.StrokeSize = 1.4f;
            canvas.StrokeColor = Color.FromArgb("#45E3B4").WithAlpha((float)(0.35 * _pulse));
            var pulseRadius = radius + stroke * 0.8f + size * 0.12f * _pulse;
            canvas.DrawArc(
                center.X - pulseRadius, center.Y - pulseRadius,
                pulseRadius * 2, pulseRadius * 2,
                -90, 360, false, false);
        }

        // File count inside the ring
        canvas.FontColor = _online
            ? Color.FromArgb("#F5F8F7")
            : Color.FromArgb("#9AA6A3");
        canvas.FontSize = (int)Math.Max(8, size * 0.30f);
        canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
        var text = _fileCount > 0 ? _fileCount.ToString() : "\u2022";
        canvas.DrawString(text, center.X, center.Y, size, size,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }
}
