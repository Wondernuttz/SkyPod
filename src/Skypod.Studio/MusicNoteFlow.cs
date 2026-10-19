using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Skypod.Studio;

/// <summary>Upright gold notes following flowing ribbons across the Atlas launcher.</summary>
public sealed class MusicNoteFlow : FrameworkElement
{
    private static readonly ImageSource SingleNote = LoadNote("Skypod-Note-Single.png");
    private static readonly ImageSource PairedNotes = LoadNote("Skypod-Note-Pair.png");
    private static readonly double[] Phases = [.04, .24, .44, .64, .84];
    private static readonly Brush EdgeFade = MakeGradient(
        new Point(0, 0), new Point(1, 0),
        ("#00FFFFFF", 0), ("#FFFFFFFF", .12), ("#FFFFFFFF", .88), ("#00FFFFFF", 1));
    private static readonly Brush VioletRibbon = MakeGradient(
        new Point(0, 0), new Point(0, 1),
        ("#8AAA55FF", 0), ("#384B1877", .5), ("#AD9B4BE8", 1));
    private static readonly Brush GoldRibbon = MakeGradient(
        new Point(0, 0), new Point(0, 1),
        ("#00FFD370", 0), ("#58FFC74E", .4), ("#BAFFE5A1", .55), ("#16D9952E", 1));
    private static readonly Pen VioletGlow = MakePen("#247F33CD", 20);
    private static readonly Pen GoldGlow = MakePen("#22FFD067", 9);
    private static readonly Pen GoldCore = MakePen("#CEFFE6A2", 1.2);
    private static readonly Pen VioletCore = MakePen("#8CC889FF", .8);
    private static readonly Pen FineGold = MakePen("#85EFC26D", .6);
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(1000d / 30)
    };
    private double _seconds;

    public MusicNoteFlow()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _timer.Tick += (_, _) =>
        {
            _seconds = _clock.Elapsed.TotalSeconds;
            InvalidateVisual();
        };
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += MotionSettingChanged;
            UpdateMotion();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= MotionSettingChanged;
            _timer.Stop();
            _clock.Stop();
        };
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    private void MotionSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateMotion();
    }

    private void UpdateMotion()
    {
        if (IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            _clock.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var time = SystemParameters.ClientAreaAnimation ? _seconds : 0;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));

        dc.PushOpacityMask(EdgeFade);
        var mainWave = WaveGeometry(time, 0);
        var lowerWave = WaveGeometry(time, 1);
        dc.DrawGeometry(null, VioletGlow, lowerWave);
        dc.DrawGeometry(VioletRibbon, null, WaveGeometry(time, 1, 7));
        dc.DrawGeometry(VioletRibbon, null, WaveGeometry(time, -1, 4));
        dc.DrawGeometry(null, GoldGlow, mainWave);
        dc.DrawGeometry(GoldRibbon, null, WaveGeometry(time, 0, 4));
        for (var lane = -3; lane <= 3; lane++)
            dc.DrawGeometry(null, lane % 2 == 0 ? FineGold : VioletCore, WaveGeometry(time, lane * .6));
        dc.DrawGeometry(null, GoldCore, mainWave);
        dc.Pop();

        for (var i = 0; i < Phases.Length; i++)
        {
            var pair = i % 2 == 1;
            var size = Math.Min(ActualHeight * .72, pair ? 66 : 61);
            // A shared travel period keeps the notes from overtaking and bunching together.
            var phase = (Phases[i] + time / 21) % 1;
            var x = phase * (ActualWidth + size * 2) - size;
            // Share the wave's vertical motion without rotating the note or its stem.
            var y = Math.Clamp(WaveY(x + size / 2, time, 0) - size * .56,
                1, Math.Max(1, ActualHeight - size - 1));
            var fade = Math.Clamp(Math.Min((x + size) / size, (ActualWidth - x) / size), 0, 1);
            dc.PushOpacity(fade);
            dc.DrawImage(pair ? PairedNotes : SingleNote, new Rect(x, y, size, size));
            dc.Pop();
        }
        dc.Pop();
    }

    private double WaveY(double x, double time, double lane)
    {
        var travel = x / Math.Max(1, ActualWidth) * Math.Tau - time * Math.Tau / 21;
        var amplitude = Math.Min(13, ActualHeight * .14);
        return ActualHeight * .55 + Math.Sin(travel + lane * .25) * amplitude
            + Math.Sin(travel * 1.8 + time * .38 + lane * .45) * amplitude * .38 + lane * 4;
    }

    private StreamGeometry WaveGeometry(double time, double lane, double halfWidth = 0)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var segments = Math.Max(24, (int)Math.Ceiling(ActualWidth / 6));
            for (var i = 0; i <= segments; i++)
            {
                var x = i * ActualWidth / segments;
                var width = halfWidth * (.65 + .35 * Math.Sin(x / ActualWidth * Math.PI));
                var point = new Point(x, WaveY(x, time, lane) - width);
                if (i == 0) path.BeginFigure(point, halfWidth > 0, halfWidth > 0);
                else path.LineTo(point, true, false);
            }
            if (halfWidth > 0)
                for (var i = segments; i >= 0; i--)
                {
                    var x = i * ActualWidth / segments;
                    var width = halfWidth * (.65 + .35 * Math.Sin(x / ActualWidth * Math.PI));
                    path.LineTo(new Point(x, WaveY(x, time, lane) + width), true, false);
                }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Brush MakeGradient(Point start, Point end, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(string color, double width)
    {
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), width);
        pen.Freeze();
        return pen;
    }

    private static ImageSource LoadNote(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Skypod.Studio;component/Assets/{name}");
        image.DecodePixelWidth = 192;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
