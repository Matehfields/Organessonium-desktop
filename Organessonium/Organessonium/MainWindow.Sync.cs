using Organessonium.Services.Sync;

namespace Organessonium;

public partial class MainWindow
{
    internal void AttachSyncHost(SyncBackgroundHost host) => _controller?.AttachSyncHost(host);
}
