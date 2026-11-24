using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace WarfAssistant.Views
{
    public partial class CraftingView : UserControl
    {
        public CraftingView()
        {
            InitializeComponent();
        }

        private void ResourceStockTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                BindingOperations.GetBindingExpression(tb, TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }
    }
}

