using System.Drawing;
using System.Drawing.Imaging;
using OtterGui.Widgets;
using Penumbra.GameData.Files;
using SharpGLTF.Scenes;
using Xande;
using Xande.Havok;
using static Penumbra.GameData.Data.GamePaths.Monster;
using PMtrlFile = Penumbra.GameData.Files.MtrlFile;

namespace Penumbra.Services.Xande;
public class Helpers
{
    /// <summary>Builds a skeleton tree from a list of .sklb paths.</summary>
    /// <param name="skeletons">A list of HavokXml instances.</param>
    /// <param name="root">The root bone node.</param>
    /// <returns>A mapping of bone name to node in the scene.</returns>
    public static Dictionary<string, NodeBuilder> GetBoneMap(IEnumerable<HavokXml> skeletons, out NodeBuilder? root)
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

    /// <summary>
    /// Compute the distance between two strings.
    /// </summary>
    public static int ComputeLD(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

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
        for (var i = 0; i <= n; d[i, 0] = i++)
        {
        }

        for (var j = 0; j <= m; d[0, j] = j++)
        {
        }

        // Step 3
        for (var i = 1; i <= n; i++)
        {
            //Step 4
            for (var j = 1; j <= m; j++)
            {
                // Step 5
                var cost = t[j - 1] == s[i - 1] ? 0 : 1;

                // Step 6
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        // Step 7
        return d[n, m];
    }

    public static void CopyNormalBlueChannelToDiffuseAlphaChannel(Bitmap normal, Bitmap diffuse)
    {
        var scaleX = (float)diffuse.Width / normal.Width;
        var scaleY = (float)diffuse.Height / normal.Height;

        for (var x = 0; x < diffuse.Width; x++)
        {
            for (var y = 0; y < diffuse.Height; y++)
            {
                var diffusePixel = diffuse.GetPixel(x, y);
                var normalPixel = normal.GetPixel((int)(x / scaleX), (int)(y / scaleY));

                diffuse.SetPixel(x, y, Color.FromArgb(normalPixel.B, diffusePixel.R, diffusePixel.G, diffusePixel.B));
            }
        }
    }

    public static (Bitmap, Bitmap, Bitmap) ComputeCharacterModelTextures(Lumina.Models.Materials.Material xivMaterial, Bitmap normal, Bitmap initDiffuse)
    {
        var diffuse = (Bitmap)normal.Clone();
        var specular = (Bitmap)normal.Clone();
        var emission = (Bitmap)normal.Clone();

        var colorSetInfo = xivMaterial.File!.ColorSetInfo;

        // copy alpha from normal to original diffuse if it exists
        if (initDiffuse != null)
        {
            CopyNormalBlueChannelToDiffuseAlphaChannel(normal, initDiffuse);
        }

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

    public static Bitmap CalculateOcclusion(Bitmap mask, Bitmap specularMap)
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

        return occlusion;
    }
}
