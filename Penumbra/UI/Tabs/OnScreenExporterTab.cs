using Dalamud.Interface;
using Dalamud.Plugin.Services;
using ImGuiNET;
using OtterGui;
using OtterGui.Widgets;
using Penumbra.Api.Enums;
using Penumbra.Collections.Manager;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using Penumbra.GameData.Structs;
using Penumbra.Import.Models;
using Penumbra.Import.Models.Export;
using Penumbra.Interop.ResourceTree;
using Penumbra.Services;
using Penumbra.String.Classes;
using Penumbra.UI.AdvancedWindow;

namespace Penumbra.UI.Tabs;

public class OnScreenExporterTab : ITab
{
    private readonly Configuration                       _config;
    private readonly ActiveCollections                   _activeCollections;
    private readonly IDataManager                        _gameData;
    private readonly ModelManager                        _modelManager;
    private          ResourceTreeViewer                  _viewer;
    private          ExportConfig                        _exportConfig;
    private ModelExportConfig _modelExportConfig = new();
    private readonly StainService                        _stainService;
    private readonly Dictionary<ResourceNode, bool>      _modelStates = new();
    private readonly Dictionary<ResourceNode, MtrlState> _mtrlNodes = new();
    private readonly CancellationToken                   _cancel     = new();

    public class ModelExportConfig
    {
        public static Vector4 DefaultHairColor          = new Vector4(130, 64,  13,  255) / new Vector4(255);
        public static Vector4 DefaultHairHighlightColor = new Vector4(77,  126, 240, 255) / new Vector4(255);
        public static Vector4 DefaultIrisColor          = new Vector4(21,  176, 172, 255) / new Vector4(255);
        public static Vector4 DefaultSkinColor          = new Vector4(234, 183, 161, 255) / new Vector4(255);
        public static Vector4 DefaultLipColor           = new Vector4(120, 69,  104, 153) / new Vector4(255);
        
        public Vector4 HairColor          = DefaultHairColor;
        public Vector4 HairHighlightColor = DefaultHairHighlightColor;
        public Vector4 IrisColor          = DefaultIrisColor;
        public Vector4 SkinColor          = DefaultSkinColor;
        public Vector4 LipColor           = DefaultLipColor;
    }
    
    public class MtrlState
    {
        public MtrlState(MtrlFile mtrlFile, StainService stainService)
        {
            MtrlFile = mtrlFile;
            if (mtrlFile.HasDyeTable)
            {        
                StainCombo = new FilterComboColors(140, MouseWheelType.None,
                    () => stainService.StainData.Value.Prepend(new KeyValuePair<byte, (string Name, uint Dye, bool Gloss)>(0, ("None", 0, false))).ToList(), 
                    Penumbra.Log);
            }
        }
        
        public readonly MtrlFile                         MtrlFile;
        public readonly FilterComboColors?               StainCombo;
    }
    
    public OnScreenExporterTab(Configuration config,
        StainService stainService,
        ActiveCollections activeCollections, 
        IDataManager gameData, 
        ModelManager modelManager, 
        ResourceTreeFactory treeFactory, 
        ChangedItemDrawer changedItemDrawer)
    {
        _config            = config;
        _stainService = stainService;
        _activeCollections = activeCollections;
        _gameData          = gameData;
        _modelManager      = modelManager;
        _viewer            = new ResourceTreeViewer(_config, treeFactory, changedItemDrawer, 3, Refresh, DrawButtons, DrawTreeButtons);
    }

    public ReadOnlySpan<byte> Label
        => "On-Screen Exporter"u8;

    private void Refresh()
    {
        _modelStates.Clear();
        _mtrlNodes.Clear();
    }

    private void DrawTreeButtons(ResourceTree tree)
    {
        var generateMissingBones = _exportConfig.GenerateMissingBones;
        if (ImGui.Checkbox("Generate missing bones", ref generateMissingBones))
        {
            _exportConfig.GenerateMissingBones = generateMissingBones;
        }
        
        if (ImGui.Button("Export All"))
        {
            InitExport(tree, false);
        }
        
        ImGui.SameLine();

        if (ImGui.Button("Export Selected"))
        {
            InitExport(tree, true);
        }
        
        // Color picker for hair
        var currentHairColor = ImGui.ColorConvertFloat4ToU32(_modelExportConfig.HairColor);
        var defaultHairColor = ImGui.ColorConvertFloat4ToU32(ModelExportConfig.DefaultHairColor);
        Widget.ColorPicker("Hair Color", "Set the hair color for the exported model.", currentHairColor, 
                x => _modelExportConfig.HairColor = ImGui.ColorConvertU32ToFloat4(x), defaultHairColor);
        
        var currentHairHighlightColor = ImGui.ColorConvertFloat4ToU32(_modelExportConfig.HairHighlightColor);
        var defaultHairHighlightColor = ImGui.ColorConvertFloat4ToU32(ModelExportConfig.DefaultHairHighlightColor);
        Widget.ColorPicker("Hair Highlight Color", "Set the hair highlight color for the exported model.", currentHairHighlightColor,
            x => _modelExportConfig.HairHighlightColor = ImGui.ColorConvertU32ToFloat4(x), defaultHairHighlightColor);
        
        var currentIrisColor = ImGui.ColorConvertFloat4ToU32(_modelExportConfig.IrisColor);
        var defaultIrisColor = ImGui.ColorConvertFloat4ToU32(ModelExportConfig.DefaultIrisColor);
        Widget.ColorPicker("Iris Color", "Set the iris color for the exported model.", currentIrisColor,
            x => _modelExportConfig.IrisColor = ImGui.ColorConvertU32ToFloat4(x), defaultIrisColor);
        
        var currentSkinColor = ImGui.ColorConvertFloat4ToU32(_modelExportConfig.SkinColor);
        var defaultSkinColor = ImGui.ColorConvertFloat4ToU32(ModelExportConfig.DefaultSkinColor);
        Widget.ColorPicker("Skin Color", "Set the skin color for the exported model.", currentSkinColor,
            x => _modelExportConfig.SkinColor = ImGui.ColorConvertU32ToFloat4(x), defaultSkinColor);
        
        var currentLipColor = ImGui.ColorConvertFloat4ToU32(_modelExportConfig.LipColor);
        var defaultLipColor = ImGui.ColorConvertFloat4ToU32(ModelExportConfig.DefaultLipColor);
        Widget.ColorPicker("Lip Color", "Set the lip color for the exported model.", currentLipColor,
            x => _modelExportConfig.LipColor = ImGui.ColorConvertU32ToFloat4(x), defaultLipColor);
    }

    private void DrawMdlButtons(ResourceTree tree, ResourceNode node, Vector2 buttonSize)
    {
        // checkbox for export
        if (!_modelStates.TryGetValue(node, out var state))
        {
            state              = false;
            _modelStates[node] = state;
        }

        if (ImGui.Checkbox($"##{node.GetHashCode()}", ref state))
        {
            _modelStates[node] = state;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Export this model");
        }
        
        ImGui.SameLine();
        
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.FileImport.ToIconString(), buttonSize,
                $"Export this model", false, true))
        {
            Task.Run(() => InitExport(tree, node), _cancel);
        }
    }

    private void DrawMtrlButtons(ResourceNode node)
    {
        if (!_mtrlNodes.TryGetValue(node, out var mtrlState))
        {
            var mtrlFile = ReadFile(node.FullPath.ToPath());
            if (mtrlFile == null)
                return;
            
            mtrlState        = new MtrlState(new MtrlFile(mtrlFile), _stainService);
            _mtrlNodes[node] = mtrlState;
        }

        if (!mtrlState.MtrlFile.HasDyeTable || mtrlState.StainCombo == null)
            return;

        // Draw colour selector
        var (dyeId, (name, dyeColor, gloss)) = mtrlState.StainCombo.CurrentSelection;
        var label = dyeId == 0 ? "Set Dye###previewDye" : $"{name}###previewDye";
        if (mtrlState.StainCombo.Draw(label, dyeColor, string.Empty, true, gloss))
        {
            var stm     = _stainService.StmFile;
            var stainId = (StainId)mtrlState.StainCombo.CurrentSelection.Key;
                
            for (var i = 0; i < MtrlFile.ColorTable.NumRows; i++)
            {
                var dyeSet = mtrlState.MtrlFile.DyeTable[i];
                if (!stm.TryGetValue(dyeSet.Template, stainId, out var dyes))
                    continue;
                    
                mtrlState.MtrlFile.Table[i].ApplyDyeTemplate(dyeSet, dyes);
            }
        }
    }
    
    public void DrawButtons(ResourceTree tree, ResourceNode node, Vector2 buttonSize)
    {
        switch (node.Type)
        {
            case ResourceType.Mdl:
                DrawMdlButtons(tree, node, buttonSize);
                break;
            case ResourceType.Mtrl: 
                DrawMtrlButtons(node);
                break;
        }
    }

    public void DrawContent()
    {
        DrawOptions();
        
        _viewer.Draw();
    }

    private void DrawOptions()
    {
    }

    private void InitExport(ResourceTree tree, bool selectedOnly)
    {
        var nodes = tree.Nodes.ToArray();
        if (selectedOnly)
        {
            nodes = nodes.Where(x => _modelStates.TryGetValue(x, out var export) && export).ToArray();
        }
        
        PrepareExport(nodes, tree.RaceCode);
    }
    
    private void InitExport(ResourceTree tree, ResourceNode node)
    {
        if (node.Type is not ResourceType.Mdl) return;
        var nodes = new[] {node};
        
        PrepareExport(nodes, tree.RaceCode);
    }

    
    private void PrepareExport(ResourceNode[] nodes, GenderRace genderRace)
    {
        // get color tables
        var colorTables = new Dictionary<string, MtrlFile.ColorTable>();
        foreach (var (key, value) in _mtrlNodes)
        {
            if (nodes.Any(x => x.Children.Contains(key)))
            {
                colorTables[key.GamePath.ToString()] = value.MtrlFile.Table;
            }
        }
        
        _modelManager.ExportFullModelToGltf(_exportConfig, _activeCollections, _gameData, 
            nodes, 
            colorTables,
            _modelExportConfig,
            genderRace, 
            ReadFile, CreateOutputPath());
    }

    private string CreateOutputPath()
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var tmpDir = Path.Combine(Path.GetTempPath(), "Penumbra", "Export", timestamp);
        Directory.CreateDirectory(tmpDir);
        return Path.Combine(tmpDir, "out.gltf");
    }
    
    private byte[]? ReadFile(string path)
    {
        // TODO: if cross-collection lookups are turned off, this conversion can be skipped
        if (!Utf8GamePath.FromString(path, out var utf8Path, true))
            throw new Exception($"Resolved path {path} could not be converted to a game path.");

        var resolvedPath = _activeCollections.Current.ResolvePath(utf8Path) ?? new FullPath(utf8Path);

        // TODO: is it worth trying to use streams for these instead? I'll need to do this for mtrl/tex too, so might be a good idea. that said, the mtrl reader doesn't accept streams, so...
        return resolvedPath.IsRooted
            ? File.ReadAllBytes(resolvedPath.FullName)
            : _gameData.GetFile(resolvedPath.InternalName.ToString())?.Data;
    }
}
