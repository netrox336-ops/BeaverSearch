using System.Windows.Threading;

namespace BeaverSearch.Infrastructure;

public static class DispatcherExtensions
{
    public static void BeginInvoke(this Dispatcher dispatcher, Action action, DispatcherPriority priority)
    {
        dispatcher.BeginInvoke(priority, action);
    }
}
