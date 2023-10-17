using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Models.Models;
using Penumbra.GameData.Enums;
using Penumbra.Interop.ResourceTree;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using Xande;
using Xande.Files;
using Xande.Havok;
using Xande.Models;
using Xande.Models.Export;
using Material = Lumina.Models.Materials.Material;
using Mesh = Lumina.Models.Models.Mesh;
using PMtrlFile = Penumbra.GameData.Files.MtrlFile;

namespace Penumbra.Services.Xande;

public class ModelExporter
{
    private readonly HavokConverter _converter;
    private readonly LuminaManager _luminaManager;
    private readonly SklbResolver _sklbResolver;
    private readonly PbdFile _pbd;
    private readonly IPluginLog _log;
    private readonly DalamudServices _dalamud;
    private readonly StainService _stainService;

    // only allow one export at a time due to memory issues
    private static readonly SemaphoreSlim ExportSemaphore = new(1, 1);

    // only allow one texture export at a time due to memory issues
    private static readonly SemaphoreSlim TextureSemaphore = new(1, 1);

    public ModelExporter(DalamudServices dalamud, StainService stainService)
    {
        _converter = new HavokConverter(dalamud.PluginInterface);
        _luminaManager = new LuminaManager(origPath =>
        {
            return null;
        });
        _sklbResolver = new SklbResolver(dalamud.PluginInterface);
        _pbd = _luminaManager.GetPbdFile();
        _log = dalamud.Log;
        _dalamud = dalamud;
        _stainService = stainService;
    }

    public Task ExportResourceTree(ResourceTree tree, bool[] enabledNodes, CancellationToken cancellationToken)
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

        return _dalamud.Framework.RunOnTick(() =>
        {
            List<ResourceNode> nodes = new();
            for (int i = 0; i < enabledNodes.Length; i++)
            {
                if (enabledNodes[i] == false) continue;
                var node = tree.Nodes[i];
                nodes.Add(node);
            }

            _log.Debug($"Exporting character to {path}");
            // skeletons should only be at the root level so no need to go further
            // do not exclude skeletons regardless of option (because its annoying)
            var skeletonNodes = tree.Nodes.Where(x => x.Type == Api.Enums.ResourceType.Sklb).ToList();
            // if skeleton is for weapon, move it to the end
            skeletonNodes.Sort((x, y) =>
            {
                if (x.GamePath.ToString().Contains("weapon"))
                {
                    return 1;
                }

                if (y.GamePath.ToString().Contains("weapon"))
                {
                    return -1;
                }

                return 0;
            });

            var skeletons = new List<HavokXml>();
            try
            {

                foreach (var node in skeletonNodes)
                {
                    // cannot use fullpath because things like ivcs are fucky and crash the game
                    var nodePath = node.FullPath.ToPath();
                    try
                    {
                        var file = _luminaManager.GetFile<FileResource>(nodePath);
                        var sklb = SklbFile.FromStream(file.Reader.BaseStream);

                        var xml = _converter.HkxToXml(sklb.HkxData);
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
                        var file = _luminaManager.GetFile<FileResource>(nodePath);
                        var sklb = SklbFile.FromStream(file.Reader.BaseStream);

                        var xml = _converter.HkxToXml(sklb.HkxData);
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
                return Task.CompletedTask;
            }


            return Task.Run(async () =>
            {
                try
                {
                    await ExportModel(path, skeletons, tree, nodes, cancellationToken);
                    // open path 
                    Process.Start("explorer.exe", path);
                }
                catch (Exception e)
                {
                    _log.Error(e, "Error while exporting character");
                }

            }, cancellationToken);
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

    private async Task ExportModel(string exportPath, IEnumerable<HavokXml> skeletons, ResourceTree tree, IEnumerable<ResourceNode> nodes, CancellationToken cancellationToken = default)
    {
        _log.Debug($"Exporting model to {exportPath}");

        if (ExportSemaphore.CurrentCount == 0)
        {
            _log.Warning("Export already in progress");
            return;
        }

        await ExportSemaphore.WaitAsync(cancellationToken);

        try
        {
            ushort deform = (ushort)tree.RaceCode;
            var boneMap = Helpers.GetBoneMap(skeletons.ToArray(), out var root);
            var joints = boneMap.Values.ToArray();
            var raceDeformer = new RaceDeformer(_pbd, boneMap);
            var modelNodes = nodes.Where(x => x.Type == Api.Enums.ResourceType.Mdl).ToArray();
            var glTFScene = new SceneBuilder(modelNodes.Length > 0 ? modelNodes[0].GamePath.ToString() : "scene");
            if (root != null)
            {
                glTFScene.AddNode(root);
            }

            var modelTasks = new List<Task>();
            foreach (var node in modelNodes)
            {
                modelTasks.Add(HandleModel(node, raceDeformer, deform, exportPath, boneMap, joints, glTFScene, cancellationToken));
            }

            await Task.WhenAll(modelTasks);

            var glTFModel = glTFScene.ToGltf2();
            //var waveFrontFolder = Path.Combine(exportPath, "wavefront");
            //Directory.CreateDirectory(waveFrontFolder);
            //glTFModel.SaveAsWavefront(Path.Combine(waveFrontFolder, "wavefront.obj"));

            var glTFFolder = Path.Combine(exportPath, "gltf");
            Directory.CreateDirectory(glTFFolder);
            glTFModel.SaveGLTF(Path.Combine(glTFFolder, "gltf.gltf"));

            //var glbFolder = Path.Combine(exportPath, "glb");
            //Directory.CreateDirectory(glbFolder);
            //glTFModel.SaveGLB(Path.Combine(glbFolder, "glb.glb"));

            _log.Debug($"Exported model to {exportPath}");
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export model");
        }
        finally
        {
            ExportSemaphore.Release();
        }
    }

    private async Task HandleModel(ResourceNode node, RaceDeformer raceDeformer, ushort? deform, string exportPath, Dictionary<string, NodeBuilder> boneMap, NodeBuilder[] joints, SceneBuilder glTFScene, CancellationToken cancellationToken)
    {
        var path = node.FullPath.ToPath();
        var file = _luminaManager.GetFile<FileResource>(path);
        if (!TryGetModel(node, deform, out var modelPath, out var model))
        {
            return;
        }

        if (string.Equals(path, modelPath, StringComparison.InvariantCultureIgnoreCase))
        {
            _log.Debug($"Using full path for {path}");
        }
        else
        {
            _log.Debug($"Retrieved model\n" +
                $"Used path: {modelPath}\n" +
                $"Init path: {path}");
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var raceCode = raceDeformer.RaceCodeFromPath(path);
        var meshes = model.Meshes.Where(x => x.Types.Contains(Mesh.MeshType.Main)).ToArray();
        var nodeChildren = node.Children.ToList();

        var materials = new List<(string fullpath, string gamepath, MaterialBuilder material)>();
        var shaderLogs = new List<string>();
        foreach (var child in nodeChildren)
        {
            if (child == null)
            {
                continue;
            }

            if (child.Type != Api.Enums.ResourceType.Mtrl)
            {
                continue;
            }

            Material? material = null;
            MtrlFile? mtrlFile = null;
            try
            {
                mtrlFile = Path.IsPathRooted(child.FullPath.ToPath())
                   ? _luminaManager.GameData.GetFileFromDisk<MtrlFile>(child.FullPath.ToPath(), child.GamePath.ToString())
                   : _luminaManager.GameData.GetFile<MtrlFile>(child.FullPath.ToPath());
                material = new Material(mtrlFile);
            }
            catch (Exception e)
            {
                _log.Error(e, $"Failed to load material {child.FullPath}");
                return;
            }

            // unfortunately processing multiple materials at once can corrupt game memory >:(
            await TextureSemaphore.WaitAsync(cancellationToken);
            try
            {
                var glTFMaterial = ComposeTextures(mtrlFile, material, exportPath, child?.Children, cancellationToken, out var shaderLog);

                if (glTFMaterial == null)
                {
                    return;
                }

                shaderLogs.Add(shaderLog);
                materials.Add((child.FullPath.ToPath(), child.GamePath.ToString(), glTFMaterial));
            }
            catch (Exception e)
            {
                _log.Error(e, $"Failed to compose textures for material {child.FullPath.ToPath()}");
                return;
            }
            finally
            {
                TextureSemaphore.Release();
            }
        }

        foreach (var mesh in meshes)
        {
            mesh.Material.Update(_luminaManager.GameData);
        }

        _log.Debug($"Handling model {name} with {meshes.Length} meshes\n{string.Join("\n", meshes.Select(x => x.Material.ResolvedPath))}\nUsing materials\n{string.Join("\n", materials.Select(x =>
        {
            if (x.fullpath == x.gamepath)
            {
                return x.fullpath;
            }

            return $"{x.gamepath} -> {x.fullpath}";
        }))}\n{string.Join("\n", shaderLogs)}");

        foreach (var mesh in meshes)
        {
            // try get material from materials
            var material = materials.FirstOrDefault(x => x.fullpath == mesh.Material.ResolvedPath || x.gamepath == mesh.Material.ResolvedPath);

            if (material == default)
            {
                // match most similar material from list
                var match = materials.Select(x => (x.fullpath, x.gamepath, Helpers.ComputeLD(x.fullpath, mesh.Material.ResolvedPath))).OrderBy(x => x.Item3).FirstOrDefault();
                var match2 = materials.Select(x => (x.fullpath, x.gamepath, Helpers.ComputeLD(x.gamepath, mesh.Material.ResolvedPath))).OrderBy(x => x.Item3).FirstOrDefault();

                if (match.Item3 < match2.Item3)
                {
                    material = materials.FirstOrDefault(x => x.fullpath == match.fullpath || x.gamepath == match.gamepath);
                }
                else
                {
                    material = materials.FirstOrDefault(x => x.fullpath == match2.fullpath || x.gamepath == match2.gamepath);
                }
            }

            if (material == default)
            {
                _log.Warning($"Could not find material for {mesh.Material.ResolvedPath}");
                continue;
            }

            try
            {
                if (mesh.Material.ResolvedPath != material.gamepath)
                {
                    _log.Warning($"Using material {material.gamepath} for {mesh.Material.ResolvedPath}");
                }

                await HandleMeshCreation(material.material, raceDeformer, glTFScene, mesh, model, raceCode, deform, boneMap, name, joints);
            }
            catch (Exception e)
            {
                _log.Error(e, $"Failed to handle mesh creation for {mesh.Material.ResolvedPath}");
                continue;
            }
        }
    }

    private Task HandleMeshCreation(MaterialBuilder glTFMaterial,
    RaceDeformer raceDeformer,
    SceneBuilder glTFScene,
    Mesh xivMesh,
    Model xivModel,
    ushort? raceCode,
    ushort? deform,
    Dictionary<string, NodeBuilder> boneMap,
    string name,
    NodeBuilder[] joints)
    {
        var boneSet = xivMesh.BoneTable();
        var boneSetJoints = boneSet?.Select(n => boneMap[n]).ToArray();
        var useSkinning = boneSet != null;

        // Mapping between ID referenced in the mesh and in Havok
        Dictionary<int, int> jointIDMapping = new();
        for (var i = 0; i < boneSetJoints?.Length; i++)
        {
            var joint = boneSetJoints[i];
            var idx = joints.ToList().IndexOf(joint);
            jointIDMapping[i] = idx;
        }

        // Handle submeshes and the main mesh
        var meshBuilder = new MeshBuilder(
            xivMesh,
            useSkinning,
            jointIDMapping,
            glTFMaterial,
            raceDeformer
        );

        // Deform for full bodies
        if (raceCode != null && deform != null) { meshBuilder.SetupDeformSteps(raceCode.Value, deform.Value); }

        meshBuilder.BuildVertices();

        if (xivMesh.Submeshes.Length > 0)
        {
            for (var i = 0; i < xivMesh.Submeshes.Length; i++)
            {
                try
                {
                    var xivSubmesh = xivMesh.Submeshes[i];
                    var subMesh = meshBuilder.BuildSubmesh(xivSubmesh);
                    subMesh.Name = $"{name}_{xivMesh.MeshIndex}.{i}";
                    meshBuilder.BuildShapes(xivModel.Shapes.Values.ToArray(), subMesh, (int)xivSubmesh.IndexOffset,
                        (int)(xivSubmesh.IndexOffset + xivSubmesh.IndexNum));
                    if (useSkinning) { glTFScene.AddSkinnedMesh(subMesh, Matrix4x4.Identity, joints); } else { glTFScene.AddRigidMesh(subMesh, Matrix4x4.Identity); }
                }
                catch (Exception e)
                {
                    _log.Error(e, $"Failed to build submesh {i} for {name}");
                }
            }
        }
        else
        {
            var mesh = meshBuilder.BuildMesh();
            mesh.Name = $"{name}_{xivMesh.MeshIndex}";
            _log.Debug($"Building mesh: \"{mesh.Name}\"");
            meshBuilder.BuildShapes(xivModel.Shapes.Values.ToArray(), mesh, 0, xivMesh.Indices.Length);
            if (useSkinning) { glTFScene.AddSkinnedMesh(mesh, Matrix4x4.Identity, joints); } else { glTFScene.AddRigidMesh(mesh, Matrix4x4.Identity); }
        }

        return Task.CompletedTask;
    }

    private bool TryGetModel(ResourceNode node, ushort? deform, out string path, out Model? model)
    {
        path = node.FullPath.ToPath();
        if (TryLoadModel(node.FullPath.ToPath(), out model))
        {
            return true;
        }

        if (TryLoadModel(node.GamePath.ToString(), out model))
        {
            return true;
        }

        if (TryLoadRacialModel(node.GamePath.ToString(), deform, out var newPath, out model))
        {
            return true;
        }


        _log.Warning($"Could not load model\n{node.FullPath}\n{node.GamePath}\n{newPath}");

        return false;
    }

    private bool TryLoadModel(string path, out Model? model)
    {
        model = null;
        try
        {
            model = _luminaManager.GetModel(path);
            return true;
        }
        catch (Exception e)
        {
            _log.Warning(e, $"Failed to load model {path}");
            return false;
        }
    }

    private bool TryLoadRacialModel(string path, ushort? deform, out string newPath, out Model? model)
    {
        newPath = path;
        model = null;
        if (deform == null)
        {
            return false;
        }

        newPath = Regex.Replace(path.ToString(), @"c\d+", $"c{deform}");
        try
        {
            model = _luminaManager.GetModel(newPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public unsafe Bitmap GetTextureBuffer(string path, string? gamePath = null)
    {
        var actualPath = _luminaManager.FileResolver?.Invoke(path) ?? path;
        var texFile = Path.IsPathRooted(actualPath)
            ? _luminaManager.GameData.GetFileFromDisk<TexFile>(actualPath, gamePath)
            : _luminaManager.GameData.GetFile<TexFile>(actualPath);
        if (texFile == null) throw new Exception($"Lumina was unable to fetch a .tex file from {path}.");
        var texBuffer = texFile.TextureBuffer.Filter(format: TexFile.TextureFormat.B8G8R8A8);
        fixed (byte* raw = texBuffer.RawData) { return new Bitmap(texBuffer.Width, texBuffer.Height, texBuffer.Width * 4, PixelFormat.Format32bppArgb, (nint)raw); }
    }

    private MaterialBuilder? ComposeTextures(MtrlFile mtrlFile, Material xivMaterial, string outputDir, IEnumerable<ResourceNode>? nodes, CancellationToken cancellationToken, out string? log)
    {
        log = "";
        var xivTextureMap = new Dictionary<TextureUsage, Bitmap>();

        foreach (var xivTexture in xivMaterial.Textures)
        {
            // Check for cancellation request
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (xivTexture.TexturePath == "dummy.tex") { continue; }

            var texturePath = xivTexture.TexturePath;
            // try find matching node for tex file
            if (nodes != null)
            {
                var nodeMatch = nodes.FirstOrDefault(x => x.GamePath.ToString() == texturePath);
                if (nodeMatch != null)
                {
                    texturePath = nodeMatch.FullPath.ToPath();
                }
                else
                {
                    var fileName = Path.GetFileNameWithoutExtension(texturePath);
                    // try get using contains
                    nodeMatch = nodes.FirstOrDefault(x => x.GamePath.ToString().Contains(fileName));

                    if (nodeMatch != null)
                    {
                        texturePath = nodeMatch.FullPath.ToPath();
                    }
                }
            }

            var textureBuffer = GetTextureBuffer(texturePath, xivTexture.TexturePath);
            xivTextureMap.Add(xivTexture.TextureUsageRaw, textureBuffer);
        }

        // reference for this fuckery
        // https://docs.google.com/spreadsheets/u/0/d/1kIKvVsW3fOnVeTi9iZlBDqJo6GWVn6K6BCUIRldEjhw/htmlview#

        var initTextureMap = xivTextureMap.Select(x => x.Key).ToList();

        // genuinely not sure when to set to blend, but I think its needed for opacity on some stuff
        var alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
        var backfaceCulling = true;
        switch (xivMaterial.ShaderPack)
        {
            case "character.shpk":
                {
                    //alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    // for character gear, split the normal map into diffuse, specular and emission
                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerNormal, out var normal))
                    {
                        xivTextureMap.TryGetValue(TextureUsage.SamplerDiffuse, out var initDifuse);
                        if (!xivTextureMap.ContainsKey(TextureUsage.SamplerDiffuse) || !xivTextureMap.ContainsKey(TextureUsage.SamplerSpecular) || !xivTextureMap.ContainsKey(TextureUsage.SamplerReflection))
                        {
                            var (diffuse, specular, emission) = Helpers.ComputeCharacterModelTextures(xivMaterial, normal, initDifuse);

                            // If the textures already exist, tryadd will make sure they are not overwritten
                            xivTextureMap.TryAdd(TextureUsage.SamplerDiffuse, diffuse);
                            xivTextureMap.TryAdd(TextureUsage.SamplerSpecular, specular);
                            xivTextureMap.TryAdd(TextureUsage.SamplerReflection, emission);
                        }
                    }

                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerMask, out var mask) && xivTextureMap.TryGetValue(TextureUsage.SamplerSpecular, out var specularMap))
                    {
                        var occlusion = Helpers.CalculateOcclusion(mask, specularMap);

                        // Add the specular occlusion texture to xivTextureMap
                        xivTextureMap.Add(TextureUsage.SamplerWaveMap, occlusion);
                    }
                    break;
                }
            case "skin.shpk":
                {
                    alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerNormal, out var normal))
                    {
                        xivTextureMap.TryGetValue(TextureUsage.SamplerDiffuse, out var diffuse);

                        if (diffuse == null) throw new Exception("Diffuse texture is null");

                        // use blue for opacity
                        Helpers.CopyNormalBlueChannelToDiffuseAlphaChannel(normal, diffuse);

                        // use blue for opacity
                        for (var x = 0; x < normal.Width; x++)
                        {
                            for (var y = 0; y < normal.Height; y++)
                            {
                                var normalPixel = normal.GetPixel(x, y);
                                normal.SetPixel(x, y, Color.FromArgb(normalPixel.B, normalPixel.R, normalPixel.G, 255));
                            }
                        }
                    }
                    break;
                }
            case "hair.shpk":
                {
                    alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    backfaceCulling = false;
                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerNormal, out var normal))
                    {
                        var specular = new Bitmap(normal.Width, normal.Height, PixelFormat.Format32bppArgb);
                        var colorSetInfo = xivMaterial.File!.ColorSetInfo;

                        for (int x = 0; x < normal.Width; x++)
                        {
                            for (int y = 0; y < normal.Height; y++)
                            {
                                var normalPixel = normal.GetPixel(x, y);
                                var colorSetIndex1 = normalPixel.A / 17 * 16;
                                var colorSetBlend = normalPixel.A % 17 / 17.0;
                                var colorSetIndexT2 = normalPixel.A / 17;
                                var colorSetIndex2 = (colorSetIndexT2 >= 15 ? 15 : colorSetIndexT2 + 1) * 16;

                                var specularBlendColour = ColorUtility.BlendColorSet(in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend, ColorUtility.TextureType.Specular);

                                specular.SetPixel(x, y, Color.FromArgb(normalPixel.B, specularBlendColour.R, specularBlendColour.G, specularBlendColour.B));
                            }
                        }

                        xivTextureMap.Add(TextureUsage.SamplerSpecular, specular);

                        if (xivTextureMap.TryGetValue(TextureUsage.SamplerMask, out var mask) && xivTextureMap.TryGetValue(TextureUsage.SamplerSpecular, out var specularMap))
                        {
                            var occlusion = Helpers.CalculateOcclusion(mask, specularMap);

                            // Add the specular occlusion texture to xivTextureMap
                            xivTextureMap.Add(TextureUsage.SamplerWaveMap, occlusion);
                        }
                    }
                    break;
                }
            case "iris.shpk":
                {
                    //alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    break;
                }
            default:
                _log.Debug($"Unhandled shader pack {xivMaterial.ShaderPack}");
                break;
        }

        log = $"Shader pack: {mtrlFile.FilePath.Path}\n" +
            $"Type: {xivMaterial.ShaderPack}\n" +
            $"{string.Join("\n", xivTextureMap.Select(x =>
            {
                // if init texturemap does not contain key show (new)
                var isNew = !initTextureMap.Contains(x.Key);
                return $"{x.Key} -> {x.Value.Width}x{x.Value.Height} {(isNew ? "(new)" : "")}";
            }))}";

        var glTFMaterial = new MaterialBuilder
        {
            Name = mtrlFile.FilePath.Path,
            AlphaMode = alphaMode,
            DoubleSided = !backfaceCulling
        };

        ExportTextures(glTFMaterial, xivTextureMap, outputDir);

        return glTFMaterial;
    }


    private void ExportTextures(MaterialBuilder glTFMaterial, Dictionary<TextureUsage, Bitmap> xivTextureMap, string outputDir)
    {
        foreach (var xivTexture in xivTextureMap)
        {
            ExportTexture(glTFMaterial, xivTexture.Key, xivTexture.Value, outputDir);
        }

        // Set the metallic roughness factor to 0
        glTFMaterial.WithMetallicRoughness(0);
    }

    private void ExportTexture(MaterialBuilder glTFMaterial, TextureUsage textureUsage, Bitmap bitmap, string outputDir)
    {
        // tbh can overwrite or delete these after use but theyre helpful for debugging
        var name = glTFMaterial.Name.Replace("\\", "/").Split("/").Last().Split(".").First();
        string path;

        // Save the texture to the output directory and update the glTF material with respective image paths
        switch (textureUsage)
        {
            case TextureUsage.SamplerColorMap0:
            case TextureUsage.SamplerDiffuse:
                path = Path.Combine(outputDir, $"{name}_diffuse.png");
                bitmap.Save(path);
                glTFMaterial.WithBaseColor(path);
                break;
            case TextureUsage.SamplerNormalMap0:
            case TextureUsage.SamplerNormal:
                path = Path.Combine(outputDir, $"{name}_normal.png");
                bitmap.Save(path);
                glTFMaterial.WithNormal(path, 1);
                break;
            case TextureUsage.SamplerSpecularMap0:
            case TextureUsage.SamplerSpecular:
                path = Path.Combine(outputDir, $"{name}_specular.png");
                bitmap.Save(path);
                glTFMaterial.WithSpecularColor(path);
                break;
            case TextureUsage.SamplerWaveMap:
                path = Path.Combine(outputDir, $"{name}_occlusion.png");
                bitmap.Save(path);
                glTFMaterial.WithOcclusion(path);
                break;
            case TextureUsage.SamplerReflection:
                path = Path.Combine(outputDir, $"{name}_emissive.png");
                bitmap.Save(path);
                glTFMaterial.WithEmissive(path, new Vector3(255, 255, 255));
                break;
            case TextureUsage.SamplerMask:
                path = Path.Combine(outputDir, $"{name}_mask.png");
                // Do something with this texture
                bitmap.Save(path);
                break;
            default:
                _log.Warning("Unhandled TextureUsage: " + textureUsage);
                path = Path.Combine(outputDir, $"{name}_{textureUsage}.png");
                bitmap.Save(path);
                break;
        }
    }
}
