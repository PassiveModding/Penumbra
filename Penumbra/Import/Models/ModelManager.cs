using Dalamud.Plugin.Services;
using Lumina.Data.Parsing;
using OtterGui;
using OtterGui.Tasks;
using Penumbra.Api.Enums;
using Penumbra.Collections.Manager;
using Penumbra.GameData;
using Penumbra.GameData.Data;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using Penumbra.GameData.Structs;
using Penumbra.Import.Models.Export;
using Penumbra.Import.Models.Import;
using Penumbra.Import.Textures;
using Penumbra.Interop.ResourceTree;
using Penumbra.Meta.Manipulations;
using Penumbra.String.Classes;
using Penumbra.UI.Tabs;
using SharpGLTF.IO;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Penumbra.Import.Models;

using Schema2 = SharpGLTF.Schema2;
using LuminaMaterial = Lumina.Models.Materials.Material;

public sealed partial class ModelManager(IFramework framework, ActiveCollections collections, GamePathParser parser) : SingleTaskQueue, IDisposable
{
    private readonly IFramework _framework = framework;

    private readonly ConcurrentDictionary<IAction, (Task, CancellationTokenSource)> _tasks = new();

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var (_, cancel) in _tasks.Values.ToArray())
            cancel.Cancel();
        _tasks.Clear();
    }
    
    public Task<IoNotifier> ExportFullModelToGltf(in ExportConfig config, 
        ActiveCollections activeCollections, 
        IDataManager gameData, 
        IEnumerable<ResourceNode> modelNodes, 
        IReadOnlyDictionary<string, MtrlFile.ColorTable> colorTables,
        OnScreenExporterTab.ModelExportConfig modelExportConfig,
        GenderRace raceCode, 
        Func<string, byte[]?> read, 
        string outputPath)
        => EnqueueWithResult(
            new ExportFullModelToGltfAction(this, activeCollections, gameData, config, modelNodes, colorTables, modelExportConfig, raceCode, read, outputPath),
            action => action.Notifier
        );

    public Task<IoNotifier> ExportToGltf(in ExportConfig config, MdlFile mdl, IEnumerable<string> sklbPaths, Func<string, byte[]?> read, string outputPath)
        => EnqueueWithResult(
            new ExportToGltfAction(this, config, mdl, sklbPaths, read, outputPath),
            action => action.Notifier
        );

    public Task<(MdlFile?, IoNotifier)> ImportGltf(string inputPath)
        => EnqueueWithResult(
            new ImportGltfAction(inputPath),
            action => (action.Out, action.Notifier)
        );

    /// <summary> Try to find the .sklb paths for a .mdl file. </summary>
    /// <param name="mdlPath"> .mdl file to look up the skeletons for. </param>
    /// <param name="estManipulations"> Modified extra skeleton template parameters. </param>
    public string[] ResolveSklbsForMdl(string mdlPath, EstManipulation[] estManipulations)
    {
        var info = parser.GetFileInfo(mdlPath);
        if (info.FileType is not FileType.Model)
            return [];

        var baseSkeleton = GamePaths.Skeleton.Sklb.Path(info.GenderRace, "base", 1);

        return info.ObjectType switch
        {
            ObjectType.Equipment when info.EquipSlot.ToSlot() is EquipSlot.Body
                => [baseSkeleton, ..ResolveEstSkeleton(EstManipulation.EstType.Body, info, estManipulations)],
            ObjectType.Equipment when info.EquipSlot.ToSlot() is EquipSlot.Head
                => [baseSkeleton, ..ResolveEstSkeleton(EstManipulation.EstType.Head, info, estManipulations)],
            ObjectType.Equipment                                                      => [baseSkeleton],
            ObjectType.Accessory                                                      => [baseSkeleton],
            ObjectType.Character when info.BodySlot is BodySlot.Body or BodySlot.Tail => [baseSkeleton],
            ObjectType.Character when info.BodySlot is BodySlot.Hair
                => [baseSkeleton, ..ResolveEstSkeleton(EstManipulation.EstType.Hair, info, estManipulations)],
            ObjectType.Character when info.BodySlot is BodySlot.Face or BodySlot.Ear
                => [baseSkeleton, ..ResolveEstSkeleton(EstManipulation.EstType.Face, info, estManipulations)],
            ObjectType.Character => throw new Exception($"Currently unsupported human model type \"{info.BodySlot}\"."),
            ObjectType.DemiHuman => [GamePaths.DemiHuman.Sklb.Path(info.PrimaryId)],
            ObjectType.Monster   => [GamePaths.Monster.Sklb.Path(info.PrimaryId)],
            ObjectType.Weapon    => [GamePaths.Weapon.Sklb.Path(info.PrimaryId)],
            _                    => [],
        };
    }

    private string[] ResolveEstSkeleton(EstManipulation.EstType type, GameObjectInfo info, EstManipulation[] estManipulations)
    {
        // Try to find an EST entry from the manipulations provided.
        var (gender, race) = info.GenderRace.Split();
        var modEst = estManipulations
            .FirstOrNull(est =>
                est.Gender == gender
             && est.Race == race
             && est.Slot == type
             && est.SetId == info.PrimaryId
            );

        // Try to use an entry from provided manipulations, falling back to the current collection.
        var targetId = modEst?.Entry
         ?? collections.Current.MetaCache?.GetEstEntry(type, info.GenderRace, info.PrimaryId)
         ?? 0;

        // If there's no entries, we can assume that there's no additional skeleton.
        if (targetId == 0)
            return [];

        return [GamePaths.Skeleton.Sklb.Path(info.GenderRace, EstManipulation.ToName(type), targetId)];
    }

    /// <summary> Try to resolve the absolute path to a .mtrl from the potentially-partial path provided by a model. </summary>
    private string? ResolveMtrlPath(string rawPath, IoNotifier notifier)
    {
        // TODO: this should probably be chosen in the export settings
        var variantId = 1;

        // Get standardised paths
        var absolutePath = rawPath.StartsWith('/')
            ? LuminaMaterial.ResolveRelativeMaterialPath(rawPath, variantId)
            : rawPath;
        var relativePath = rawPath.StartsWith('/')
            ? rawPath
            : '/' + Path.GetFileName(rawPath);

        if (absolutePath == null)
        {
            notifier.Warning($"Material path \"{rawPath}\" could not be resolved.");
            return null;
        }

        var info = parser.GetFileInfo(absolutePath);
        if (info.FileType is not FileType.Material)
        {
            notifier.Warning($"Material path {rawPath} does not conform to material conventions.");
            return null;
        }

        var resolvedPath = info.ObjectType switch
        {
            ObjectType.Character => GamePaths.Character.Mtrl.Path(
                info.GenderRace, info.BodySlot, info.PrimaryId, relativePath, out _, out _, info.Variant),
            _ => absolutePath,
        };

        Penumbra.Log.Debug($"Resolved material {rawPath} to {resolvedPath}");

        return resolvedPath;
    }

    private Task Enqueue(IAction action)
    {
        if (_disposed)
            return Task.FromException(new ObjectDisposedException(nameof(ModelManager)));

        Task task;
        lock (_tasks)
        {
            task = _tasks.GetOrAdd(action, a =>
            {
                var token = new CancellationTokenSource();
                var t     = Enqueue(a, token.Token);
                t.ContinueWith(_ =>
                {
                    lock (_tasks)
                    {
                        return _tasks.TryRemove(a, out var unused);
                    }
                }, CancellationToken.None);
                return (t, token);
            }).Item1;
        }

        return task;
    }

    private Task<TOut> EnqueueWithResult<TAction, TOut>(TAction action, Func<TAction, TOut> process)
        where TAction : IAction
        => Enqueue(action).ContinueWith(task =>
        {
            if (task is { IsFaulted: true, Exception: not null })
                throw task.Exception;

            return process(action);
        });

    public partial class ExportFullModelToGltfAction(
        ModelManager manager,
        ActiveCollections activeCollections,
        IDataManager gameData,
        ExportConfig config,
        IEnumerable<ResourceNode> nodes,
        IReadOnlyDictionary<string, MtrlFile.ColorTable> colorTables,
        OnScreenExporterTab.ModelExportConfig modelExportConfig,
        GenderRace raceCode,
        Func<string, byte[]?> read,
        string outputPath)
        : IAction
    {
        public readonly IoNotifier Notifier = new();
        
        public void Execute(CancellationToken cancel)
        {
            var lowPolyModelRegex = LowPolyModelRegex();
            var models = nodes
                .Where(x => x.Type is ResourceType.Mdl)
                .Where(x => !lowPolyModelRegex.IsMatch(x.GamePath.ToString()))
                .ToArray();

            var pbdFile = gameData.GetFile<PbdFile>("chara/xls/boneDeformer/human.pbd");
            if (pbdFile == null)
            {
                Penumbra.Log.Error("Could not find bone deformer file.");
                return;
            }


            // Resolve all skeletons for the models
            var skeletons = ProcessSkeletons(models, cancel);
            var skeleton = ModelExporter.ConvertSkeleton(skeletons)!.Value;
            var scene    = new SceneBuilder();
            scene.AddNode(skeleton.Root);
            
            foreach (var node in models)
            {
                try
                {
                    Penumbra.Log.Information($"Exporting {node.FullPath}.");
                    var actualPath = node.FullPath;
                    var mdlBytes   = read(actualPath.ToPath());
                    if (mdlBytes == null)
                    {
                        Penumbra.Log.Error($"Could not find file {actualPath}.");
                        continue;
                    }

                    var mdlFile = new MdlFile(mdlBytes);

                    var materials = CreateMaterials(node.Children, mdlFile, cancel)
                        .ToDictionary(pair => pair.Key, pair => pair.Value);

                    // Build deform from the shared model to the requested race code
                    var fromDeform = RaceDeformer.RaceCodeFromPath(node.GamePath.ToString());
                    RaceDeformer? raceDeformer = null;
                    if (fromDeform != null)
                    {
                        Penumbra.Log.Information($"Setup deform for {actualPath} From {fromDeform} To {(ushort)raceCode}.");
                        raceDeformer = new RaceDeformer(pbdFile, skeleton, fromDeform.Value, (ushort)raceCode);
                    }

                    var model = ModelExporter.Export(config, mdlFile, skeletons, materials, raceDeformer, Notifier);

                    AddMeshesToScene(scene, model, skeleton);
                }
                catch (Exception e)
                {
                    Penumbra.Log.Error($"Error exporting {node.FullPath}:\n{e}");
                }
            }

            var gltfModel  = scene.ToGltf2();
            gltfModel.SaveGLTF(outputPath);
            Penumbra.Log.Information($"Exported to {outputPath}.");
            
            var folder = Path.GetDirectoryName(outputPath)!;
            Process.Start("explorer.exe", folder);
        }
        
        private static void AddMeshesToScene(SceneBuilder scene, ModelExporter.Model model, GltfSkeleton skeleton)
        {
            foreach (var mesh in model.Meshes)
            {
                foreach (var data in mesh.Meshes)
                {
                    var extras = new Dictionary<string, object>(data.Attributes.Length);
                    foreach (var attribute in data.Attributes)
                        extras.Add(attribute, true);

                    if (extras.ContainsKey("atr_eye_a"))
                    {
                        // reaper eye on player models. we don't want them
                        continue;
                    }
                    
                    // Use common skeleton here since we're exporting the full model
                    var instance = mesh.Skeleton != null ? 
                        scene.AddSkinnedMesh(data.Mesh, Matrix4x4.Identity, [.. skeleton.Joints]) : 
                        scene.AddRigidMesh(data.Mesh, Matrix4x4.Identity);

                    instance.WithExtras(JsonContent.CreateFrom(extras));
                }
            }
        }
        
        private HashSet<XivSkeleton> ProcessSkeletons(IEnumerable<ResourceNode> models, 
            CancellationToken cancel)
        {
            var skeletons = new HashSet<XivSkeleton>();
            foreach (var node in models)
            {
                var nodeSkeletons = manager.ResolveSklbsForMdl(node.GamePath.ToString(),
                    GetEstManipulationsForPath(node.GamePath, activeCollections));
                var xivSkeletons = Util.BuildSkeletons(nodeSkeletons, manager._framework, read, cancel).ToArray();

                // filter out duplicate skeletons
                foreach (var skeleton in xivSkeletons)
                {
                    if (skeletons.Any(x => x.Equals(skeleton))) continue;
                    skeletons.Add(skeleton);
                }
            }

            return skeletons;
        }

        private Dictionary<string, MaterialExporter.Material> CreateMaterials(IEnumerable<ResourceNode> mtrlNodes, MdlFile mdlFile, CancellationToken cancel)
        {
            var materials = new Dictionary<string, MaterialExporter.Material>();
            foreach (var mtrlNode in mtrlNodes)
            {
                if (mtrlNode.Type != ResourceType.Mtrl)
                    continue;

                var mtrlFullPath = mtrlNode.FullPath.ToPath();
                var nodeGamePath = mtrlNode.GamePath.ToString();

                var bytes = read(mtrlFullPath);
                if (bytes == null)
                    continue;

                var mtrl = new MtrlFile(bytes);
                
                var colorTable = colorTables.TryGetValue(nodeGamePath, out var table) ? table : mtrl.Table;
                mtrl.Table = colorTable;
                        
                var material = new MaterialExporter.Material
                {
                    Mtrl = mtrl,
                    Textures = mtrl.ShaderPackage.Samplers.ToDictionary(
                        sampler => (TextureUsage)sampler.SamplerId,
                        sampler => Util.ConvertImage(mtrl.Textures[sampler.TextureIndex], read, cancel)
                    ),
                };

                switch (mtrl.ShaderPackage.Name)
                {
                    case "hair.shpk":
                        material.BaseColor      = modelExportConfig.HairColor;
                        material.HighlightColor = modelExportConfig.HairHighlightColor;
                        break;
                    case "iris.shpk": 
                        material.BaseColor = modelExportConfig.IrisColor;
                        break;
                    case "skin.shpk":
                        material.BaseColor      = modelExportConfig.SkinColor;
                        material.HighlightColor = modelExportConfig.LipColor;
                        break;
                }

                // since the race may be different, find most similar by comparing the gamepath of the node
                // ie. au ra skin textures on a shirt with a hyur base
                var mostSimilar = mdlFile.Materials
                    .Select(modelDefaultGamePath => (modelDefaultGamePath, similarity: ComputeLd(modelDefaultGamePath, nodeGamePath)))
                    .MinBy(pair => pair.similarity)
                    .modelDefaultGamePath;
                        
                Penumbra.Log.Information($"Most similar path for {mtrlFullPath} is {mostSimilar}.");
                        
                materials[mostSimilar] = material;
            }
            
            return materials;
        }

        private static int ComputeLd(string source1, string source2) //O(n*m)
        {
            var source1Length = source1.Length;
            var source2Length = source2.Length;

            var matrix = new int[source1Length + 1, source2Length + 1];

            // First calculation, if one entry is empty return full length
            if (source1Length == 0)
                return source2Length;

            if (source2Length == 0)
                return source1Length;

            // Initialization of matrix with row size source1Length and columns size source2Length
            for (var i = 0; i <= source1Length; matrix[i, 0] = i++){}
            for (var j = 0; j <= source2Length; matrix[0, j] = j++){}

            // Calculate rows and collumns distances
            for (var i = 1; i <= source1Length; i++)
            {
                for (var j = 1; j <= source2Length; j++)
                {
                    var cost = (source2[j - 1] == source1[i - 1]) ? 0 : 1;

                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }
            // return result
            return matrix[source1Length, source2Length];
        }
        
        public bool Equals(IAction? other)
        {
            if (other is not ExportFullModelToGltfAction rhs)
                return false;

            return true;
        }

        public static EstManipulation[] GetEstManipulationsForPath(Utf8GamePath gamePath, ActiveCollections activeCollections)
        {
            if (!activeCollections.Current.ResolvedFiles.TryGetValue(gamePath, out var option))
                return Array.Empty<EstManipulation>();
        
            Penumbra.Log.Information($"Found option {option.Mod.Name} for path {gamePath}.");
        
            return option.Mod.AllSubMods
                //.Where(subMod => subMod != option)
                //.Prepend(option)
                .SelectMany(subMod => subMod.Manipulations)
                .Where(manipulation => manipulation.ManipulationType is MetaManipulation.Type.Est)
                .Select(manipulation => manipulation.Est)
                .ToArray();
        }
        
        [GeneratedRegex("^chara/human/c\\d+/obj/body/b0003/model/c\\d+b0003_top.mdl$")]
        private static partial Regex LowPolyModelRegex();
    }
    
    private class ExportToGltfAction(
        ModelManager manager,
        ExportConfig config,
        MdlFile mdl,
        IEnumerable<string> sklbPaths,
        Func<string, byte[]?> read,
        string outputPath)
        : IAction
    {
        public readonly IoNotifier Notifier = new();

        public void Execute(CancellationToken cancel)
        {
            Penumbra.Log.Debug($"[GLTF Export] Exporting model to {outputPath}...");

            Penumbra.Log.Debug("[GLTF Export] Reading skeletons...");
            var xivSkeletons = Util.BuildSkeletons(sklbPaths, manager._framework, read, cancel);

            Penumbra.Log.Debug("[GLTF Export] Reading materials...");
            var materials = mdl.Materials
                .Select(path => (path, material: Util.BuildMaterial(path, manager, read, Notifier, cancel)))
                .Where(pair => pair.material != null)
                .ToDictionary(pair => pair.path, pair => pair.material!.Value);

            Penumbra.Log.Debug("[GLTF Export] Converting model...");
            var model = ModelExporter.Export(config, mdl, xivSkeletons, materials, null, Notifier);

            Penumbra.Log.Debug("[GLTF Export] Building scene...");
            var scene = new SceneBuilder();
            model.AddToScene(scene);

            Penumbra.Log.Debug("[GLTF Export] Saving...");
            var gltfModel = scene.ToGltf2();
            gltfModel.SaveGLTF(outputPath);
            Penumbra.Log.Debug("[GLTF Export] Done.");
        }

        public bool Equals(IAction? other)
        {
            if (other is not ExportToGltfAction rhs)
                return false;

            // TODO: compare configuration and such
            return true;
        }
    }

    public static class Util
    {
        public static Dictionary< string, NodeBuilder > GetBoneMap( XivSkeleton[] skeletons, out NodeBuilder? root ) {
            Dictionary< string, NodeBuilder > boneMap = new();
            root = null;

            foreach( var skeleton in skeletons ) {

                for( var j = 0; j < skeleton.Bones.Length; j++ ) {
                    var bone = skeleton.Bones[ j ];
                    if( boneMap.ContainsKey( bone.Name ) ) continue;

                    var nodeBuilder = new NodeBuilder(bone.Name);

                    var transform = new AffineTransform(bone.Transform.Scale, bone.Transform.Rotation, bone.Transform.Translation);
                    nodeBuilder.SetLocalTransform( transform, false );

                    
                    if( bone.ParentIndex != -1 ) {
                        var parent = skeleton.Bones[ bone.ParentIndex ];
                        if( !boneMap.TryGetValue( parent.Name, out var parentNode ) ) {
                            parentNode = new NodeBuilder( parent.Name );
                            boneMap[ parent.Name ] = parentNode;
                        }
                    } else { 
                        root = nodeBuilder;
                    }

                    boneMap[ bone.Name ] = nodeBuilder;
                }
            }

            return boneMap;
        }
        
        /// <summary>Creates an affine transform for a bone from the reference pose in the Havok XML file.</summary>
        /// <param name="refPos">The reference pose.</param>
        /// <returns>The affine transform.</returns>
        /// <exception cref="Exception">Thrown if the reference pose is invalid.</exception>
        public static AffineTransform CreateAffineTransform( ReadOnlySpan< float > refPos ) {
            // Compared with packfile vs tagfile and xivModdingFramework code
            if( refPos.Length < 11 ) throw new Exception( "RefPos does not contain enough values for affine transformation." );
            var translation = new Vector3( refPos[ 0 ], refPos[ 1 ], refPos[ 2 ] );
            var rotation    = new Quaternion( refPos[ 4 ], refPos[ 5 ], refPos[ 6 ], refPos[ 7 ] );
            var scale       = new Vector3( refPos[ 8 ], refPos[ 9 ], refPos[ 10 ] );
            return new AffineTransform( scale, rotation, translation );
        }
        
        /// <summary> Attempt to read out the pertinent information from the sklb file paths provided. </summary>
        public static IEnumerable<XivSkeleton> BuildSkeletons(IEnumerable<string> sklbPaths, IFramework framework, Func<string, byte[]?> read, CancellationToken cancel)
        {
            // We're intentionally filtering failed reads here - the failure will
            // be picked up, if relevant, when the model tries to create mappings
            // for a bone in the failed sklb.
            var havokTasks = sklbPaths
                .Select(read)
                .Where(bytes => bytes != null)
                .Select(bytes => new SklbFile(bytes!))
                .WithIndex()
                .Select(CreateHavokTask)
                .ToArray();

            // Result waits automatically.
            return havokTasks.Select(task => SkeletonConverter.FromXml(task.Result));

            // The havok methods we're relying on for this conversion are a bit
            // finicky at the best of times, and can outright cause a CTD if they
            // get upset. Running each conversion on its own tick seems to make
            // this consistently non-crashy across my testing.
            Task<string> CreateHavokTask((SklbFile Sklb, int Index) pair)
                => framework.RunOnTick(
                    () => HavokConverter.HkxToXml(pair.Sklb.Skeleton),
                    delayTicks: pair.Index, cancellationToken: cancel);
        }

        /// <summary> Read a .mtrl and populate its textures. </summary>
        public static MaterialExporter.Material? BuildMaterial(string relativePath, ModelManager manager, Func<string, byte[]?> read, IoNotifier notifier, CancellationToken cancel)
        {
            var path = manager.ResolveMtrlPath(relativePath, notifier);
            if (path == null)
                return null;
            var bytes = read(path);
            if (bytes == null)
                return null;
            var mtrl = new MtrlFile(bytes);

            return new MaterialExporter.Material
            {
                Mtrl = mtrl,
                Textures = mtrl.ShaderPackage.Samplers.ToDictionary(
                    sampler => (TextureUsage)sampler.SamplerId,
                    sampler => ConvertImage(mtrl.Textures[sampler.TextureIndex], read, cancel)
                ),
            };
        }

        /// <summary> Read a texture referenced by a .mtrl and convert it into an ImageSharp image. </summary>
        public static Image<Rgba32> ConvertImage(MtrlFile.Texture texture, Func<string, byte[]?> read, CancellationToken cancel)
        {
            // Work out the texture's path - the DX11 material flag controls a file name prefix.
            GamePaths.Tex.HandleDx11Path(texture, out var texturePath);
            var bytes = read(texturePath);
            if (bytes == null)
                return CreateDummyImage();

            using var textureData = new MemoryStream(bytes);
            var       image       = TexFileParser.Parse(textureData);
            var       pngImage    = TextureManager.ConvertToPng(image, cancel).AsPng;
            return pngImage ?? throw new Exception("Failed to convert texture to png.");
        }

        private static Image<Rgba32> CreateDummyImage()
        {
            var image = new Image<Rgba32>(1, 1);
            image[0, 0] = Color.White;
            return image;
        }
    }

    private partial class ImportGltfAction(string inputPath) : IAction
    {
        public          MdlFile?   Out;
        public readonly IoNotifier Notifier = new();

        public void Execute(CancellationToken cancel)
        {
            var model = Schema2.ModelRoot.Load(inputPath);

            Out = ModelImporter.Import(model, Notifier);
        }

        public bool Equals(IAction? other)
        {
            if (other is not ImportGltfAction rhs)
                return false;

            return true;
        }
    }
}
