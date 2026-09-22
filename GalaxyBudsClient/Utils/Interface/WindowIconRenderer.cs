using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GalaxyBudsClient.Generated.I18N;
using GalaxyBudsClient.Message;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Model.Specifications;
using GalaxyBudsClient.Platform;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Brushes = Avalonia.Media.Brushes;
using Point = Avalonia.Point;

namespace GalaxyBudsClient.Utils.Interface;

public static class WindowIconRenderer
{
    private const string DefaultToolTip = "Galaxy Buds";
    private const double CanvasSize = 256;
    private const double Padding = 12;

    private static readonly WindowIcon DefaultIcon = MakeDefaultIcon();

    public static void UpdateDynamicIcon(IBasicStatusUpdate status)
    {
        var trayIcons = TrayIcon.GetIcons(Application.Current!);
        if (trayIcons == null)
            return;

        var rawLeft = status.BatteryL;
        var rawRight = status.BatteryR;
        var batteryLeft = rawLeft;
        var batteryRight = rawRight;

        // Ignore battery level of disconnected earbuds
        if (batteryLeft <= 0)
            batteryLeft = batteryRight;
        if (batteryRight <= 0)
            batteryRight = batteryLeft;

        var chargingL = status.PlacementL == PlacementStates.Charging;
        var chargingR = status.PlacementR == PlacementStates.Charging;
        var anyCharging = (chargingL && rawLeft > 0) || (chargingR && rawRight > 0);

        var mode = Settings.Data.DynamicTrayIconMode;
        var toolTip = BuildToolTip(status);

        // Render on the UI thread, as RenderTargetBitmap should not be used from background threads
        Dispatcher.UIThread.Post(() =>
        {
            WindowIcon? icon = mode switch
            {
                DynamicTrayIconModes.BatteryMin => MakeFromBatteryLevel(Math.Min(Math.Min(batteryLeft, batteryRight), 99), anyCharging),
                DynamicTrayIconModes.BatteryAvg => MakeFromBatteryLevel(Math.Min((batteryLeft + batteryRight) / 2, 99), anyCharging),
                DynamicTrayIconModes.BatteryLeftRight => MakeFromLeftRight(rawLeft, rawRight, chargingL, chargingR),
                _ => null
            };

            if (icon != null)
                trayIcons[0].Icon = icon;
            trayIcons[0].ToolTipText = toolTip;
        });
    }

    public static void ResetIconToDefault()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var trayIcons = TrayIcon.GetIcons(Application.Current!);
            if (trayIcons == null)
                return;

            trayIcons[0].Icon = DefaultIcon;
            trayIcons[0].ToolTipText = DefaultToolTip;
        });
    }

    /// <summary>
    /// Tooltip shown when hovering the tray icon,
    /// e.g. "Left: 90% (Charging) · Right: 85% (Charging) · Case: 70%"
    /// </summary>
    private static string BuildToolTip(IBasicStatusUpdate status)
    {
        string Suffix(bool charging) => charging ? $" ({Strings.PlacementCharging})" : string.Empty;

        var parts = new List<string>();
        if (status.BatteryL > 0)
            parts.Add($"{Strings.Left}: {status.BatteryL}%{Suffix(status.PlacementL == PlacementStates.Charging)}");
        if (status.BatteryR > 0)
            parts.Add($"{Strings.Right}: {status.BatteryR}%{Suffix(status.PlacementR == PlacementStates.Charging)}");

        var batteryCase = status.BatteryCase;
        if (batteryCase > 100)
            batteryCase = DeviceMessageCache.Instance.BasicStatusUpdateWithValidCase?.BatteryCase ?? batteryCase;
        if (batteryCase is > 0 and <= 100 && BluetoothImpl.Instance.DeviceSpec.Supports(Features.CaseBattery))
        {
            var caseCharging = status switch
            {
                ExtendedStatusUpdateDecoder e => e.IsCaseCharging,
                StatusUpdateDecoder u => u.IsCaseCharging,
                _ => false
            };
            parts.Add($"{Strings.Case}: {batteryCase}%{Suffix(caseCharging)}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : DefaultToolTip;
    }

    /// <summary>
    /// Lightning bolt shape used as charging indicator (drawn as a path, so it does not depend on font glyphs)
    /// </summary>
    private static Geometry MakeBoltGeometry() =>
        Geometry.Parse("M62,0 L8,112 L46,112 L34,200 L92,84 L54,84 L70,0 Z");

    /// <summary>
    /// Lays out an optional charging bolt followed by the text in one row.
    /// Returns the parts with their row-local transforms and the bounds of the whole row.
    /// </summary>
    private static (List<(Geometry geometry, Matrix transform)> parts, Rect bounds)? BuildRow(string text, bool withBolt)
    {
        var textGeometry = BuildTextGeometry(text);
        if (textGeometry == null)
            return null;

        var tb = textGeometry.Bounds;
        var parts = new List<(Geometry, Matrix)>();
        double x = 0;

        if (withBolt)
        {
            var bolt = MakeBoltGeometry();
            var bb = bolt.Bounds;
            // Scale the bolt to the height of the text and align it to the text's top edge
            var boltScale = tb.Height / bb.Height;
            parts.Add((bolt,
                Matrix.CreateTranslation(-bb.X, -bb.Y) *
                Matrix.CreateScale(boltScale, boltScale) *
                Matrix.CreateTranslation(0, tb.Y)));
            x = bb.Width * boltScale + tb.Height * 0.08;
        }

        parts.Add((textGeometry, Matrix.CreateTranslation(-tb.X + x, 0)));
        return (parts, new Rect(0, tb.Y, x + tb.Width, tb.Height));
    }

    private static Geometry? BuildTextGeometry(string text)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold),
            210,
            Brushes.Black // This brush does not matter since we use the geometry of the text.
        );
        return formattedText.BuildGeometry(new Point(0, 0));
    }

    private static IBrush IconBrush => PlatformUtils.IsOSX
        // OSX uses templated (black) icons
        ? (IBrush)Brushes.Black
        : new SolidColorBrush(Settings.Data.AccentColor);

    private static WindowIcon RenderGeometries(IEnumerable<(Geometry geometry, Matrix transform)> items)
    {
        var render = new RenderTargetBitmap(new PixelSize((int)CanvasSize, (int)CanvasSize), new Vector(96, 96));

        using (var ctx = render.CreateDrawingContext())
        {
            ctx.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
                TextRenderingMode = TextRenderingMode.Antialias,
                EdgeMode = EdgeMode.Antialias,
                RequiresFullOpacityHandling = true
            });

            var brush = IconBrush;
            foreach (var (geometry, transform) in items)
            {
                using (ctx.PushTransform(transform))
                {
                    ctx.DrawGeometry(brush, new Pen(Brushes.Transparent, 0), geometry);
                }
            }
        }

        return new WindowIcon(render);
    }

    private static WindowIcon MakeFromBatteryLevel(int level, bool charging = false)
    {
        var row = BuildRow($"{level}", charging);
        if (row == null)
            return DefaultIcon;

        // Fit the row into the canvas based on its real bounds instead of a fixed offset.
        // Font metrics differ between platforms (e.g. SF on macOS), which previously caused
        // two-digit values to be clipped in the macOS menu bar.
        var (parts, bounds) = row.Value;
        var scale = Math.Min((CanvasSize - Padding * 2) / bounds.Width, (CanvasSize - Padding * 2) / bounds.Height);
        var fit =
            Matrix.CreateTranslation(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2)) *
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(CanvasSize / 2, CanvasSize / 2);

        return RenderGeometries(parts.Select(p => (p.geometry, p.transform * fit)));
    }

    /// <summary>
    /// Renders the left battery level on the top row and the right battery level on the bottom row.
    /// A disconnected earbud is shown as "-". While an earbud is charging, its side letter is
    /// replaced by a lightning bolt (the top row is always the left earbud).
    /// </summary>
    private static WindowIcon MakeFromLeftRight(int left, int right, bool chargingLeft, bool chargingRight)
    {
        var showBoltL = chargingLeft && left > 0;
        var showBoltR = chargingRight && right > 0;
        var leftValue = left > 0 ? Math.Min(left, 99).ToString() : "-";
        var rightValue = right > 0 ? Math.Min(right, 99).ToString() : "-";

        var top = BuildRow(showBoltL ? leftValue : $"L{leftValue}", showBoltL);
        var bottom = BuildRow(showBoltR ? rightValue : $"R{rightValue}", showBoltR);
        // Reference row so both rows share the same scale regardless of the digits shown
        var reference = BuildRow("88", true);
        if (top == null || bottom == null || reference == null)
            return DefaultIcon;

        const double rowGap = 16;
        var rowHeight = (CanvasSize - Padding * 2 - rowGap) / 2;
        var refBounds = reference.Value.bounds;
        var maxWidth = Math.Max(refBounds.Width, Math.Max(top.Value.bounds.Width, bottom.Value.bounds.Width));
        var scale = Math.Min((CanvasSize - Padding * 2) / maxWidth, rowHeight / refBounds.Height);

        IEnumerable<(Geometry, Matrix)> PlaceRow((List<(Geometry geometry, Matrix transform)> parts, Rect bounds) row, double rowCenterY)
        {
            var b = row.bounds;
            // Center horizontally; align vertically on the reference height so rows line up
            var fit = Matrix.CreateTranslation(-(b.X + b.Width / 2), -(refBounds.Y + refBounds.Height / 2)) *
                      Matrix.CreateScale(scale, scale) *
                      Matrix.CreateTranslation(CanvasSize / 2, rowCenterY);
            return row.parts.Select(p => (p.geometry, p.transform * fit));
        }

        var topCenter = Padding + rowHeight / 2;
        var bottomCenter = CanvasSize - Padding - rowHeight / 2;

        return RenderGeometries(PlaceRow(top.Value, topCenter).Concat(PlaceRow(bottom.Value, bottomCenter)));
    }

    private static WindowIcon MakeDefaultIcon()
    {
        return new WindowIcon(MakeDefaultBitmap());
    }

    private static Bitmap MakeDefaultBitmap()
    {
        // OSX uses templated icons
        var type = PlatformUtils.IsOSX ? "black" : PlatformUtils.IsWindows ? "white_outlined_single" : "white_outlined_multi";
        var uri = $"{Program.AvaresUrl}/Resources/icon_{type}_tray.ico";
        return new Bitmap(AssetLoader.Open(new Uri(uri)));
    }
}
