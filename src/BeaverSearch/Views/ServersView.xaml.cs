using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using BeaverSearch.Models;
using BeaverSearch.Infrastructure;

namespace BeaverSearch.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private void ServerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ServersGrid?.ItemsSource is null) return;
        var query = ServerSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(ServersGrid.ItemsSource);
        view.Filter = item =>
        {
            if (item is not ServerEntry server || query.Length == 0) return true;
            return server.Address.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   server.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   server.Map.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   server.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        view.Refresh();
    }

    private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => PageScrollHelper.Handle(PageScroll, e);
}
