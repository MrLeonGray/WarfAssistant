using System;
using System.Windows;
using System.Windows.Controls;

namespace WarfAssistant.Controls
{
    /// <summary>
    /// Constrains its single child to an exact width-to-height aspect ratio (e.g., 16:9 = 1.7778).
    /// </summary>
    public class AspectRatioDecorator : Decorator
    {
        public static readonly DependencyProperty AspectRatioProperty =
            DependencyProperty.Register(
                nameof(AspectRatio),
                typeof(double),
                typeof(AspectRatioDecorator),
                new FrameworkPropertyMetadata(16.0 / 9.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double AspectRatio
        {
            get => (double)GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            double ratio = AspectRatio > 0.01 ? AspectRatio : (16.0 / 9.0);
            double width = !double.IsInfinity(constraint.Width) && constraint.Width > 0 ? constraint.Width : 0;

            if (width <= 0 && Child != null)
            {
                Child.Measure(constraint);
                width = Child.DesiredSize.Width;
            }

            double height = width > 0 ? Math.Round(width / ratio) : 0;
            if (!double.IsInfinity(constraint.Height) && height > constraint.Height && constraint.Height > 0)
            {
                height = constraint.Height;
                width = Math.Round(height * ratio);
            }

            Child?.Measure(new Size(width, height));
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            double ratio = AspectRatio > 0.01 ? AspectRatio : (16.0 / 9.0);
            double width = arrangeSize.Width;
            double height = width > 0 ? Math.Round(width / ratio) : arrangeSize.Height;
            if (height > arrangeSize.Height && arrangeSize.Height > 0)
            {
                height = arrangeSize.Height;
                width = Math.Round(height * ratio);
            }

            Child?.Arrange(new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }
    }
}
