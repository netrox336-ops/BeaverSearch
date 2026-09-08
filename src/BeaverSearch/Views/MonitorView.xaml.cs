using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeaverSearch.Infrastructure;

namespace BeaverSearch.Views;

public partial class MonitorView : UserControl
{
    public MonitorView() => InitializeComponent();

    private void OpenServers_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo(1);

    private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => PageScrollHelper.Handle(PageScroll, e);
}
