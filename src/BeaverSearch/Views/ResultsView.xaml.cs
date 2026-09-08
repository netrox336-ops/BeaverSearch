using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using BeaverSearch.Models;
using BeaverSearch.Infrastructure;

namespace BeaverSearch.Views;

public partial class ResultsView : UserControl
{
    public ResultsView() => InitializeComponent();


    private void ResultsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ResultsGrid?.ItemsSource is null) return;
        var query = ResultsSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(ResultsGrid.ItemsSource);
        view.Filter = item =>
        {
            if (item is not PlayerResult player || query.Length == 0) return true;
            return player.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   player.SteamId64.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   player.Servers.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   player.MatchedBy.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        view.Refresh();
    }

    private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => PageScrollHelper.Handle(PageScroll, e);
}
