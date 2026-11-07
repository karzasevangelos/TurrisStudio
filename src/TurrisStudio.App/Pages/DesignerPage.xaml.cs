using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TurrisStudio.Core.Config;
using TurrisStudio.Core.Overlay;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace TurrisStudio.Pages;

/// <summary>
/// Simple overlay editor: each widget is a Sensor, Clock or Text/Badge, edited with a few plain controls.
/// Everything is saved and sent to the screen automatically.
/// </summary>
public partial class DesignerPage : UserControl, IDisposable
{
    private enum Kind { Sensor, Clock, Text }

    private static readonly (string Format, string Label)[] TimeFormats =
    [
        ("HH:mm", "24-hour"),
        ("HH:mm:ss", "24-hour with seconds"),
        ("h:mm tt", "12-hour with AM/PM"),
        ("h:mm", "12-hour"),
    ];

    private static readonly string[] DateFormats =
        ["dddd, d MMMM", "d MMMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "ddd d MMM", "dddd", "MMMM yyyy"];

    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private OverlayDesign _design;
    private bool _updating;

    private AppSettings Settings => AppServices.Settings;
    private OverlayWidgetModel? Selected => WidgetList.SelectedItem as OverlayWidgetModel;

    public DesignerPage()
    {
        InitializeComponent();
        _design = Settings.Design;

        Canvas.Interactive = true;
        Canvas.Sensors = AppServices.Device.Sensors;
        Canvas.SelectionChanged += w => { if (!_updating) SelectWidget(w); };
        Canvas.ModelChanged += () => { _syncTimer.Stop(); _syncTimer.Start(); };
        Canvas.SnapSize = 10;

        SensorPicker.Hub = AppServices.Device.Sensors;
        SensorPicker.Picked += OnSensorPicked;

        foreach (var (id, display) in DeviceFonts.All)
        {
            var (family, weight) = Controls.ScreenCanvas.FontFor(id);
            FontBox.Items.Add(new ComboBoxItem { Content = display, Tag = id, FontFamily = family, FontWeight = weight, FontSize = 15 });
        }
        foreach (var (format, label) in TimeFormats)
            TimeFormatBox.Items.Add(new ComboBoxItem { Content = $"{label}   ({DateTime.Now.ToString(format)})", Tag = format });
        foreach (var format in DateFormats)
            DateFormatBox.Items.Add(new ComboBoxItem { Content = DateTime.Now.ToString(format), Tag = format });

        OnColorChanged(ColorBox, c => ForEachMain(l => l.Color = c));
        OnColorChanged(AccentColorBox, c => { if (AccentPart() is { } a) a.Color = c; });
        OnColorChanged(BgColorBox, c => { if (TextPart() is { } t) t.BackgroundColor = c; });
        OnColorChanged(BgColor2Box, c => { if (TextPart() is { } t) t.GradientColor = c; });

        _syncTimer.Tick += (_, _) =>
        {
            _syncTimer.Stop();
            AppServices.Device.InvalidateLayout();
            AppServices.SaveSoon();
            WidgetList.Items.Refresh();
            UpdateSyncStatus();
        };
        _liveTimer.Tick += (_, _) => { Canvas.RefreshValues(); UpdateSyncStatus(); };
        AppServices.Device.StateChanged += OnDeviceState;
        AppServices.Device.ModelChanged += OnModelChanged;

        Loaded += (_, _) =>
        {
            LoadPreviewChoices();
            _liveTimer.Start();
            UpdateSyncStatus();
        };
        Unloaded += (_, _) => _liveTimer.Stop();
        PreviewKeyDown += OnKey;

        LoadDesigns();
    }

    private void OnColorChanged(Controls.ColorBox box, Action<string> apply) =>
        DependencyPropertyDescriptor.FromProperty(Controls.ColorBox.ValueProperty, typeof(Controls.ColorBox))
            .AddValueChanged(box, (_, _) => { if (!_updating && Selected != null) apply(box.Value); });

    private void OnDeviceState() => Dispatcher.BeginInvoke(UpdateSyncStatus);

    /// <summary>A cooler with another screen size was connected: refit the layout and preview.</summary>
    private void OnModelChanged() => Dispatcher.BeginInvoke(() =>
    {
        ShowDesign(_design);
        LoadPreviewChoices();
    });

    private void LoadPreviewChoices()
    {
        var current = UiHelpers.CurrentItem();
        var keep = PreviewBox.SelectedItem as MediaLibraryItem;
        PreviewBox.ItemsSource = Settings.Library.OrderByDescending(i => i.Added).ToList();
        // In split mode the preview shows exactly what both screens play.
        var split = UiHelpers.SplitActive;
        PreviewBox.SelectedItem = split ? null : keep != null && Settings.Library.Contains(keep) ? keep : current ?? Settings.Library.FirstOrDefault();
        if (PreviewBox.SelectedItem == null) _ = UiHelpers.ShowInPreviewAsync(Canvas, null);
        PreviewBox.Visibility = PreviewLabel.Visibility = Settings.Library.Count > 0 && !split ? Visibility.Visible : Visibility.Collapsed;
        NowOnCoolerText.Text = "Now on your cooler:  " + UiHelpers.CurrentMediaName();
    }

    private async void PreviewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewBox.SelectedItem is MediaLibraryItem item) await UiHelpers.ShowInPreviewAsync(Canvas, item);
    }

    private void UpdateSyncStatus()
    {
        var connected = AppServices.Device.IsConnected;
        SyncDot.Fill = connected ? Brushes.LimeGreen : Brushes.Orange;
        SyncText.Text = connected ? "Live on screen" : "Saved";
        SyncText.ToolTip = connected ? "Every change is sent to the cooler instantly" : "Saved. It will be sent when the cooler connects.";
    }

    // ============================================================ layouts

    private void LoadDesigns()
    {
        _updating = true;
        DesignBox.ItemsSource = null;
        DesignBox.ItemsSource = Settings.Designs;
        DesignBox.SelectedItem = _design;
        _updating = false;
        ShowDesign(_design);
    }

    private void ShowDesign(OverlayDesign design)
    {
        _design = design;
        Settings.ActiveDesign = design.Name;
        var model = UiHelpers.Model;
        design.FitTo(model.Width, model.Height);
        UiHelpers.ApplyScreen(Canvas);
        Canvas.Design = design;
        WidgetList.ItemsSource = design.Widgets;
        FahrenheitToggle.IsChecked = design.Fahrenheit;
        SelectWidget(null);
        AppServices.Device.InvalidateLayout();
        AppServices.SaveSoon();
    }

    private void DesignBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && DesignBox.SelectedItem is OverlayDesign d) ShowDesign(d);
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; Settings.Designs.Any(d => d.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }

    private void NewDesign_Click(object sender, RoutedEventArgs e)
    {
        _design = new OverlayDesign { Name = UniqueName("New layout"), CanvasWidth = UiHelpers.Model.Width, CanvasHeight = UiHelpers.Model.Height };
        Settings.Designs.Add(_design);
        LoadDesigns();
    }

    private void DuplicateDesign_Click(object sender, RoutedEventArgs e)
    {
        var copy = SettingsStore.ImportDesign(SettingsStore.ExportDesign(_design))!;
        copy.Name = UniqueName(_design.Name + " copy");
        Settings.Designs.Add(copy);
        _design = copy;
        LoadDesigns();
    }

    private async void RenameDesign_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _design.Name, MinWidth = 300 };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        var dialog = new Wpf.Ui.Controls.MessageBox { Title = "Rename layout", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel" };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0 || name == _design.Name) return;
        _design.Name = UniqueName(name);
        LoadDesigns();
    }

    private async void DeleteDesign_Click(object sender, RoutedEventArgs e)
    {
        if (Settings.Designs.Count <= 1)
        {
            MainWindow.Notify("Can't delete", "You need at least one layout.", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new Wpf.Ui.Controls.MessageBox
            { Title = "Delete layout?", Content = $"“{_design.Name}” will be deleted.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel" };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        Settings.Designs.Remove(_design);
        _design = Settings.Designs[0];
        LoadDesigns();
    }

    private void ImportDesign_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Turris layout|*.turrislayout;*.json|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var d = SettingsStore.ImportDesign(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException();
            SettingsStore.Migrate(d);
            d.Name = UniqueName(d.Name);
            Settings.Designs.Add(d);
            _design = d;
            LoadDesigns();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            MainWindow.Notify("Import failed", "That file isn't a valid layout.", InfoBarSeverity.Error);
        }
    }

    private void ExportDesign_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Turris layout|*.turrislayout", FileName = _design.Name + ".turrislayout" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SettingsStore.ExportDesign(_design));
        MainWindow.Notify("Exported", Path.GetFileName(dialog.FileName), InfoBarSeverity.Success);
    }

    // ============================================================ widgets

    private (int x, int y) NextSpot() => (40 + _design.Widgets.Count * 30 % 600, 40 + _design.Widgets.Count * 40 % 520);

    private void AddWidget(OverlayWidgetModel widget)
    {
        _design.Widgets.Add(widget);
        SelectWidget(widget);
    }

    private void AddMetric_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpot();
        AddWidget(WidgetPresets.Metric("CPU TEMP", "auto:cpu_temp", "CPU Temperature (auto)", "°C", x, y));
    }

    private void AddCompact_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpot();
        AddWidget(WidgetPresets.Compact("GPU TEMP", "auto:gpu_temp", "GPU Temperature (auto)", x, y));
    }

    private void AddClock_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpot();
        AddWidget(WidgetPresets.Clock(x, y));
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpot();
        AddWidget(WidgetPresets.Text("Hello", x, y));
    }

    private void AddBadge_Click(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpot();
        AddWidget(WidgetPresets.Badge("RYZEN", x, y));
    }

    private void WidgetDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w) return;
        var copy = w.Clone();
        copy.X = Math.Min(1200, w.X + 30);
        copy.Y = Math.Min(680, w.Y + 30);
        AddWidget(copy);
    }

    private void WidgetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w) return;
        var i = _design.Widgets.IndexOf(w);
        _design.Widgets.Remove(w);
        SelectWidget(_design.Widgets.Count > 0 ? _design.Widgets[Math.Min(i, _design.Widgets.Count - 1)] : null);
    }

    private void WidgetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) SelectWidget(Selected);
    }

    private void SelectWidget(OverlayWidgetModel? widget)
    {
        _updating = true;
        WidgetList.SelectedItem = widget;
        Canvas.Select(widget);
        _updating = false;
        EditPanel.DataContext = widget;
        EditPanel.Visibility = widget != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelection.Visibility = widget == null ? Visibility.Visible : Visibility.Collapsed;
        RefreshEditor();
    }

    // ============================================================ widget anatomy

    private static Kind KindOf(OverlayWidgetModel w) =>
        w.Labels.Any(l => l.Source == LabelSource.Sensor) ? Kind.Sensor
        : w.Labels.Any(l => l.Source is LabelSource.Time) ? Kind.Clock
        : Kind.Text;

    private OverlayLabelModel? ValuePart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Sensor);
    private OverlayLabelModel? UnitPart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Unit);
    private OverlayLabelModel? TitlePart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Text);
    private OverlayLabelModel? TimePart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Time);
    private OverlayLabelModel? DatePart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Date);
    private OverlayLabelModel? TextPart() => Selected?.Labels.FirstOrDefault(l => l.Source == LabelSource.Text);

    /// <summary>The big part that "Size" and "Color" refer to.</summary>
    private OverlayLabelModel? MainPart() => Selected == null ? null : KindOf(Selected) switch
    {
        Kind.Sensor => ValuePart(),
        Kind.Clock => TimePart(),
        _ => TextPart(),
    };

    /// <summary>The secondary part with its own colour (sensor title / clock date).</summary>
    private OverlayLabelModel? AccentPart() => Selected == null ? null : KindOf(Selected) switch
    {
        Kind.Sensor => TitlePart(),
        Kind.Clock => DatePart(),
        _ => null,
    };

    private void ForEachMain(Action<OverlayLabelModel> apply)
    {
        if (Selected is not { } w) return;
        var accent = AccentPart();
        foreach (var l in w.Labels.Where(l => l != accent)) apply(l);
    }

    // ============================================================ editor

    private void RefreshEditor()
    {
        if (Selected is not { } w) return;
        _updating = true;
        var kind = KindOf(w);
        SensorSection.Visibility = kind == Kind.Sensor ? Visibility.Visible : Visibility.Collapsed;
        ClockSection.Visibility = kind == Kind.Clock ? Visibility.Visible : Visibility.Collapsed;
        TextSection.Visibility = kind == Kind.Text ? Visibility.Visible : Visibility.Collapsed;
        EditTitle.Text = kind switch { Kind.Sensor => "Sensor", Kind.Clock => "Clock", _ => "Text" };

        switch (kind)
        {
            case Kind.Sensor:
                var value = ValuePart()!;
                SensorPicker.SetDisplay(value.SensorDisplay.Length > 0 ? value.SensorDisplay : value.SensorKey ?? "");
                TitleBox.Text = TitlePart()?.Text ?? w.Name;
                ShowTitleCheck.IsChecked = TitlePart() != null;
                ShowUnitCheck.IsChecked = UnitPart() != null || value.AppendUnit;
                DecimalsBox.SelectedIndex = Math.Min(2, value.Decimals);
                AccentLabel.Text = "Title color";
                break;
            case Kind.Clock:
                var time = TimePart()!;
                TimeFormatBox.SelectedItem = TimeFormatBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == time.TimeFormat)
                                             ?? TimeFormatBox.Items[0];
                var date = DatePart();
                ShowDateCheck.IsChecked = date != null;
                DateFormatBox.IsEnabled = date != null;
                DateFormatBox.SelectedItem = DateFormatBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == date?.TimeFormat)
                                             ?? DateFormatBox.Items[0];
                AccentLabel.Text = "Date color";
                break;
            default:
                var text = TextPart();
                TextInput.Text = text?.Text.Trim() ?? "";
                BackgroundBox.SelectedIndex = (int)(text?.Background ?? LabelBackground.None);
                BgColorBox.Value = text?.BackgroundColor ?? "#A92F2C";
                BgColor2Box.Value = text?.GradientColor ?? "#CB6236";
                UpdateBackgroundVisibility();
                break;
        }

        var main = MainPart();
        var accent = AccentPart();
        AccentSection.Visibility = accent != null ? Visibility.Visible : Visibility.Hidden;
        SizeSlider.Value = main?.Size ?? 40;
        SizeText.Text = $"{(int)SizeSlider.Value}px";
        ColorBox.Value = main?.Color ?? "#FFFFFF";
        AccentColorBox.Value = accent?.Color ?? "#FFFFFF";
        FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == main?.Font) ?? FontBox.Items[0];
        AlignLeft.IsChecked = w.Align == WidgetAlign.Left;
        AlignCenter.IsChecked = w.Align == WidgetAlign.Center;
        AlignRight.IsChecked = w.Align == WidgetAlign.Right;
        _updating = false;
    }

    // ---- sensor

    private void OnSensorPicked(string key, string display)
    {
        if (Selected is not { } w || ValuePart() is not { } value) return;
        value.SensorKey = key;
        value.SensorDisplay = display;
        if (UnitPart() is { } unit) unit.SensorKey = key;
        if (AppServices.Device.Sensors.Get(key) is { } reading)
        {
            var title = ShortTitle(reading.Name);
            if (TitlePart() is { } t) t.Text = title;
            w.Name = title;
            // Whole numbers for temperatures/percent, one decimal for small values like GHz or MB/s.
            value.Decimals = reading.Unit is "°C" or "%" or "W" or "MHz" or "RPM" ? 0 : Math.Abs(reading.Value) < 10 ? 1 : 0;
        }
        RefreshEditor();
        WidgetList.Items.Refresh();
    }

    private static string ShortTitle(string name) =>
        name.Replace("Temperature", "Temp").Replace("Utilization", "Usage").Replace("D3D ", "").Replace("(Tctl/Tdie)", "")
            .Replace("Total ", "").Replace("(auto)", "").Trim().ToUpperInvariant();

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || Selected is not { } w) return;
        if (TitlePart() is { } t) t.Text = TitleBox.Text;
        w.Name = TitleBox.Text.Trim().Length > 0 ? TitleBox.Text.Trim() : "Sensor";
        WidgetList.Items.Refresh();
    }

    private void ShowTitle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w || ValuePart() is not { } value) return;
        if (ShowTitleCheck.IsChecked == true && TitlePart() == null)
        {
            var oneLine = w.Height < value.Size * 1.6;
            var title = new OverlayLabelModel
            {
                Name = "Title", Text = TitleBox.Text.Length > 0 ? TitleBox.Text : w.Name, Font = value.Font,
                Line = oneLine ? value.Line : value.Line + 1, Size = oneLine ? value.Size : Math.Max(12, value.Size * 22 / 120),
                GapLeft = oneLine ? 0 : 13, Color = AccentColorBox.Value,
            };
            if (oneLine)
            {
                value.GapLeft = Math.Max(value.GapLeft, 16);
                w.Labels.Insert(w.Labels.IndexOf(value), title);
            }
            else w.Labels.Add(title);
        }
        else if (ShowTitleCheck.IsChecked != true && TitlePart() is { } t)
        {
            w.Labels.Remove(t);
        }
        RefreshEditor();
    }

    private void ShowUnit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w || ValuePart() is not { } value) return;
        var on = ShowUnitCheck.IsChecked == true;
        if (on && UnitPart() == null && !value.AppendUnit)
        {
            var big = value.Size >= 80;
            if (big)
                w.Labels.Insert(w.Labels.IndexOf(value) + 1, new OverlayLabelModel
                {
                    Name = "Unit", Source = LabelSource.Unit, SensorKey = value.SensorKey, Line = value.Line,
                    Size = Math.Max(12, value.Size * 25 / 120), Color = value.Color, Font = value.Font,
                });
            else value.AppendUnit = true;
        }
        else if (!on)
        {
            value.AppendUnit = false;
            if (UnitPart() is { } u) w.Labels.Remove(u);
        }
    }

    private void Decimals_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ValuePart() is { } v && DecimalsBox.SelectedIndex >= 0) v.Decimals = DecimalsBox.SelectedIndex;
    }

    // ---- clock

    private void TimeFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && TimePart() is { } t && TimeFormatBox.SelectedItem is ComboBoxItem { Tag: string f }) t.TimeFormat = f;
    }

    private void DateFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && DatePart() is { } d && DateFormatBox.SelectedItem is ComboBoxItem { Tag: string f }) d.TimeFormat = f;
    }

    private void ShowDate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w || TimePart() is not { } time) return;
        if (ShowDateCheck.IsChecked == true && DatePart() == null)
        {
            w.Labels.Add(new OverlayLabelModel
            {
                Name = "Date", Source = LabelSource.Date, Line = time.Line + 1, GapLeft = 8, Font = time.Font,
                Size = Math.Max(12, time.Size * 26 / 120), Color = AccentColorBox.Value,
                TimeFormat = (DateFormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? DateFormats[0],
            });
        }
        else if (ShowDateCheck.IsChecked != true && DatePart() is { } d)
        {
            w.Labels.Remove(d);
        }
        RefreshEditor();
    }

    // ---- text / badge

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || Selected is not { } w || TextPart() is not { } t) return;
        // Badges get a little padding inside their coloured background.
        t.Text = t.Background != LabelBackground.None ? $"  {TextInput.Text.Trim()}  " : TextInput.Text;
        w.Name = TextInput.Text.Trim().Length > 0 ? TextInput.Text.Trim() : "Text";
        WidgetList.Items.Refresh();
    }

    private void Background_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || TextPart() is not { } t || BackgroundBox.SelectedIndex < 0) return;
        t.Background = (LabelBackground)BackgroundBox.SelectedIndex;
        var plain = TextInput.Text.Trim();
        t.Text = t.Background != LabelBackground.None ? $"  {plain}  " : plain;
        UpdateBackgroundVisibility();
    }

    private void UpdateBackgroundVisibility()
    {
        var bg = (LabelBackground)Math.Max(0, BackgroundBox.SelectedIndex);
        BackgroundColors.Visibility = bg == LabelBackground.None ? Visibility.Collapsed : Visibility.Visible;
        BgColor2Box.Visibility = bg is LabelBackground.GradientHorizontal or LabelBackground.GradientVertical ? Visibility.Visible : Visibility.Hidden;
    }

    // ---- look

    /// <summary>Scales the whole widget (all text and the box) together.</summary>
    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SizeText != null) SizeText.Text = $"{(int)e.NewValue}px";
        if (_updating || Selected is not { } w || MainPart() is not { } main || main.Size <= 0) return;
        var factor = e.NewValue / main.Size;
        if (Math.Abs(factor - 1) < 0.001) return;
        foreach (var l in w.Labels) l.Size = Math.Max(6, (int)Math.Round(l.Size * factor));
        main.Size = (int)Math.Round(e.NewValue);
        w.Width = Math.Clamp((int)Math.Round(w.Width * factor), 40, (int)Canvas.ScreenWidth);
        w.Height = Math.Clamp((int)Math.Round(w.Height * factor), 30, (int)Canvas.ScreenHeight);
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Selected is not { } w || FontBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        foreach (var l in w.Labels) l.Font = id;
    }

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w) return;
        w.Align = AlignCenter.IsChecked == true ? WidgetAlign.Center : AlignRight.IsChecked == true ? WidgetAlign.Right : WidgetAlign.Left;
    }

    /// <summary>Centres the widget on its screen (each screen separately on dual-screen coolers).</summary>
    private void CenterH_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w) return;
        var width = (int)Canvas.ScreenWidth;
        if (Canvas.DualScreen)
        {
            var half = width / 2;
            var left = w.X + w.Width / 2 < half ? 0 : half;
            w.X = Math.Max(0, left + (half - w.Width) / 2);
        }
        else
        {
            w.X = Math.Max(0, (width - w.Width) / 2);
        }
    }

    private void CenterV_Click(object sender, RoutedEventArgs e) { if (Selected is { } w) w.Y = Math.Max(0, ((int)Canvas.ScreenHeight - w.Height) / 2); }

    // ============================================================ toolbar + keys

    private void GridToggle_Click(object sender, RoutedEventArgs e) => Canvas.ShowGrid = GridToggle.IsChecked == true;

    private void Fahrenheit_Click(object sender, RoutedEventArgs e) => _design.Fahrenheit = FahrenheitToggle.IsChecked == true;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or ListBoxItem) return;
        if (Selected is not { } w) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: w.X -= step; break;
            case Key.Right: w.X += step; break;
            case Key.Up: w.Y -= step; break;
            case Key.Down: w.Y += step; break;
            case Key.Delete: WidgetDelete_Click(this, new RoutedEventArgs()); break;
            default: return;
        }
        e.Handled = true;
    }

    internal void SelectFirstWidgetForScreenshot() => SelectWidget(_design.Widgets.FirstOrDefault());

    public void Dispose()
    {
        _liveTimer.Stop();
        _syncTimer.Stop();
        AppServices.Device.StateChanged -= OnDeviceState;
        AppServices.Device.ModelChanged -= OnModelChanged;
        AppServices.Store.Save();
    }
}
