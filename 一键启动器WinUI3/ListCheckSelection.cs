using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace QuickLauncherWinUI3;

// One selection model for row clicks, checkboxes and mouse marquee selection.
internal sealed class ListCheckSelection<T> where T : class
{
    private readonly ListView _list;
    private readonly Grid _surface;
    private readonly Canvas _overlay;
    private readonly Rectangle _box;
    private readonly Func<T, bool> _get;
    private readonly Action<T, bool> _set;
    private readonly Action<T>? _open;
    private readonly Dictionary<T, bool> _initial = new();
    private Point _start;
    private bool _pressed, _dragging, _add;
    private T? _down, _lastClick;
    private DateTime _lastTime;

    public ListCheckSelection(ListView list, Grid surface, Canvas overlay, Func<T, bool> get, Action<T, bool> set, Action<T>? open = null)
    {
        _list = list; _surface = surface; _overlay = overlay; _get = get; _set = set; _open = open;
        _box = new Rectangle { StrokeThickness = 1, Visibility = Visibility.Collapsed };
        _box.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 80, 220));
        _box.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 160, 80, 220));
        overlay.Children.Add(_box);
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
        surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        surface.PointerCanceled += Canceled;
        surface.PointerCaptureLost += Canceled;
    }
    private IEnumerable<T> Items => _list.Items.Cast<object>().OfType<T>();
    private Rect? Bounds(T item)
    {
        if (_list.ContainerFromItem(item) is not FrameworkElement container) return null;
        return container.TransformToVisual(_surface).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
    }
    private T? At(Point p) => Items.FirstOrDefault(item => Bounds(item)?.Contains(p) == true);
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_surface);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Keep scrollbars available for normal scrolling.
        for (var v = e.OriginalSource as DependencyObject; v != null && v != _surface; v = VisualTreeHelper.GetParent(v))
            if (v is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return;
        _start = point.Position; _down = At(_start);
        _initial.Clear(); foreach (var item in Items) _initial[item] = _get(item);
        _add = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        _pressed = _surface.CapturePointer(e.Pointer); _dragging = false;
        e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        Point p = e.GetCurrentPoint(_surface).Position;
        if (!_dragging && Math.Abs(p.X - _start.X) < 5 && Math.Abs(p.Y - _start.Y) < 5) return;
        _dragging = true;
        p.X = Math.Clamp(p.X, 0, _surface.ActualWidth); p.Y = Math.Clamp(p.Y, 0, _surface.ActualHeight);
        var area = new Rect(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        Canvas.SetLeft(_box, area.X); Canvas.SetTop(_box, area.Y);
        _box.Width = area.Width; _box.Height = area.Height; _box.Visibility = Visibility.Visible;
        foreach (var item in Items)
        {
            Rect? bounds = Bounds(item);
            bool hit = bounds is Rect r && r.Bottom > area.Top && r.Top < area.Bottom && r.Right > area.Left && r.Left < area.Right;
            _set(item, hit || (_add && _initial.TryGetValue(item, out bool selected) && selected));
        }
        e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        if (!_dragging && _down is T item && ReferenceEquals(At(e.GetCurrentPoint(_surface).Position), item))
        {
            if (_list.SelectionMode != ListViewSelectionMode.None) _list.SelectedItem = item;
            if (ReferenceEquals(_lastClick, item) && (DateTime.UtcNow - _lastTime).TotalMilliseconds < 450 && _open != null)
            {
                // A double click opens the item without toggling twice.
                _set(item, _initial[item]); _open(item); _lastClick = null;
            }
            else { _set(item, !_get(item)); _lastClick = item; _lastTime = DateTime.UtcNow; }
        }
        else if (_dragging) _lastClick = null;
        Finish(); _surface.ReleasePointerCapture(e.Pointer); e.Handled = true;
    }
    private void Canceled(object sender, PointerRoutedEventArgs e) => Finish();
    private void Finish() { _pressed = false; _dragging = false; _box.Visibility = Visibility.Collapsed; _initial.Clear(); _down = null; }
}
