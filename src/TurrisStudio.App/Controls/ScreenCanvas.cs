using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TurrisStudio.Core.Overlay;
using TurrisStudio.Core.Sensors;

namespace TurrisStudio.Controls;

/// <summary>
/// Preview of the display (1280x720 Turris, 1440x720 Stage): background media + overlay widgets rendered
/// like the firmware (rows by line, labels side by side, aligned inside the widget box). Optional drag/select
/// editing. Dual-screen coolers show the gap between their two screens and can show one media per screen.
/// </summary>
public sealed class ScreenCanvas : Border
{
    /// <summary>One background area: a still frame plus an optional looping clip.</summary>
    private sealed class MediaSlot
    {
        public readonly Image Image = new() { Stretch = Stretch.UniformToFill };
        public readonly MediaElement Video = new()
        {
            Stretch = Stretch.UniformToFill, IsMuted = true,
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Visibility = Visibility.Collapsed,
        };

        public void Place(double x, double width, double height, bool visible)
        {
            foreach (var e in new FrameworkElement[] { Image, Video })
            {
                e.Width = Math.Max(1, width);
                e.Height = height;
                Canvas.SetLeft(e, x);
            }
            Image.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) Video.Visibility = Visibility.Collapsed;
        }

        public void Set(string? imagePath, string? videoPath, double decodeWidth)
        {
            Image.Source = LoadImage(imagePath, decodeWidth);
            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
            {
                var uri = new Uri(videoPath);
                if (Video.Source != uri) Video.Source = uri;
                Video.Visibility = Visibility.Visible;
                Video.Play();
            }
            else
            {
                Video.Stop();
                Video.Source = null;
                Video.Visibility = Visibility.Collapsed;
            }
        }
    }

    public double ScreenWidth { get; private set; } = 1280;
    public double ScreenHeight { get; private set; } = 720;
    /// <summary>True for coolers made of two screens side by side (Stage).</summary>
    public bool DualScreen { get; private set; }

    private readonly Canvas _canvas = new() { Width = 1280, Height = 720, ClipToBounds = true };
    private readonly MediaSlot _main = new();
    private readonly MediaSlot _right = new();
    private readonly System.Windows.Shapes.Rectangle _divider = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(235, 6, 6, 6)), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private readonly Canvas _gridLayer = new() { Width = 1280, Height = 720, IsHitTestVisible = false };
    private readonly Dictionary<OverlayWidgetModel, FrameworkElement> _widgetElements = new();
    private readonly DispatcherTimer _rebuildTimer;
    private bool _splitMedia;
    private OverlayDesign? _design;
    private OverlayWidgetModel? _selected;
    private OverlayWidgetModel? _dragging;
    private Point _dragStart;
    private (int x, int y, int w, int h) _dragOrigin;
    private bool _resizing;

    public SensorHub? Sensors { get; set; }
    public bool Interactive { get; set; }
    public int SnapSize { get; set; } = 10;
    public bool ShowGrid { get => _gridLayer.Visibility == Visibility.Visible; set => _gridLayer.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public event Action<OverlayWidgetModel?>? SelectionChanged;
    public event Action? DesignEdited;

    public ScreenCanvas()
    {
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        Background = Brushes.Black;
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        BorderThickness = new Thickness(1);

        foreach (var slot in new[] { _main, _right })
        {
            var video = slot.Video;
            _canvas.Children.Add(slot.Image);
            video.MediaEnded += (_, _) => { video.Position = TimeSpan.Zero; video.Play(); };
            video.MediaFailed += (_, _) => video.Visibility = Visibility.Collapsed;
            video.MouseLeftButtonDown += (_, _) => { if (Interactive) Select(null); };
            _canvas.Children.Add(video);
            Unloaded += (_, _) => video.Pause();
            Loaded += (_, _) => { if (video.Source != null) video.Play(); };
        }
        LayoutSlots();
        _main.Image.Source = DefaultBackground();
        _canvas.Children.Add(_divider);
        _canvas.Children.Add(_gridLayer);
        BuildGrid();
        ShowGrid = false;
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };

        _rebuildTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            _rebuildTimer!.Stop();
            Rebuild();
        }, Dispatcher);

        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (Interactive && (e.OriginalSource == _main.Image || e.OriginalSource == _right.Image)) Select(null);
        };
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvas.LostMouseCapture += (_, _) => EndDrag();
    }

    /// <summary>Sets the screen geometry; <paramref name="dualScreen"/> draws the gap between two screens.</summary>
    public void SetScreen(int width, int height, bool dualScreen)
    {
        if (ScreenWidth == width && ScreenHeight == height && DualScreen == dualScreen) return;
        ScreenWidth = width;
        ScreenHeight = height;
        DualScreen = dualScreen;
        _canvas.Width = width;
        _canvas.Height = height;
        _gridLayer.Width = width;
        _gridLayer.Height = height;
        _divider.Width = 10;
        _divider.Height = height;
        Canvas.SetLeft(_divider, width / 2.0 - 5);
        _divider.Visibility = dualScreen ? Visibility.Visible : Visibility.Collapsed;
        LayoutSlots();
        BuildGrid();
        ScheduleRebuild();
    }

    private void LayoutSlots()
    {
        if (_splitMedia && DualScreen)
        {
            _main.Place(0, ScreenWidth / 2, ScreenHeight, true);
            _right.Place(ScreenWidth / 2, ScreenWidth / 2, ScreenHeight, true);
        }
        else
        {
            _main.Place(0, ScreenWidth, ScreenHeight, true);
            _right.Place(0, 1, ScreenHeight, false);
        }
    }

    /// <summary>Shows the media behind the overlay: a still frame, plus the looping clip when there is one.</summary>
    public void SetBackground(string? imagePath, string? videoPath)
    {
        _splitMedia = false;
        LayoutSlots();
        _main.Set(imagePath, videoPath, ScreenWidth);
        _right.Set(null, null, 0);
        _main.Image.Source ??= DefaultBackground();
    }

    /// <summary>Dual-screen coolers in split mode: separate media for the left and right screens.</summary>
    public void SetSplitBackground(string? leftImage, string? leftVideo, string? rightImage, string? rightVideo)
    {
        _splitMedia = true;
        LayoutSlots();
        _main.Set(leftImage, leftVideo, ScreenWidth / 2);
        _right.Set(rightImage, rightVideo, ScreenWidth / 2);
        _main.Image.Source ??= DefaultBackground();
        _right.Image.Source ??= DefaultBackground();
    }

    public void SetBackgroundImage(string? path) => SetBackground(path, null);

    public OverlayDesign? Design
    {
        get => _design;
        set
        {
            if (_design == value) return;
            Detach(_design);
            _design = value;
            Attach(_design);
            _selected = null;
            ScheduleRebuild();
        }
    }

    public OverlayWidgetModel? Selected => _selected;

    public void Select(OverlayWidgetModel? widget)
    {
        if (_selected == widget) return;
        _selected = widget;
        ScheduleRebuild();
        SelectionChanged?.Invoke(widget);
    }

    private static ImageSource? LoadImage(string? path, double decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0) bmp.DecodePixelWidth = (int)decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource DefaultBackground()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(18, 24, 48), Color.FromRgb(70, 20, 60), 30);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, 640, 360));
        var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Re-render live values (call periodically).</summary>
    public void RefreshValues() => ScheduleRebuild();

    // ------------------------------------------------------------ model change tracking

    private void Attach(OverlayDesign? design)
    {
        if (design == null) return;
        design.PropertyChanged += OnModelChanged;
        design.Widgets.CollectionChanged += OnWidgetsChanged;
        foreach (var w in design.Widgets) AttachWidget(w);
    }

    private void Detach(OverlayDesign? design)
    {
        if (design == null) return;
        design.PropertyChanged -= OnModelChanged;
        design.Widgets.CollectionChanged -= OnWidgetsChanged;
        foreach (var w in design.Widgets) DetachWidget(w);
    }

    private void AttachWidget(OverlayWidgetModel w)
    {
        w.PropertyChanged += OnModelChanged;
        w.Labels.CollectionChanged += OnLabelsChanged;
        foreach (var l in w.Labels) l.PropertyChanged += OnModelChanged;
    }

    private void DetachWidget(OverlayWidgetModel w)
    {
        w.PropertyChanged -= OnModelChanged;
        w.Labels.CollectionChanged -= OnLabelsChanged;
        foreach (var l in w.Labels) l.PropertyChanged -= OnModelChanged;
    }

    private void OnWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (OverlayWidgetModel w in e.OldItems) DetachWidget(w);
        if (e.NewItems != null) foreach (OverlayWidgetModel w in e.NewItems) AttachWidget(w);
        if (e.Action == NotifyCollectionChangedAction.Reset && _design != null)
            foreach (var w in _design.Widgets) { DetachWidget(w); AttachWidget(w); }
        ScheduleRebuild(modelChanged: true);
    }

    private void OnLabelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (OverlayLabelModel l in e.OldItems) l.PropertyChanged -= OnModelChanged;
        if (e.NewItems != null) foreach (OverlayLabelModel l in e.NewItems) l.PropertyChanged += OnModelChanged;
        ScheduleRebuild(modelChanged: true);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRebuild(modelChanged: true);

    /// <summary>Raised whenever the design itself changes (not for live value refreshes).</summary>
    public event Action? ModelChanged;

    private void ScheduleRebuild(bool modelChanged = false)
    {
        if (modelChanged) ModelChanged?.Invoke();
        if (!_rebuildTimer.IsEnabled) _rebuildTimer.Start();
    }

    // ------------------------------------------------------------ rendering

    private void Rebuild()
    {
        foreach (var el in _widgetElements.Values) _canvas.Children.Remove(el);
        _widgetElements.Clear();
        if (_design == null) return;
        var now = DateTime.Now;

        foreach (var widget in _design.Widgets)
        {
            if (!widget.Visible && !Interactive) continue;
            var element = RenderWidget(widget, now);
            Canvas.SetLeft(element, widget.X);
            Canvas.SetTop(element, widget.Y);
            _canvas.Children.Add(element);
            _widgetElements[widget] = element;
        }
    }

    private FrameworkElement RenderWidget(OverlayWidgetModel widget, DateTime now)
    {
        var rows = new StackPanel { Orientation = Orientation.Vertical };
        var align = widget.Align switch
        {
            WidgetAlign.Center => HorizontalAlignment.Center,
            WidgetAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        var first = true;
        foreach (var line in widget.Labels.GroupBy(l => l.Line).OrderBy(g => g.Key))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align };
            if (!first) row.Margin = new Thickness(0, widget.LineGap, 0, 0);
            first = false;
            foreach (var label in line)
                row.Children.Add(RenderLabel(label, now));
            rows.Children.Add(row);
        }

        var root = new Grid
        {
            Width = widget.Width,
            Height = widget.Height,
            Opacity = widget.Visible ? 1 : 0.35,
            Background = Interactive ? Brushes.Transparent : null,
        };
        // The firmware does not clip text to the group box, so let rows overflow it.
        rows.Width = widget.Width;
        var overflow = new Canvas { ClipToBounds = false };
        overflow.Children.Add(rows);
        root.Children.Add(overflow);

        if (Interactive)
        {
            var selected = widget == _selected;
            root.Children.Add(new Border
            {
                BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0, 170, 255)) : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(selected ? 3 : 1.5),
                IsHitTestVisible = false,
            });
            if (selected)
            {
                var handle = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromRgb(0, 170, 255)),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, -11, -11), Cursor = Cursors.SizeNWSE,
                };
                handle.MouseLeftButtonDown += (_, e) => BeginDrag(widget, e, resize: true);
                root.Children.Add(handle);
            }
            root.Cursor = Cursors.SizeAll;
            root.MouseLeftButtonDown += (_, e) =>
            {
                if (e.Handled) return;
                Select(widget);
                BeginDrag(widget, e, resize: false);
            };
        }
        return root;
    }

    private FrameworkElement RenderLabel(OverlayLabelModel label, DateTime now)
    {
        var (family, weight) = FontFor(label.Font);
        var text = new TextBlock
        {
            Text = OverlayCompiler.FormatLabel(label, Sensors, now, _design?.Fahrenheit ?? false),
            FontFamily = family,
            FontWeight = weight,
            FontSize = label.Size,
            Foreground = new SolidColorBrush(ToColor(label.Color)),
            VerticalAlignment = VerticalAlignment.Bottom,
            TextWrapping = TextWrapping.NoWrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(1, label.Size * 1.0),
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        Brush? bg = label.Background switch
        {
            LabelBackground.Color => new SolidColorBrush(ToColor(label.BackgroundColor)),
            LabelBackground.GradientHorizontal => new LinearGradientBrush(ToColor(label.BackgroundColor), ToColor(label.GradientColor), 0),
            LabelBackground.GradientVertical => new LinearGradientBrush(ToColor(label.BackgroundColor), ToColor(label.GradientColor), 90),
            _ => null,
        };
        return new Border
        {
            Child = text,
            Background = bg,
            CornerRadius = new CornerRadius(bg != null ? Math.Min(8, label.Size / 4.0) : 0),
            Margin = new Thickness(label.GapLeft, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    public static (FontFamily family, FontWeight weight) FontFor(string fontId)
    {
        var app = Application.Current;
        return fontId switch
        {
            "roboto-thin" => ((FontFamily)app.Resources["RobotoFont"], FontWeights.Thin),
            "Orbitron-Regular" => ((FontFamily)app.Resources["OrbitronFont"], FontWeights.Normal),
            "BitcountSingle-Regular" => ((FontFamily)app.Resources["BitcountFont"], FontWeights.Normal),
            "RubikGlitch-Regular" => ((FontFamily)app.Resources["RubikGlitchFont"], FontWeights.Normal),
            _ => ((FontFamily)app.Resources["RobotoFont"], FontWeights.Normal),
        };
    }

    public static Color ToColor(string? hex)
    {
        var v = OverlayCompiler.ParseColor(hex);
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private void BuildGrid()
    {
        _gridLayer.Children.Clear();
        var pen = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        for (var x = 40; x < ScreenWidth; x += 40)
            _gridLayer.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = ScreenHeight, Stroke = pen, StrokeThickness = x % 160 == 0 ? 1.5 : 0.7 });
        for (var y = 40; y < ScreenHeight; y += 40)
            _gridLayer.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = ScreenWidth, Y1 = y, Y2 = y, Stroke = pen, StrokeThickness = y % 160 == 0 ? 1.5 : 0.7 });
        // Centre guides: the middle of the screen, or of each screen on dual-screen coolers.
        var guide = new SolidColorBrush(Color.FromArgb(90, 0, 170, 255));
        var centres = DualScreen ? new[] { ScreenWidth / 4, ScreenWidth * 3 / 4 } : new[] { ScreenWidth / 2 };
        foreach (var cx in centres)
            _gridLayer.Children.Add(new System.Windows.Shapes.Line { X1 = cx, X2 = cx, Y1 = 0, Y2 = ScreenHeight, Stroke = guide, StrokeThickness = 1.5 });
        _gridLayer.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = ScreenWidth, Y1 = ScreenHeight / 2, Y2 = ScreenHeight / 2, Stroke = guide, StrokeThickness = 1.5 });
    }

    // ------------------------------------------------------------ dragging

    private void BeginDrag(OverlayWidgetModel widget, MouseButtonEventArgs e, bool resize)
    {
        _dragging = widget;
        _resizing = resize;
        _dragStart = e.GetPosition(_canvas);
        _dragOrigin = (widget.X, widget.Y, widget.Width, widget.Height);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        int Snap(double v) => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || SnapSize <= 1
            ? (int)Math.Round(v)
            : (int)(Math.Round(v / SnapSize) * SnapSize);
        if (_resizing)
        {
            _dragging.Width = Math.Clamp(Snap(_dragOrigin.w + dx), 20, (int)ScreenWidth);
            _dragging.Height = Math.Clamp(Snap(_dragOrigin.h + dy), 20, (int)ScreenHeight);
        }
        else
        {
            _dragging.X = Math.Clamp(Snap(_dragOrigin.x + dx), 0, (int)ScreenWidth - 10);
            _dragging.Y = Math.Clamp(Snap(_dragOrigin.y + dy), 0, (int)ScreenHeight - 10);
        }
    }

    private void EndDrag()
    {
        if (_dragging == null) return;
        _dragging = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        DesignEdited?.Invoke();
    }
}
