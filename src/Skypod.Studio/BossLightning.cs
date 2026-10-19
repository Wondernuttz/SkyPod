using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Skypod.Studio;

/// <summary>Occasional gold lightning behind the Bosses launcher.</summary>
public sealed class BossLightning : FrameworkElement
{
    private const double StrikeSeconds = .72;
    private static readonly Pen Halo = MakePen("#12FFC34D", 15);
    private static readonly Pen Glow = MakePen("#35FFD16B", 6);
    private static readonly Pen Gold = MakePen("#E8FFD16B", 2);
    private static readonly Pen Core = MakePen("#FFFDE9B6", .7);
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Stopwatch _strikeClock = new();
    private StreamGeometry? _bolt;
    private Window? _host;
    private double _lastStart = .5;

    public BossLightning()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _timer.Tick += Tick;
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += MotionSettingChanged;
            _host = Window.GetWindow(this);
            if (_host is not null) _host.StateChanged += HostStateChanged;
            UpdateMotion();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= MotionSettingChanged;
            if (_host is not null) _host.StateChanged -= HostStateChanged;
            _host = null;
            StopMotion();
        };
        IsVisibleChanged += (_, _) => UpdateMotion();
        IsEnabledChanged += (_, _) => UpdateMotion();
        SizeChanged += (_, _) =>
        {
            // A resize must not stretch a bolt beyond the new rounded card.
            if (_strikeClock.IsRunning) EndStrike();
        };
    }

    private bool CanAnimate => IsLoaded && IsVisible && IsEnabled
        && SystemParameters.ClientAreaAnimation && _host?.WindowState != WindowState.Minimized;

    private void MotionSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateMotion();
    }

    private void HostStateChanged(object? sender, EventArgs e) => UpdateMotion();

    private void UpdateMotion()
    {
        if (!CanAnimate) StopMotion();
        else if (!_timer.IsEnabled)
        {
            _timer.Interval = TimeSpan.FromSeconds(1.5 + Random.Shared.NextDouble());
            _timer.Start();
        }
    }

    private void StopMotion()
    {
        _timer.Stop();
        _strikeClock.Reset();
        _bolt = null;
        InvalidateVisual();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!CanAnimate) { StopMotion(); return; }
        if (!_strikeClock.IsRunning)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            _bolt = CreateBolt();
            _strikeClock.Restart();
            _timer.Interval = TimeSpan.FromMilliseconds(1000d / 30);
        }
        else if (_strikeClock.Elapsed.TotalSeconds >= StrikeSeconds) EndStrike();
        InvalidateVisual();
    }

    private void EndStrike()
    {
        _strikeClock.Reset();
        _bolt = null;
        _timer.Interval = TimeSpan.FromSeconds(4 + Random.Shared.NextDouble() * 4);
        InvalidateVisual();
    }

    private StreamGeometry CreateBolt()
    {
        var start = .12 + Random.Shared.NextDouble() * .76;
        if (Math.Abs(start - _lastStart) < .22) start = .12 + ((start - .12 + .38) % .76);
        _lastStart = start;
        var direction = start > .5 ? -1 : 1;
        var end = Math.Clamp(start + direction * (.2 + Random.Shared.NextDouble() * .28), .08, .92);
        var points = new Point[8];
        for (var i = 0; i < points.Length; i++)
        {
            var progress = i / (double)(points.Length - 1);
            var jag = i == 0 || i == points.Length - 1 ? 0 : (Random.Shared.NextDouble() - .5) * .13;
            points[i] = new Point((start + (end - start) * progress + jag) * ActualWidth,
                progress * ActualHeight);
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Length; i++) path.LineTo(points[i], true, false);
            var fork = points[3];
            path.BeginFigure(fork, false, false);
            path.LineTo(new Point(fork.X - direction * ActualWidth * .09, fork.Y + ActualHeight * .12), true, false);
            path.LineTo(new Point(fork.X - direction * ActualWidth * .06, fork.Y + ActualHeight * .21), true, false);
            path.LineTo(new Point(fork.X - direction * ActualWidth * .17, fork.Y + ActualHeight * .34), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_bolt is null || !_strikeClock.IsRunning || !CanAnimate) return;
        var seconds = _strikeClock.Elapsed.TotalSeconds;
        var reveal = Math.Clamp(seconds / .12, 0, 1);
        var fade = Math.Pow(Math.Clamp(1 - seconds / StrikeSeconds, 0, 1), 1.4);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize), 8, 8));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight * reveal)));
        dc.PushOpacity(.82 * fade);
        dc.DrawGeometry(null, Halo, _bolt);
        dc.DrawGeometry(null, Glow, _bolt);
        dc.DrawGeometry(null, Gold, _bolt);
        dc.DrawGeometry(null, Core, _bolt);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static Pen MakePen(string color, double width)
    {
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
