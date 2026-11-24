using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Walkman.L3.AppShell.Controls;

/// <summary>
/// Smooth, low-overhead marquee text control that only animates when text overflows its bounds.
/// </summary>
public sealed class MarqueeTextBlock : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string?>(nameof(Text), string.Empty);

    public static readonly StyledProperty<double> SpeedPxPerSecProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(SpeedPxPerSec), 28.0);

    public static readonly StyledProperty<double> PauseSecondsProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(PauseSeconds), 2.0);

    public static readonly StyledProperty<double> GapPixelsProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(GapPixels), 44.0);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(FontSize), 13.0);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, IBrush?>(nameof(Foreground), Brushes.Black);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double SpeedPxPerSec
    {
        get => GetValue(SpeedPxPerSecProperty);
        set => SetValue(SpeedPxPerSecProperty, value);
    }

    public double PauseSeconds
    {
        get => GetValue(PauseSecondsProperty);
        set => SetValue(PauseSecondsProperty, value);
    }

    public double GapPixels
    {
        get => GetValue(GapPixelsProperty);
        set => SetValue(GapPixelsProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _timer;
    private double _textWidth;
    private double _textHeight = 16.0;
    private double _offset;
    private double _pauseRemaining;
    private double _lastTickSec;
    private bool _isAttached;
    private FormattedText? _cachedFormattedText;
    private Window? _hostWindow;

    static MarqueeTextBlock()
    {
        AffectsMeasure<MarqueeTextBlock>(TextProperty, FontSizeProperty, FontWeightProperty);
        AffectsRender<MarqueeTextBlock>(TextProperty, FontSizeProperty, FontWeightProperty, ForegroundProperty);
        TextProperty.Changed.AddClassHandler<MarqueeTextBlock>((c, _) => c.OnTextOrLayoutChanged(resetOffset: true));
        BoundsProperty.Changed.AddClassHandler<MarqueeTextBlock>((c, _) => c.OnTextOrLayoutChanged(resetOffset: false));
        IsVisibleProperty.Changed.AddClassHandler<MarqueeTextBlock>((c, _) => c.UpdateTimerState());
    }

    public MarqueeTextBlock()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }
        UpdateTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }
        StopTimer();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateTimerState();
        }
    }

    private FormattedText GetOrCreateFormattedText()
    {
        return _cachedFormattedText ??= new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight),
            FontSize,
            Foreground ?? Brushes.Black);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = GetOrCreateFormattedText();
        _textWidth = ft.Width;
        _textHeight = Math.Max(14.0, ft.Height);
        double w = double.IsInfinity(availableSize.Width) ? _textWidth : availableSize.Width;
        return new Size(w, _textHeight);
    }

    private void OnTextOrLayoutChanged(bool resetOffset)
    {
        _cachedFormattedText = null;
        var ft = GetOrCreateFormattedText();
        _textWidth = ft.Width;
        _textHeight = Math.Max(14.0, ft.Height);
        if (resetOffset)
        {
            _offset = 0;
            _pauseRemaining = PauseSeconds;
        }
        UpdateTimerState();
        InvalidateVisual();
    }

    private void UpdateTimerState()
    {
        bool isMinimized = _hostWindow?.WindowState == WindowState.Minimized;
        bool needsScroll = _isAttached && IsVisible && !isMinimized && Bounds.Width > 4 && _textWidth > Bounds.Width + 1.0;
        if (!needsScroll)
        {
            _offset = 0;
            StopTimer();
            return;
        }

        if (_timer is null)
        {
            _lastTickSec = _clock.Elapsed.TotalSeconds;
            _pauseRemaining = PauseSeconds;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    private void StopTimer()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTickSec, 0.001, 0.1);
        _lastTickSec = now;

        if (_textWidth <= Bounds.Width + 1.0)
        {
            StopTimer();
            _offset = 0;
            InvalidateVisual();
            return;
        }

        if (_pauseRemaining > 0)
        {
            _pauseRemaining -= dt;
            return;
        }

        _offset += SpeedPxPerSec * dt;
        double cycle = _textWidth + GapPixels;
        if (_offset >= cycle)
        {
            _offset = 0;
            _pauseRemaining = PauseSeconds;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var ft = GetOrCreateFormattedText();
        double y = Math.Max(0, (Bounds.Height - ft.Height) * 0.5);
        if (ft.Width <= Bounds.Width + 1.0)
        {
            context.DrawText(ft, new Point(0, y));
            return;
        }

        double x1 = -_offset;
        context.DrawText(ft, new Point(x1, y));

        double x2 = x1 + ft.Width + GapPixels;
        if (x2 < Bounds.Width)
        {
            context.DrawText(ft, new Point(x2, y));
        }
    }
}
