using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace WarfAssistant.Views
{
    public partial class EventsView : UserControl
    {
        public EventsView()
        {
            InitializeComponent();
        }

        private void NightwaveRewards_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                double next = sv.HorizontalOffset - e.Delta * 0.85;
                sv.ScrollToHorizontalOffset(Math.Clamp(next, 0, sv.ScrollableWidth));
                e.Handled = true;
            }
        }
    }
}

