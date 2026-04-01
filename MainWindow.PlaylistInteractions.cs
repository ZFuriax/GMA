using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicPlayer
{
    public partial class MainWindow
    {
        private static bool IsClickOnScrollBarOrThumb(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is ScrollBar || d is Thumb)
                    return true;

                d = VisualTreeHelper.GetParent(d);
            }

            return false;
        }

        private void PlaylistList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            PlaylistList.Focus();

            // If the click is on the scrollbar/thumb, do NOT arm re-order dragging.
            if (IsClickOnScrollBarOrThumb(e.OriginalSource as DependencyObject))
            {
                _playlistDragArmed = false;
                return;
            }

            // Only arm dragging if they clicked an actual item.
            var item = GetListBoxItemFromEventSource(e.OriginalSource as DependencyObject);
            if (item == null)
            {
                _playlistDragArmed = false;
                return;
            }

            _playlistDragStartPoint = e.GetPosition(PlaylistList);
            _playlistDragArmed = true;

            // Preserve existing selection behavior.
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == ModifierKeys.None)
                PlaylistList.SelectedItem = item.Content;
        }

        private void PlaylistList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (IsClickOnScrollBarOrThumb(e.OriginalSource as DependencyObject))
            {
                _playlistDragArmed = false;
                return;
            }

            if (!_playlistDragArmed)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _playlistDragArmed = false;
                return;
            }

            var pos = e.GetPosition(PlaylistList);
            if (Math.Abs(pos.X - _playlistDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _playlistDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (PlaylistList.SelectedItems.Count != 1)
            {
                _playlistDragArmed = false;
                return;
            }

            int fromIndex = PlaylistList.SelectedIndex;
            if (fromIndex < 0 || fromIndex >= Active.Tracks.Count)
            {
                _playlistDragArmed = false;
                return;
            }

            _playlistDragArmed = false;

            DragDrop.DoDragDrop(
                PlaylistList,
                new DataObject(PlaylistDragFormat, fromIndex),
                DragDropEffects.Move);
        }

        private void PlaylistList_PreviewDragLeave(object sender, DragEventArgs e) => ClearInsertionAdorner();

        private void PlaylistList_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Let the Window-level handlers process file/folder drops.
                e.Handled = false;
                return;
            }

            if (!e.Data.GetDataPresent(PlaylistDragFormat))
            {
                e.Effects = DragDropEffects.None;
                ClearInsertionAdorner();
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;

            int insertIndex = GetInsertIndexFromPoint(
                e.GetPosition(PlaylistList),
                out bool drawAbove,
                out ListBoxItem? itemUnderMouse);

            if (itemUnderMouse != null)
            {
                ShowInsertionAdorner(itemUnderMouse, drawAbove, insertIndex);
            }
            else
            {
                var last = PlaylistList.ItemContainerGenerator.ContainerFromIndex(Active.Tracks.Count - 1) as ListBoxItem;
                if (last != null)
                    ShowInsertionAdorner(last, drawAbove: false, insertIndex: Active.Tracks.Count);
                else
                    ClearInsertionAdorner();
            }

            e.Handled = true;
        }

        private void PlaylistList_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Let the Window-level handlers process file/folder drops.
                e.Handled = false;
                return;
            }

            try
            {
                if (!e.Data.GetDataPresent(PlaylistDragFormat))
                {
                    e.Handled = true;
                    return;
                }

                int fromIndex = (int)e.Data.GetData(PlaylistDragFormat);
                int toIndex = GetInsertIndexFromPoint(e.GetPosition(PlaylistList), out _, out _);

                MovePlaylistItem(fromIndex, toIndex);

                e.Handled = true;
            }
            finally
            {
                ClearInsertionAdorner();
            }
        }

        private int GetInsertIndexFromPoint(Point point, out bool drawAbove, out ListBoxItem? itemUnderMouse)
        {
            drawAbove = false;
            itemUnderMouse = null;

            if (Active.Tracks.Count == 0)
                return 0;

            DependencyObject? element = PlaylistList.InputHitTest(point) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            itemUnderMouse = element as ListBoxItem;

            if (itemUnderMouse == null)
                return Active.Tracks.Count;

            int index = PlaylistList.ItemContainerGenerator.IndexFromContainer(itemUnderMouse);

            Point posInItem = Mouse.GetPosition(itemUnderMouse);
            drawAbove = posInItem.Y < (itemUnderMouse.ActualHeight / 2.0);

            return drawAbove ? index : index + 1;
        }

        private void ShowInsertionAdorner(ListBoxItem targetItem, bool drawAbove, int insertIndex)
        {
            if (_playlistAdornerLayer == null)
                _playlistAdornerLayer = AdornerLayer.GetAdornerLayer(targetItem);

            if (_playlistAdornerLayer == null)
                return;

            if (_insertionAdorner != null)
            {
                if (ReferenceEquals(_insertionAdorner.AdornedElement, targetItem) &&
                    _insertionAdorner.DrawAbove == drawAbove &&
                    _currentInsertIndex == insertIndex)
                {
                    return;
                }

                _playlistAdornerLayer.Remove(_insertionAdorner);
                _insertionAdorner = null;
            }

            _insertionAdorner = new InsertionAdorner(targetItem, drawAbove);
            _playlistAdornerLayer.Add(_insertionAdorner);
            _currentInsertIndex = insertIndex;
        }

        private void ClearInsertionAdorner()
        {
            if (_insertionAdorner != null && _playlistAdornerLayer != null)
            {
                _playlistAdornerLayer.Remove(_insertionAdorner);
            }

            _insertionAdorner = null;
            _playlistAdornerLayer = null;
            _currentInsertIndex = -1;
        }

        private sealed class InsertionAdorner : Adorner
        {
            public bool DrawAbove { get; }

            public InsertionAdorner(UIElement adornedElement, bool drawAbove)
                : base(adornedElement)
            {
                DrawAbove = drawAbove;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);

                var rect = new Rect(this.AdornedElement.RenderSize);
                double y = DrawAbove ? 0 : rect.Height;

                var pen = new Pen(Brushes.LimeGreen, 2);

                dc.DrawLine(pen, new Point(0, y), new Point(rect.Width, y));
            }
        }
    }
}