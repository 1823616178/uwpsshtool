using System;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace SshTool.App.Terminal
{
    public enum SelectionToolbarAction
    {
        Copy = 0,
        Paste = 1,
        SelectAll = 2,
        Share = 3,
        Dismiss = 4
    }

    public sealed class SelectionToolbarEventArgs : EventArgs
    {
        public SelectionToolbarEventArgs(SelectionToolbarAction action)
        {
            Action = action;
        }

        public SelectionToolbarAction Action { get; private set; }
    }

    public sealed partial class SelectionLayer : UserControl
    {
        public const int HandleStart = 0;
        public const int HandleEnd = 1;

        public SelectionLayer()
        {
            this.InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        public event EventHandler<SelectionToolbarEventArgs> ToolbarAction;

        public event EventHandler<HandleDragEventArgs> HandleDrag;

        public void Hide()
        {
            HighlightCanvas.Children.Clear();
            HandleCanvas.Children.Clear();
            Toolbar.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
        }

        public void Show(SelectionRange range, double cellWidth, double cellHeight,
                         double padding, int rows, int cols)
        {
            if (cellWidth <= 0 || cellHeight <= 0 || cols <= 0)
            {
                Hide();
                return;
            }
            Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Visible;
            HighlightCanvas.Children.Clear();
            HandleCanvas.Children.Clear();
            Brush fill = Brush("AppAccentBrush");
            double opacity = TokenDouble("SelectionOverlayOpacity");
            if (opacity <= 0)
            {
                opacity = 0.35;
            }
            for (int row = range.StartRow; row <= range.EndRow; row++)
            {
                if (row < 0 || row >= rows)
                {
                    continue;
                }
                int from = row == range.StartRow ? range.StartCol : 0;
                int to = row == range.EndRow ? range.EndCol : cols - 1;
                if (from > to)
                {
                    continue;
                }
                var rect = new Rectangle
                {
                    Width = (to - from + 1) * cellWidth,
                    Height = cellHeight,
                    Fill = fill,
                    Opacity = opacity,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, padding + from * cellWidth);
                Canvas.SetTop(rect, padding + row * cellHeight);
                HighlightCanvas.Children.Add(rect);
            }

            AddHandle(HandleStart, padding + range.StartCol * cellWidth,
                padding + range.StartRow * cellHeight, cellWidth, cellHeight);
            AddHandle(HandleEnd, padding + (range.EndCol + 1) * cellWidth,
                padding + range.EndRow * cellHeight, cellWidth, cellHeight);
        }

        private void AddHandle(int which, double x, double y, double cellWidth, double cellHeight)
        {
            double size = TokenDouble("TouchTargetMin");
            if (size <= 0)
            {
                size = 40;
            }
            var handle = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brush("AppAccentBrush"),
                Stroke = Brush("AppOnAccentBrush"),
                StrokeThickness = TokenThickness("BorderThin").Left,
                Tag = which
            };
            Canvas.SetLeft(handle, x - size / 2);
            Canvas.SetTop(handle, y + cellHeight - size / 2);
            handle.PointerPressed += OnHandlePressed;
            handle.PointerMoved += OnHandleMoved;
            handle.PointerReleased += OnHandleReleased;
            handle.PointerCaptureLost += OnHandleReleased;
            HandleCanvas.Children.Add(handle);
        }

        private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
        {
            var ellipse = sender as Ellipse;
            if (ellipse != null)
            {
                ellipse.CapturePointer(e.Pointer);
            }
            e.Handled = true;
        }

        private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
        {
            var ellipse = sender as Ellipse;
            if (ellipse == null || !e.Pointer.IsInContact)
            {
                return;
            }
            Point point = e.GetCurrentPoint(this).Position;
            int which = ellipse.Tag is int ? (int)ellipse.Tag : HandleStart;
            EventHandler<HandleDragEventArgs> handler = HandleDrag;
            if (handler != null)
            {
                handler(this, new HandleDragEventArgs(which, point.X, point.Y));
            }
            e.Handled = true;
        }

        private void OnHandleReleased(object sender, PointerRoutedEventArgs e)
        {
            var ellipse = sender as Ellipse;
            if (ellipse != null)
            {
                try { ellipse.ReleasePointerCapture(e.Pointer); }
                catch (Exception) { }
            }
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            Raise(SelectionToolbarAction.Copy);
        }

        private void OnPasteClick(object sender, RoutedEventArgs e)
        {
            Raise(SelectionToolbarAction.Paste);
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            Raise(SelectionToolbarAction.SelectAll);
        }

        private void OnShareClick(object sender, RoutedEventArgs e)
        {
            Raise(SelectionToolbarAction.Share);
        }

        private void OnDismissClick(object sender, RoutedEventArgs e)
        {
            Raise(SelectionToolbarAction.Dismiss);
        }

        private void Raise(SelectionToolbarAction action)
        {
            EventHandler<SelectionToolbarEventArgs> handler = ToolbarAction;
            if (handler != null)
            {
                handler(this, new SelectionToolbarEventArgs(action));
            }
        }

        private static Brush Brush(string key)
        {
            return Application.Current.Resources[key] as Brush;
        }

        private static double TokenDouble(string key)
        {
            object value;
            try
            {
                value = Application.Current.Resources[key];
            }
            catch (Exception)
            {
                return 0;
            }
            return value is double ? (double)value : 0;
        }

        private static Thickness TokenThickness(string key)
        {
            object value;
            try
            {
                value = Application.Current.Resources[key];
            }
            catch (Exception)
            {
                return new Thickness();
            }
            return value is Thickness ? (Thickness)value : new Thickness();
        }
    }

    public sealed class HandleDragEventArgs : EventArgs
    {
        public HandleDragEventArgs(int handle, double x, double y)
        {
            Handle = handle;
            X = x;
            Y = y;
        }

        public int Handle { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
    }
}
