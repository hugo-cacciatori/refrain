using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Refrain.Core.Index;

namespace Refrain.Views;

/// <summary>A result's double click, from its two presses: the same row pressed twice within Windows' double-click time
/// and a few pixels. In the installed build, WinUI's own DoubleTapped came for few of Hugo's double clicks (measured,
/// 2026-10-31: six quick presses, one DoubleTapped), while every press reached the list.</summary>
public static partial class DoubleClicks
{
    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    public static void Attach(ListViewBase list, Action<FileRow> activated)
    {
        long? lastRow = null;
        long lastAt = 0;
        Windows.Foundation.Point lastPoint = default;
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(list);
            if (!point.Properties.IsLeftButtonPressed || FileTable.RowAt(list, e.OriginalSource) is not { } row)
            {
                lastRow = null;
                return;
            }
            var now = Environment.TickCount64;
            var close = Math.Abs(point.Position.X - lastPoint.X) <= 6 && Math.Abs(point.Position.Y - lastPoint.Y) <= 6;
            if (lastRow == row.Id && now - lastAt <= GetDoubleClickTime() && close)
            {
                lastRow = null;
                activated(row);
                return;
            }
            (lastRow, lastAt, lastPoint) = (row.Id, now, point.Position);
        }), handledEventsToo: true);
    }
}
