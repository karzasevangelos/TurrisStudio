using System.Globalization;
using Panorama.Wire.V1;
using TurrisStudio.Core.Sensors;

namespace TurrisStudio.Core.Overlay;

/// <summary>Turns a design into firmware OverlayLayout / MetricBatch messages.</summary>
public static class OverlayCompiler
{
    public static uint GroupId(int widgetIndex) => (uint)(100 + widgetIndex);
    public static uint LabelId(int widgetIndex, int labelIndex) => (uint)(1000 + widgetIndex * 50 + labelIndex);

    public static OverlayLayout BuildLayout(OverlayDesign design, SensorHub? sensors, DateTime now)
    {
        var layout = new OverlayLayout();
        for (var wi = 0; wi < design.Widgets.Count; wi++)
        {
            var w = design.Widgets[wi];
            if (!w.Visible || w.Labels.Count == 0) continue;
            var group = new OverlayGroup
            {
                GroupId = GroupId(wi),
                GroupX = (uint)w.X,
                GroupY = (uint)w.Y,
                GroupWidth = (uint)w.Width,
                GroupHeight = (uint)w.Height,
                TextAlign = w.Align switch
                {
                    WidgetAlign.Center => OverlayGroup.Types.TextAlignment.AlignCenter,
                    WidgetAlign.Right => OverlayGroup.Types.TextAlignment.AlignRight,
                    _ => OverlayGroup.Types.TextAlignment.AlignLeft,
                },
                LineGap = w.LineGap,
            };
            var orderPerLine = new Dictionary<int, uint>();
            for (var li = 0; li < w.Labels.Count; li++)
            {
                var l = w.Labels[li];
                orderPerLine.TryGetValue(l.Line, out var order);
                orderPerLine[l.Line] = order + 1;
                group.Labels.Add(new OverlayLabel
                {
                    LabelId = LabelId(wi, li),
                    Line = (uint)l.Line,
                    Order = order,
                    GapLeft = l.GapLeft,
                    Background = l.Background switch
                    {
                        LabelBackground.Color => OverlayLabel.Types.Background.Color,
                        LabelBackground.GradientVertical => OverlayLabel.Types.Background.GradientVertical,
                        LabelBackground.GradientHorizontal => OverlayLabel.Types.Background.GradientHorizontal,
                        _ => OverlayLabel.Types.Background.None,
                    },
                    BackgroundColor = ParseColor(l.BackgroundColor),
                    GradientColor = ParseColor(l.GradientColor),
                    TextFont = l.Font,
                    TextSize = (uint)l.Size,
                    TextColor = ParseColor(l.Color),
                    Text = FormatLabel(l, sensors, now, design.Fahrenheit),
                });
            }
            layout.LabelGroups.Add(group);
        }
        return layout;
    }

    public static MetricBatch BuildMetrics(OverlayDesign design, SensorHub? sensors, DateTime now)
    {
        var batch = new MetricBatch();
        for (var wi = 0; wi < design.Widgets.Count; wi++)
        {
            var w = design.Widgets[wi];
            if (!w.Visible) continue;
            OverlayGroupUpdate? update = null;
            for (var li = 0; li < w.Labels.Count; li++)
            {
                var l = w.Labels[li];
                if (!l.IsDynamic) continue;
                update ??= new OverlayGroupUpdate { GroupId = GroupId(wi) };
                update.LabelTexts.Add(new OverlayLabelUpdate { LabelId = LabelId(wi, li), Text = FormatLabel(l, sensors, now, design.Fahrenheit) });
            }
            if (update != null) batch.LabelGroups.Add(update);
        }
        return batch;
    }

    public static string FormatLabel(OverlayLabelModel label, SensorHub? sensors, DateTime now, bool fahrenheit)
    {
        switch (label.Source)
        {
            case LabelSource.Time:
            case LabelSource.Date:
                try
                {
                    var fmt = string.IsNullOrWhiteSpace(label.TimeFormat) ? (label.Source == LabelSource.Time ? "HH:mm" : "dd/MM/yyyy") : label.TimeFormat;
                    return label.Prefix + now.ToString(fmt, CultureInfo.CurrentCulture) + label.Suffix;
                }
                catch (FormatException)
                {
                    return "format?";
                }
            case LabelSource.Unit:
                var unitReading = sensors?.Get(label.SensorKey);
                if (unitReading == null) return label.Text;
                return label.Prefix + (fahrenheit && unitReading.Unit is "°C" or "C" ? "°F" : unitReading.Unit) + label.Suffix;
            case LabelSource.Sensor:
                var reading = sensors?.Get(label.SensorKey);
                if (reading == null) return label.Prefix + "--" + label.Suffix;
                var value = reading.Value * label.Multiplier;
                var unit = reading.Unit;
                if (fahrenheit && unit is "°C" or "C")
                {
                    value = value * 9 / 5 + 32;
                    unit = "°F";
                }
                var text = value.ToString("F" + label.Decimals, CultureInfo.InvariantCulture);
                return label.Prefix + text + (label.AppendUnit ? unit : "") + label.Suffix;
            default:
                return label.Text;
        }
    }

    /// <summary>"#RRGGBB" → 0x00RRGGBB as the firmware expects.</summary>
    public static uint ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // drop alpha from #AARRGGBB
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v & 0xFFFFFF : 0;
    }
}

/// <summary>Ready-made widgets the designer can add.</summary>
public static class WidgetPresets
{
    public static OverlayWidgetModel Metric(string title, string? sensorKey, string sensorDisplay, string unit, int x = 40, int y = 60)
    {
        var w = new OverlayWidgetModel { Name = title, X = x, Y = y, Width = 420, Height = 160, LineGap = -10 };
        w.Labels.Add(new OverlayLabelModel { Name = "Value", Source = LabelSource.Sensor, SensorKey = sensorKey, SensorDisplay = sensorDisplay, Line = 0, Size = 120, Font = "roboto-regular" });
        w.Labels.Add(new OverlayLabelModel { Name = "Unit", Source = LabelSource.Unit, SensorKey = sensorKey, Text = unit, Line = 0, Size = 25 });
        w.Labels.Add(new OverlayLabelModel { Name = "Title", Text = title, Line = 1, Size = 22, GapLeft = 13, Font = "roboto-thin" });
        return w;
    }

    public static OverlayWidgetModel Clock(int x = 40, int y = 40)
    {
        var w = new OverlayWidgetModel { Name = "Clock", X = x, Y = y, Width = 500, Height = 170 };
        w.Labels.Add(new OverlayLabelModel { Name = "Time", Source = LabelSource.Time, TimeFormat = "HH:mm", Line = 0, Size = 120 });
        w.Labels.Add(new OverlayLabelModel { Name = "Date", Source = LabelSource.Date, TimeFormat = "dddd, d MMMM", Line = 1, Size = 26, GapLeft = 8 });
        return w;
    }

    public static OverlayWidgetModel Text(string text = "Hello", int x = 40, int y = 600) =>
        new()
        {
            Name = "Text", X = x, Y = y, Width = 600, Height = 80,
            Labels = { new OverlayLabelModel { Name = "Text", Text = text, Size = 48 } },
        };

    public static OverlayWidgetModel Badge(string text = "RYZEN", int x = 40, int y = 30)
    {
        var w = new OverlayWidgetModel { Name = "Badge", X = x, Y = y, Width = 400, Height = 50, LineGap = 1 };
        w.Labels.Add(new OverlayLabelModel
        {
            Name = "Badge", Text = $"  {text}  ", Size = 25, Color = "#DCDCDC",
            Background = LabelBackground.GradientHorizontal, BackgroundColor = "#A92F2C", GradientColor = "#CB6236",
        });
        return w;
    }

    public static OverlayWidgetModel Compact(string title, string? sensorKey, string sensorDisplay, int x = 40, int y = 60)
    {
        var w = new OverlayWidgetModel { Name = title, X = x, Y = y, Width = 500, Height = 70 };
        w.Labels.Add(new OverlayLabelModel { Name = "Title", Text = title, Size = 40, Font = "roboto-thin" });
        w.Labels.Add(new OverlayLabelModel { Name = "Value", Source = LabelSource.Sensor, SensorKey = sensorKey, SensorDisplay = sensorDisplay, AppendUnit = true, Size = 40, GapLeft = 16 });
        return w;
    }

    /// <summary>A sensible first layout similar to the official one.</summary>
    public static OverlayDesign Default(int width = 1280, int height = 720)
    {
        if (width == 1440 && height == 720)
        {
            // Stage: one column per 720x720 screen, like KANALI's split layout.
            var s = new OverlayDesign { Name = "Default", CanvasWidth = 1440, CanvasHeight = 720 };
            s.Widgets.Add(Metric("CPU TEMP", "auto:cpu_temp", "CPU Temperature (auto)", "°C", 30, 170));
            s.Widgets.Add(Metric("CPU USAGE", "auto:cpu_usage", "CPU Usage (auto)", "%", 30, 400));
            s.Widgets.Add(Metric("GPU TEMP", "auto:gpu_temp", "GPU Temperature (auto)", "°C", 750, 170));
            s.Widgets.Add(Metric("GPU USAGE", "auto:gpu_usage", "GPU Usage (auto)", "%", 750, 400));
            foreach (var w in s.Widgets) w.Width = Math.Min(w.Width, 660);
            return s;
        }
        var d = new OverlayDesign { Name = "Default", CanvasWidth = width, CanvasHeight = height };
        d.Widgets.Add(Metric("CPU TEMP", "auto:cpu_temp", "CPU Temperature (auto)", "°C", 30, 120));
        d.Widgets.Add(Metric("GPU TEMP", "auto:gpu_temp", "GPU Temperature (auto)", "°C", 30, 300));
        d.Widgets.Add(Metric("CPU USAGE", "auto:cpu_usage", "CPU Usage (auto)", "%", 30, 480));
        return d;
    }
}
