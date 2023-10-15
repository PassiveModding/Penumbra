using OtterGui.Widgets;
using Penumbra.Interop.ResourceTree;
using Penumbra.Services;
using Penumbra.UI.AdvancedWindow;

namespace Penumbra.UI.Tabs;

public class OnScreenTab : ITab
{
    private readonly Configuration      _config;
    private          ResourceTreeViewer _viewer;

    public OnScreenTab(Configuration config, ResourceTreeFactory treeFactory, ChangedItemDrawer changedItemDrawer, DalamudServices dalamud)
    {
        _config = config;
        _viewer = new ResourceTreeViewer(_config, treeFactory, changedItemDrawer, 0, delegate { }, delegate { }, dalamud);
    }

    public ReadOnlySpan<byte> Label
        => "On-Screen"u8;

    public void DrawContent()
        => _viewer.Draw();
}
