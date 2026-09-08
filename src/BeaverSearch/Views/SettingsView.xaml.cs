using System.Windows.Controls;
using System.Windows.Input;
using BeaverSearch.Infrastructure;

namespace BeaverSearch.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => PageScrollHelper.Handle(PageScroll, e);
}
