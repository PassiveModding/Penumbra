using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ImGuiNET;
using OtterGui.Raii;
using OtterGui;
using Penumbra.Interop.ResourceTree;
using Penumbra.UI.Classes;
using System.Text.Json;
using Penumbra.Services;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Xande.Files;
using Xande.Havok;

namespace Penumbra.UI.AdvancedWindow;

public class ResourceTreeViewer
{
    private const ResourceTreeFactory.Flags ResourceTreeFactoryFlags =
        ResourceTreeFactory.Flags.RedactExternalPaths |
        ResourceTreeFactory.Flags.WithUiData |
        ResourceTreeFactory.Flags.WithOwnership;

    private readonly Configuration _config;
    private readonly ResourceTreeFactory _treeFactory;
    private readonly ChangedItemDrawer _changedItemDrawer;
    private readonly int _actionCapacity;
    private readonly Action _onRefresh;
    private readonly Action<ResourceNode, Vector2> _drawActions;
    private readonly DalamudServices _dalamud;
    private readonly HashSet<nint> _unfolded;
    private readonly ModelExporter _modelExporter;
    private readonly IPluginLog _log;
    private Task<ResourceTree[]>? _task;

    public ResourceTreeViewer(Configuration config, ResourceTreeFactory treeFactory, ChangedItemDrawer changedItemDrawer,
        int actionCapacity, Action onRefresh, Action<ResourceNode, Vector2> drawActions, DalamudServices dalamud)
    {
        _config = config;
        _treeFactory = treeFactory;
        _changedItemDrawer = changedItemDrawer;
        _actionCapacity = actionCapacity;
        _onRefresh = onRefresh;
        _drawActions = drawActions;
        _dalamud = dalamud;
        _modelExporter = new ModelExporter(dalamud);
        _log = dalamud.Log;
        _unfolded = new HashSet<nint>();
        _log.Debug("Initialized ResourceTreeViewer");
    }

    private static bool IsExporting;

    public void Draw()
    {
        if (ImGui.Button("Refresh Character List") || _task == null)
            _task = RefreshCharacterList();

        using var child = ImRaii.Child("##Data");
        if (!child)
            return;

        if (!_task.IsCompleted)
        {
            ImGui.NewLine();
            ImGui.TextUnformatted("Calculating character list...");
        }
        else if (_task.Exception != null)
        {
            ImGui.NewLine();
            using var color = ImRaii.PushColor(ImGuiCol.Text, Colors.RegexWarningBorder);
            ImGui.TextUnformatted($"Error during calculation of character list:\n\n{_task.Exception}");
        }
        else if (_task.IsCompletedSuccessfully)
        {
            var debugMode = _config.DebugMode;
            foreach (var (tree, index) in _task.Result.WithIndex())
            {
                var headerColorId =
                    tree.LocalPlayerRelated ? ColorId.ResTreeLocalPlayer :
                    tree.PlayerRelated ? ColorId.ResTreePlayer :
                    tree.Networked ? ColorId.ResTreeNetworked :
                    ColorId.ResTreeNonNetworked;
                using (var c = ImRaii.PushColor(ImGuiCol.Text, headerColorId.Value()))
                {
                    var isOpen = ImGui.CollapsingHeader($"{tree.Name}##{index}", index == 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0);
                    if (debugMode)
                    {
                        using var _ = ImRaii.PushFont(UiBuilder.MonoFont);
                        ImGuiUtil.HoverTooltip(
                            $"Object Index:        {tree.GameObjectIndex}\nObject Address:      0x{tree.GameObjectAddress:X16}\nDraw Object Address: 0x{tree.DrawObjectAddress:X16}");
                    }
                    if (!isOpen)
                        continue;
                }

                using var id = ImRaii.PushId(index);

                ImGui.TextUnformatted($"Collection: {tree.CollectionName}");

                // export character button 
                if (IsExporting)
                {
                    ImGui.TextUnformatted("Exporting character");
                    if (ImGui.Button("Cancel Export"))
                    {
                        if (Cts != null)
                        {
                            Cts.Cancel();
                            Cts = null;
                            _log.Debug("Cancelled export");
                        }
                    }
                }
                else
                {
                    if (ImGui.Button("Export Character"))
                    {
                        try
                        {
                            IsExporting = true;
                            Cts = new CancellationTokenSource();
                            RunModelExport(tree);
                        }
                        catch (Exception e)
                        {
                            IsExporting = false;
                            _log.Error(e, "Error while exporting character");
                        }
                    }
                }

                using var table = ImRaii.Table("##ResourceTree", _actionCapacity > 0 ? 4 : 3,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
                if (!table)
                    continue;

                ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthStretch, 0.2f);
                ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                ImGui.TableSetupColumn("Actual Path", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                if (_actionCapacity > 0)
                    ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed,
                        (_actionCapacity - 1) * 3 * ImGuiHelpers.GlobalScale + _actionCapacity * ImGui.GetFrameHeight());
                ImGui.TableHeadersRow();

                DrawNodes(tree.Nodes, 0, unchecked(tree.DrawObjectAddress * 31));
            }
        }
    }

    private static CancellationTokenSource? Cts = null;

    private void RunModelExport(ResourceTree tree)
    {
        var path = Path.Combine(Path.GetTempPath(), "Penumbra.XandeTest");
        Directory.CreateDirectory(path);
        path = Path.Combine(path, $"{tree.Name}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}");
        Directory.CreateDirectory(path);
        var fileName = $"{tree.Name}.json";
        var filePath = Path.Combine(path, fileName);

        var json = JsonSerializer.Serialize(tree.Nodes.Select(GetResourceNodeAsJson), new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(filePath, json);

        _dalamud.Framework.RunOnTick(() =>
        {
            _log.Debug($"Exporting character to {path}");
            // skeletons should only be at the root level so no need to go further
            var skeletonNodes = tree.Nodes.Where(x => x.Type == Api.Enums.ResourceType.Sklb).ToList();
            var skeletons = new List<HavokXml>();
            try
            {

                foreach (var node in skeletonNodes)
                {
                    // cannot use fullpath because things like ivcs are fucky and crash the game
                    var nodePath = node.FullPath.ToPath();
                    try
                    {
                        var file = _modelExporter.LuminaManager.GetFile<FileResource>(nodePath);
                        var sklb = SklbFile.FromStream(file.Reader.BaseStream);

                        var xml = _modelExporter.Converter.HkxToXml(sklb.HkxData);
                        // write xml file without extension
                        File.WriteAllText(Path.Combine(path, Path.GetFileNameWithoutExtension(nodePath) + ".xml"), xml);

                        skeletons.Add(new HavokXml(xml));
                        _log.Debug($"Loaded skeleton {nodePath}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Failed to load {nodePath}, falling back to GamePath");
                    }

                    nodePath = node.GamePath.ToString();
                    try
                    {
                        var file = _modelExporter.LuminaManager.GetFile<FileResource>(nodePath);
                        var sklb = SklbFile.FromStream(file.Reader.BaseStream);

                        var xml = _modelExporter.Converter.HkxToXml(sklb.HkxData);
                        skeletons.Add(new HavokXml(xml));
                        _log.Debug($"Loaded skeleton {nodePath}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Failed to load {nodePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error loading skeletons");
                IsExporting = false;
                return Task.CompletedTask;
            }


            return Task.Run(async () =>
            {
                try
                {
                    await _modelExporter.ExportModel(path, skeletons, tree.Nodes, Cts.Token);
                    // open path 
                    Process.Start("explorer.exe", path);
                }
                catch (Exception e)
                {
                    _log.Error(e, "Error while exporting character");
                }

                IsExporting = false;
            }, Cts.Token);
        });
    }

    private object GetResourceNodeAsJson(ResourceNode node)
    {
        return new
        {
            node.Name,
            Type = node.Type.ToString(),
            GamePath = node.GamePath.ToString(),
            node.FullPath.FullName,
            node.Internal,
            Children = node.Children.Select(GetResourceNodeAsJson)
        };
    }

    private Task<ResourceTree[]> RefreshCharacterList()
        => Task.Run(() =>
        {
            try
            {
                return _treeFactory.FromObjectTable(ResourceTreeFactoryFlags)
                    .Select(entry => entry.ResourceTree)
                    .ToArray();
            }
            finally
            {
                _unfolded.Clear();
                _onRefresh();
            }
        });

    private void DrawNodes(IEnumerable<ResourceNode> resourceNodes, int level, nint pathHash)
    {
        var debugMode = _config.DebugMode;
        var frameHeight = ImGui.GetFrameHeight();
        var cellHeight = _actionCapacity > 0 ? frameHeight : 0.0f;
        foreach (var (resourceNode, index) in resourceNodes.WithIndex())
        {
            if (resourceNode.Internal && !debugMode)
                continue;

            var textColor = ImGui.GetColorU32(ImGuiCol.Text);
            var textColorInternal = (textColor & 0x00FFFFFFu) | ((textColor & 0xFE000000u) >> 1); // Half opacity

            using var mutedColor = ImRaii.PushColor(ImGuiCol.Text, textColorInternal, resourceNode.Internal);

            var nodePathHash = unchecked(pathHash + resourceNode.ResourceHandle);

            using var id = ImRaii.PushId(index);
            ImGui.TableNextColumn();
            var unfolded = _unfolded.Contains(nodePathHash);
            using (var indent = ImRaii.PushIndent(level))
            {
                var unfoldable = debugMode
                    ? resourceNode.Children.Count > 0
                    : resourceNode.Children.Any(child => !child.Internal);
                if (unfoldable)
                {
                    using var font = ImRaii.PushFont(UiBuilder.IconFont);
                    var icon = (unfolded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
                    var offset = (ImGui.GetFrameHeight() - ImGui.CalcTextSize(icon).X) / 2;
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                    ImGui.TextUnformatted(icon);
                    ImGui.SameLine(0f, offset + ImGui.GetStyle().ItemInnerSpacing.X);
                }
                else
                {
                    ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
                    ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                }

                _changedItemDrawer.DrawCategoryIcon(resourceNode.Icon);
                ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.TableHeader(resourceNode.Name);
                if (ImGui.IsItemClicked() && unfoldable)
                {
                    if (unfolded)
                        _unfolded.Remove(nodePathHash);
                    else
                        _unfolded.Add(nodePathHash);
                    unfolded = !unfolded;
                }

                if (debugMode)
                {
                    using var _ = ImRaii.PushFont(UiBuilder.MonoFont);
                    ImGuiUtil.HoverTooltip(
                        $"Resource Type:   {resourceNode.Type}\nObject Address:  0x{resourceNode.ObjectAddress:X16}\nResource Handle: 0x{resourceNode.ResourceHandle:X16}\nLength:          0x{resourceNode.Length:X16}");
                }
            }

            ImGui.TableNextColumn();
            var hasGamePaths = resourceNode.PossibleGamePaths.Length > 0;
            ImGui.Selectable(resourceNode.PossibleGamePaths.Length switch
            {
                0 => "(none)",
                1 => resourceNode.GamePath.ToString(),
                _ => "(multiple)",
            }, false, hasGamePaths ? 0 : ImGuiSelectableFlags.Disabled, new Vector2(ImGui.GetContentRegionAvail().X, cellHeight));
            if (hasGamePaths)
            {
                var allPaths = string.Join('\n', resourceNode.PossibleGamePaths);
                if (ImGui.IsItemClicked())
                    ImGui.SetClipboardText(allPaths);
                ImGuiUtil.HoverTooltip($"{allPaths}\n\nClick to copy to clipboard.");
            }

            ImGui.TableNextColumn();
            if (resourceNode.FullPath.FullName.Length > 0)
            {
                ImGui.Selectable(resourceNode.FullPath.ToPath(), false, 0, new Vector2(ImGui.GetContentRegionAvail().X, cellHeight));
                if (ImGui.IsItemClicked())
                    ImGui.SetClipboardText(resourceNode.FullPath.ToPath());
                ImGuiUtil.HoverTooltip($"{resourceNode.FullPath}\n\nClick to copy to clipboard.");
            }
            else
            {
                ImGui.Selectable("(unavailable)", false, ImGuiSelectableFlags.Disabled,
                    new Vector2(ImGui.GetContentRegionAvail().X, cellHeight));
                ImGuiUtil.HoverTooltip("The actual path to this file is unavailable.\nIt may be managed by another plug-in.");
            }

            mutedColor.Dispose();

            if (_actionCapacity > 0)
            {
                ImGui.TableNextColumn();
                using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,
                    ImGui.GetStyle().ItemSpacing with { X = 3 * ImGuiHelpers.GlobalScale });
                _drawActions(resourceNode, new Vector2(frameHeight));
            }

            if (unfolded)
                DrawNodes(resourceNode.Children, level + 1, unchecked(nodePathHash * 31));
        }
    }
}
