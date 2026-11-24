using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WarfAssistant.Controls
{
    /// <summary>
    /// High-performance virtualizing wrap panel for uniform AlecaFrame-style cards.
    /// Realizes only visible rows (+1 buffer row) and supports container recycling.
    /// </summary>
    public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(252.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        private Size _extent;
        private Size _viewport;
        private Point _offset;
        private ScrollViewer? _scrollOwner;

        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => _offset.X;
        public double VerticalOffset => _offset.Y;
        public ScrollViewer? ScrollOwner
        {
            get => _scrollOwner;
            set => _scrollOwner = value;
        }

        private const double WheelScrollDelta = 52.0;

        public void LineUp() => SetVerticalOffset(_offset.Y - 24);
        public void LineDown() => SetVerticalOffset(_offset.Y + 24);
        public void LineLeft() => SetHorizontalOffset(_offset.X - 24);
        public void LineRight() => SetHorizontalOffset(_offset.X + 24);
        public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height * 0.85);
        public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height * 0.85);
        public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
        public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
        public void MouseWheelUp() => SetVerticalOffset(_offset.Y - WheelScrollDelta);
        public void MouseWheelDown() => SetVerticalOffset(_offset.Y + WheelScrollDelta);
        public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - WheelScrollDelta);
        public void MouseWheelRight() => SetHorizontalOffset(_offset.X + WheelScrollDelta);

        public void SetHorizontalOffset(double offset)
        {
            double maxOffset = Math.Max(0, _extent.Width - _viewport.Width);
            double clamped = Math.Clamp(offset, 0, maxOffset);
            if (Math.Abs(clamped - _offset.X) < 0.01) return;
            _offset.X = clamped;
            InvalidateArrange();
            _scrollOwner?.InvalidateScrollInfo();
        }

        public void SetVerticalOffset(double offset)
        {
            double maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
            double clamped = Math.Clamp(offset, 0, maxOffset);
            if (Math.Abs(clamped - _offset.Y) < 0.01) return;
            _offset.Y = clamped;
            InvalidateMeasure();
            _scrollOwner?.InvalidateScrollInfo();
        }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            DependencyObject? cur = visual;
            while (cur != null && !ReferenceEquals(VisualTreeHelper.GetParent(cur), this))
                cur = VisualTreeHelper.GetParent(cur);

            if (cur is UIElement element)
            {
                int idx = InternalChildren.IndexOf(element);
                if (idx >= 0)
                {
                    int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(idx, 0));
                    if (itemIndex >= 0)
                    {
                        int cols = CalculateColumns(_viewport.Width);
                        int row = itemIndex / cols;
                        double top = row * ItemHeight;
                        double bottom = top + ItemHeight;
                        if (top < _offset.Y) SetVerticalOffset(top);
                        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
                    }
                }
            }
            return rectangle;
        }

        private int CalculateColumns(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 1;
            return Math.Max(1, (int)Math.Floor(width / Math.Max(40, ItemWidth)));
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                    if (args.Position.Index >= 0 && args.Position.Offset == 0 && args.ItemUICount > 0 &&
                        args.Position.Index + args.ItemUICount <= InternalChildren.Count)
                    {
                        RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    }
                    else
                    {
                        InvalidateMeasure();
                    }
                    break;
                case NotifyCollectionChangedAction.Move:
                    if (args.OldPosition.Index >= 0 && args.OldPosition.Offset == 0 && args.ItemUICount > 0 &&
                        args.OldPosition.Index + args.ItemUICount <= InternalChildren.Count)
                    {
                        RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                    }
                    else
                    {
                        InvalidateMeasure();
                    }
                    break;
                case NotifyCollectionChangedAction.Reset:
                    if (InternalChildren.Count > 0)
                        RemoveInternalChildRange(0, InternalChildren.Count);
                    _offset = new Point(0, 0);
                    InvalidateMeasure();
                    _scrollOwner?.InvalidateScrollInfo();
                    break;
            }
        }

        protected override void BringIndexIntoView(int index)
        {
            if (index < 0) return;
            int cols = CalculateColumns(_viewport.Width);
            int row = index / cols;
            double targetY = row * ItemHeight;
            SetVerticalOffset(targetY);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Touch InternalChildren before accessing ItemContainerGenerator (WPF panel contract)
            UIElementCollection children = InternalChildren;
            IItemContainerGenerator generator = ItemContainerGenerator;

            var itemsOwner = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsOwner?.Items.Count ?? 0;

            double cellW = Math.Max(40, ItemWidth);
            double cellH = Math.Max(40, ItemHeight);

            double viewW = double.IsInfinity(availableSize.Width) ? Math.Max(cellW, _viewport.Width) : availableSize.Width;
            double viewH = double.IsInfinity(availableSize.Height) ? Math.Max(cellH, _viewport.Height) : availableSize.Height;

            int cols = CalculateColumns(viewW);
            int totalRows = itemCount == 0 ? 0 : (int)Math.Ceiling((double)itemCount / cols);

            Size newExtent = new Size(cols * cellW, totalRows * cellH);
            Size newViewport = new Size(viewW, viewH);

            bool scrollChanged = newExtent != _extent || newViewport != _viewport;
            _extent = newExtent;
            _viewport = newViewport;

            double maxY = Math.Max(0, _extent.Height - _viewport.Height);
            if (_offset.Y > maxY) _offset.Y = maxY;
            if (_offset.X > 0) _offset.X = 0;

            if (scrollChanged)
                _scrollOwner?.InvalidateScrollInfo();

            if (itemCount == 0 || generator == null)
            {
                CleanUpItems(children, generator, 0, -1);
                return new Size(
                    double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                    double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
            }

            // Determine visible row band (+1 buffer row above & below)
            int firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / cellH) - 1);
            int lastVisibleRow = Math.Min(Math.Max(0, totalRows - 1), (int)Math.Floor((_offset.Y + viewH) / cellH) + 1);

            int firstIndex = firstVisibleRow * cols;
            int lastIndex = Math.Min(itemCount - 1, (lastVisibleRow + 1) * cols - 1);

            GeneratorPosition startPos = generator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

            using (generator.StartAt(startPos, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
                {
                    var child = generator.GenerateNext(out bool newlyRealized) as UIElement;
                    if (child == null) continue;

                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(childIndex, child);

                        generator.PrepareItemContainer(child);
                    }
                    else if (!children.Contains(child))
                    {
                        if (childIndex >= children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(childIndex, child);
                    }

                    child.Measure(new Size(cellW, cellH));
                }
            }

            CleanUpItems(children, generator, firstIndex, lastIndex);

            return new Size(
                double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height);
        }

        private void CleanUpItems(UIElementCollection children, IItemContainerGenerator? generator, int minDesiredIndex, int maxDesiredIndex)
        {
            if (generator == null) return;
            var recycling = generator as IRecyclingItemContainerGenerator;
            bool recycleEnabled = VirtualizingPanel.GetVirtualizationMode(this) == VirtualizationMode.Recycling;

            for (int i = children.Count - 1; i >= 0; i--)
            {
                var pos = new GeneratorPosition(i, 0);
                int itemIndex = generator.IndexFromGeneratorPosition(pos);

                if (itemIndex < minDesiredIndex || itemIndex > maxDesiredIndex)
                {
                    if (itemIndex < 0)
                    {
                        // Orphaned container from cleared/reset collection
                        RemoveInternalChildRange(i, 1);
                    }
                    else
                    {
                        RemoveInternalChildRange(i, 1);
                        if (recycleEnabled && recycling != null)
                            recycling.Recycle(pos, 1);
                        else
                            generator.Remove(pos, 1);
                    }
                }
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            IItemContainerGenerator? generator = ItemContainerGenerator;
            if (generator == null) return finalSize;

            double cellW = Math.Max(40, ItemWidth);
            double cellH = Math.Max(40, ItemHeight);
            int cols = CalculateColumns(finalSize.Width);

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (itemIndex < 0) continue;

                int row = itemIndex / cols;
                int col = itemIndex % cols;

                double x = col * cellW - _offset.X;
                double y = row * cellH - _offset.Y;
                child.Arrange(new Rect(x, y, cellW, cellH));
            }

            return finalSize;
        }
    }
}
