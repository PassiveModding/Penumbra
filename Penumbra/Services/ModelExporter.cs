using System;
using System.Drawing;
using System.Drawing.Imaging;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Models.Models;
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

namespace Penumbra.Services;

public class ModelExporter
{
    public readonly HavokConverter Converter;
    public readonly LuminaManager LuminaManager;
    public readonly SklbResolver SklbResolver;
    public readonly PbdFile Pbd;
    private readonly IPluginLog _log;

    public ModelExporter(DalamudServices dalamud)
    {
        Converter = new HavokConverter(dalamud.PluginInterface);
        LuminaManager = new LuminaManager(origPath =>
        {
            return null;
        });
        SklbResolver = new SklbResolver(dalamud.PluginInterface);
        Pbd = LuminaManager.GetPbdFile();
        _log = dalamud.Log;
    }

    /// <summary>Builds a skeleton tree from a list of .sklb paths.</summary>
    /// <param name="skeletons">A list of HavokXml instances.</param>
    /// <param name="root">The root bone node.</param>
    /// <returns>A mapping of bone name to node in the scene.</returns>
    private Dictionary<string, NodeBuilder> GetBoneMap(IEnumerable<HavokXml> skeletons, out NodeBuilder? root)
    {
        Dictionary<string, NodeBuilder> boneMap = new();
        root = null;

        foreach (var xml in skeletons)
        {
            var skeleton = xml.GetMainSkeleton();
            var boneNames = skeleton.BoneNames;
            var refPose = skeleton.ReferencePose;
            var parentIndices = skeleton.ParentIndices;

            for (var j = 0; j < boneNames.Length; j++)
            {
                var name = boneNames[j];
                if (boneMap.ContainsKey(name)) continue;

                var bone = new NodeBuilder(name);
                bone.SetLocalTransform(XmlUtils.CreateAffineTransform(refPose[j]), false);

                var boneRootId = parentIndices[j];
                if (boneRootId != -1)
                {
                    var parent = boneMap[boneNames[boneRootId]];
                    parent.AddNode(bone);
                }
                else { root = bone; }

                boneMap[name] = bone;
            }
        }

        return boneMap;
    }

    private static SemaphoreSlim _semaphore = new(1, 1);

    public async Task ExportModel(string exportPath, IEnumerable<HavokXml> skeletons, List<ResourceNode> nodes, CancellationToken cancellationToken = default)
    {
        _log.Debug($"Exporting model to {exportPath}");

        if (_semaphore.CurrentCount == 0)
        {
            _log.Warning("Export already in progress");
            return;
        }

        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            // chara/human/cXXXX/.../..._XXXXX.sklb
            var raceSkeletonRegex = new Regex(@"^chara/human/c(\d+)/");
            var race = nodes.Select(x => x.GamePath.ToString())
                .Where(x => raceSkeletonRegex.IsMatch(x))
                .Select(x => raceSkeletonRegex.Match(x).Groups[1].Value)
                .FirstOrDefault();

            ushort? deform = null;
            if (race != null)
            {
                deform = ushort.Parse(race);
                _log.Debug($"Deform: {deform}");
            }

            var boneMap = GetBoneMap(skeletons.ToArray(), out var root);
            var joints = boneMap.Values.ToArray();
            var raceDeformer = new RaceDeformer(Pbd, boneMap);
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
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Compute the distance between two strings.
    /// </summary>
    public static int ComputeLD(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        // Step 1
        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        // Step 2
        for (int i = 0; i <= n; d[i, 0] = i++)
        {
        }

        for (int j = 0; j <= m; d[0, j] = j++)
        {
        }

        // Step 3
        for (int i = 1; i <= n; i++)
        {
            //Step 4
            for (int j = 1; j <= m; j++)
            {
                // Step 5
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                // Step 6
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        // Step 7
        return d[n, m];
    }

    private async Task HandleModel(ResourceNode node, RaceDeformer raceDeformer, ushort? deform, string exportPath, Dictionary<string, NodeBuilder> boneMap, NodeBuilder[] joints, SceneBuilder glTFScene, CancellationToken cancellationToken)
    {
        var path = node.FullPath.ToPath();
        var file = LuminaManager.GetFile<FileResource>(path);
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
                   ? LuminaManager.GameData.GetFileFromDisk<MtrlFile>(child.FullPath.ToPath(), child.GamePath.ToString())
                   : LuminaManager.GameData.GetFile<MtrlFile>(child.FullPath.ToPath());
                material = new Material(mtrlFile);
            }
            catch (Exception e)
            {
                _log.Error(e, $"Failed to load material {child.FullPath}");
                return;
            }

            // unfortunately processing multiple materials at once can corrupt game memory >:(
            await _semaphoreSlim.WaitAsync(cancellationToken);
            try
            {
                var glTFMaterial = await ComposeTextures(mtrlFile, material, exportPath, child?.Children, cancellationToken);
                       
                if (glTFMaterial == null)
                {
                    return;
                }

                materials.Add((child.FullPath.ToPath(), child.GamePath.ToString(), glTFMaterial));
            }
            catch (Exception e)
            {
                _log.Error(e, $"Failed to compose textures for material {child.FullPath.ToPath()}");
                return;
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        foreach (var mesh in meshes)
        {
            mesh.Material.Update(LuminaManager.GameData);
        }

        _log.Debug($"Handling model {name} with {meshes.Length} meshes\n{string.Join("\n", meshes.Select(x => x.Material.ResolvedPath))}\nUsing materials\n{string.Join("\n", materials.Select(x =>
        {
            if (x.fullpath == x.gamepath)
            {
                return x.fullpath;
            }

            return $"{x.gamepath} -> {x.fullpath}";
        }))}");

        foreach (var mesh in meshes)
        { 
            // try get material from materials
            var material = materials.FirstOrDefault(x => x.fullpath == mesh.Material.ResolvedPath || x.gamepath == mesh.Material.ResolvedPath);

            if (material == default)
            {
                // match most similar material from list
                var match = materials.Select(x => (x.fullpath, x.gamepath, ComputeLD(x.fullpath, mesh.Material.ResolvedPath))).OrderBy(x => x.Item3).FirstOrDefault();
                var match2 = materials.Select(x => (x.fullpath, x.gamepath, ComputeLD(x.gamepath, mesh.Material.ResolvedPath))).OrderBy(x => x.Item3).FirstOrDefault();

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

        if (TryLoadRacialModel(node.GamePath.ToString(), deform, out string newPath, out model))
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
            model = LuminaManager.GetModel(path);
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
            model = LuminaManager.GetModel(newPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (Bitmap, Bitmap, Bitmap) CalculateCharacterShaderPack(Material xivMaterial, Bitmap normal)
    {
        var diffuse = (Bitmap)normal.Clone();
        var specular = (Bitmap)normal.Clone();
        var emission = (Bitmap)normal.Clone();

        var colorSetInfo = xivMaterial.File!.ColorSetInfo;

        for (var x = 0; x < normal.Width; x++)
        {
            for (var y = 0; y < normal.Height; y++)
            {
                var normalPixel = normal.GetPixel(x, y);

                var colorSetIndex1 = normalPixel.A / 17 * 16;
                var colorSetBlend = normalPixel.A % 17 / 17.0;
                var colorSetIndexT2 = normalPixel.A / 17;
                var colorSetIndex2 = (colorSetIndexT2 >= 15 ? 15 : colorSetIndexT2 + 1) * 16;

                // to fix transparency issues 
                // normal.SetPixel(x, y, Color.FromArgb(255, normalPixel.R, normalPixel.G, 255));
                normal.SetPixel(x, y, Color.FromArgb(normalPixel.B, normalPixel.R, normalPixel.G, 255));

                var diffuseBlendColour = ColorUtility.BlendColorSet(in colorSetInfo, colorSetIndex1, colorSetIndex2, normalPixel.B, colorSetBlend, ColorUtility.TextureType.Diffuse);
                var specularBlendColour = ColorUtility.BlendColorSet(in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend, ColorUtility.TextureType.Specular);
                var emissionBlendColour = ColorUtility.BlendColorSet(in colorSetInfo, colorSetIndex1, colorSetIndex2, 255, colorSetBlend, ColorUtility.TextureType.Emissive);

                // Set the blended colors in the respective bitmaps
                diffuse.SetPixel(x, y, diffuseBlendColour);
                specular.SetPixel(x, y, specularBlendColour);
                emission.SetPixel(x, y, emissionBlendColour);
            }
        }

        return (diffuse, specular, emission);
    }

    private (Bitmap, Bitmap) CalculateSpecularOcclusion(Bitmap mask, Bitmap specularMap)
    {
        var occlusion = (Bitmap)mask.Clone();

        for (var x = 0; x < mask.Width; x++)
        {
            for (var y = 0; y < mask.Height; y++)
            {
                var maskPixel = mask.GetPixel(x, y);
                var specularPixel = specularMap.GetPixel(x, y);

                // Calculate the new RGB channels for the specular pixel based on the mask pixel
                specularMap.SetPixel(x, y, Color.FromArgb(
                    specularPixel.A,
                    Convert.ToInt32(specularPixel.R * Math.Pow(maskPixel.G / 255.0, 2)),
                    Convert.ToInt32(specularPixel.G * Math.Pow(maskPixel.G / 255.0, 2)),
                    Convert.ToInt32(specularPixel.B * Math.Pow(maskPixel.G / 255.0, 2))
                ));

                var occlusionPixel = occlusion.GetPixel(x, y);

                // Set the R channel of occlusion pixel to 255 and keep the G and B channels the same
                occlusion.SetPixel(x, y, Color.FromArgb(
                    255,
                    occlusionPixel.R,
                    occlusionPixel.R,
                    occlusionPixel.R
                ));
            }
        }

        var result = (specularMap, occlusion);
        return result;
    }

    public unsafe Bitmap GetTextureBuffer(string path, string? gamePath = null)
    {
        var actualPath = LuminaManager.FileResolver?.Invoke(path) ?? path;
        var texFile = Path.IsPathRooted(actualPath)
            ? LuminaManager.GameData.GetFileFromDisk<TexFile>(actualPath, gamePath)
            : LuminaManager.GameData.GetFile<TexFile>(actualPath);
        if (texFile == null) throw new Exception($"Lumina was unable to fetch a .tex file from {path}.");
        var texBuffer = texFile.TextureBuffer.Filter(format: TexFile.TextureFormat.B8G8R8A8);
        fixed (byte* raw = texBuffer.RawData) { return new Bitmap(texBuffer.Width, texBuffer.Height, texBuffer.Width * 4, PixelFormat.Format32bppArgb, (nint)raw); }
    }

    private void ColorsetShit(MtrlFile mtrl)
    {
        // create penumbra MtrlFile from lumina MtrlFile
        var penumbraMtrl = new PMtrlFile(mtrl.Data);

        // get colorset info
        if (penumbraMtrl.HasTable && penumbraMtrl.HasDyeTable)
        {
            _log.Debug($"MtrlFile has dye table: {mtrl.FilePath.Path}");
            // do something with dye table on the texture

        }
        else if (penumbraMtrl.HasTable)
        {
            _log.Debug($"MtrlFile has table: {mtrl.FilePath.Path}");
            // set default colorset
        }
    }

    // Compose the textures for the glTF material using the xivMaterial information
    private static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1);
    private async Task<MaterialBuilder?> ComposeTextures(MtrlFile mtrlFile, Material xivMaterial, string outputDir, IEnumerable<ResourceNode>? nodes, CancellationToken cancellationToken)
    {
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

            // check outputdir if texture already exists
            var textureFileName = GetTextureFileName(xivTexture.TextureUsageRaw);
            var texturePath2 = Path.Combine(outputDir, $"{mtrlFile.FilePath.Path.Replace("\\", "/").Split("/").Last()}_{textureFileName}.png");


            var textureBuffer = GetTextureBuffer(texturePath, xivTexture.TexturePath);

            xivTextureMap.Add(xivTexture.TextureUsageRaw, textureBuffer);
        }

        // reference for this fuckery
        // https://docs.google.com/spreadsheets/u/0/d/1kIKvVsW3fOnVeTi9iZlBDqJo6GWVn6K6BCUIRldEjhw/htmlview#

        // genuinely not sure when to set to blend, but I think its needed for opacity on some stuff
        SharpGLTF.Materials.AlphaMode alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
        switch (xivMaterial.ShaderPack)
        {
            case "character.shpk":
                {
                    alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    // for character gear, split the normal map into diffuse, specular and emission
                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerNormal, out var normal))
                    {
                        var (diffuse, specular, emission) = CalculateCharacterShaderPack(xivMaterial, normal);
                        // Add the shader pack textures to xivTextureMap
                        // Use TryAdd to avoid overwriting existing textures (if any)
                        xivTextureMap.TryAdd(TextureUsage.SamplerDiffuse, diffuse);
                        xivTextureMap.TryAdd(TextureUsage.SamplerSpecular, specular);
                        xivTextureMap.TryAdd(TextureUsage.SamplerReflection, emission);
                    }

                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerMask, out var mask) && xivTextureMap.TryGetValue(TextureUsage.SamplerSpecular, out var specularMap))
                    {
                        var (specular, occlusion) = CalculateSpecularOcclusion(mask, specularMap);

                        // Add the specular occlusion texture to xivTextureMap
                        xivTextureMap.Add(TextureUsage.SamplerWaveMap, occlusion);
                    }
                    break;
                }
            case "skin.shpk":
                {
                    alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    // skin should use normalmap as alphachannel for Principled BSDF
                    // currently AlphaMode.Mask means we pull it from vertexvolor

                    if (xivTextureMap.TryGetValue(TextureUsage.SamplerNormal, out var normal))
                    {
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
                    break;
                }
            case "iris.shpk":
                {
                    alphaMode = SharpGLTF.Materials.AlphaMode.MASK;
                    break;
                }
            default:
                _log.Debug($"Unhandled shader pack {xivMaterial.ShaderPack}");
                break;
        }

        _log.Debug($"Shader pack: {mtrlFile.FilePath.Path}\n{string.Join("\n", xivTextureMap.Select(x => x.Key.ToString()))}");

        var glTFMaterial = new MaterialBuilder
        {
            Name = mtrlFile.FilePath.Path,
            AlphaMode = alphaMode,
            DoubleSided = true
        };

        ExportTextures(glTFMaterial, xivTextureMap, outputDir);

        return glTFMaterial;
    }

    private void ExportTexture(MaterialBuilder glTFMaterial, TextureUsage textureUsage, Bitmap bitmap, string outputDir)
    {
        var name = glTFMaterial.Name.Replace("\\", "/").Split("/").Last().Split(".").First();
        string texturePath = $"{name}_{GetTextureFileName(textureUsage)}.png";
        var path = Path.Combine(outputDir, texturePath);

        // Save the texture to the output directory and update the glTF material with respective image paths
        switch (textureUsage)
        {
            case TextureUsage.SamplerColorMap0:
            case TextureUsage.SamplerDiffuse:
                bitmap.Save(path);
                glTFMaterial.WithBaseColor(path);
                break;
            case TextureUsage.SamplerNormalMap0:
            case TextureUsage.SamplerNormal:
                bitmap.Save(path);
                glTFMaterial.WithNormal(path, 1);
                break;
            case TextureUsage.SamplerSpecularMap0:
            case TextureUsage.SamplerSpecular:
                bitmap.Save(path);
                glTFMaterial.WithSpecularColor(path);
                break;
            case TextureUsage.SamplerWaveMap:
                bitmap.Save(path);
                glTFMaterial.WithOcclusion(path);
                break;
            case TextureUsage.SamplerReflection:
                bitmap.Save(path);
                glTFMaterial.WithEmissive(path, new Vector3(255, 255, 255));
                break;
            case TextureUsage.SamplerMask:
                // Do something with this texture
                bitmap.Save(path);
                break;
            default:
                _log.Warning("Unhandled TextureUsage: " + textureUsage);
                bitmap.Save(path);
                break;
        }
    }

    private void ExportTextures(MaterialBuilder glTFMaterial, Dictionary<TextureUsage, Bitmap> xivTextureMap, string outputDir)
    {
        var num = 0;
        foreach (var xivTexture in xivTextureMap)
        {
            ExportTexture(glTFMaterial, xivTexture.Key, xivTexture.Value, outputDir);
            num++;
        }

        // Set the metallic roughness factor to 0
        glTFMaterial.WithMetallicRoughness(0);
    }


    private string? GetTextureFileName(TextureUsage textureUsage)
    {
        switch (textureUsage)
        {
            case TextureUsage.SamplerColorMap0:
            case TextureUsage.SamplerDiffuse:
                return "diffuse";
            case TextureUsage.SamplerNormalMap0:
            case TextureUsage.SamplerNormal:
                return "normal";
            case TextureUsage.SamplerSpecularMap0:
            case TextureUsage.SamplerSpecular:
                return "specular";
            case TextureUsage.SamplerWaveMap:
                return "occlusion";
            case TextureUsage.SamplerReflection:
                return "emissive";
            case TextureUsage.SamplerMask:
                return "mask";
            case TextureUsage.Sampler:
            case TextureUsage.Sampler0:
            case TextureUsage.Sampler1:
            case TextureUsage.SamplerCatchlight:
            case TextureUsage.SamplerColorMap1:
            case TextureUsage.SamplerEnvMap:
            case TextureUsage.SamplerNormalMap1:
            case TextureUsage.SamplerSpecularMap1:
            case TextureUsage.SamplerWaveletMap0:
            case TextureUsage.SamplerWaveletMap1:
            case TextureUsage.SamplerWhitecapMap:
            default:
                return textureUsage.ToString();
        }
    }
}
