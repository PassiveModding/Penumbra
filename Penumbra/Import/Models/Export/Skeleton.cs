using SharpGLTF.Scenes;

namespace Penumbra.Import.Models.Export;

/// <summary> Representation of a skeleton within XIV. </summary>
public class XivSkeleton(XivSkeleton.Bone[] bones) : IEquatable<XivSkeleton>
{
    public readonly Bone[] Bones = bones;

    public struct Bone : IEquatable<Bone>
    {
        public string Name;
        public int ParentIndex;
        public Transform Transform;
        
        public bool Equals( Bone other )
            => Name == other.Name && ParentIndex == other.ParentIndex && Transform.Equals( other.Transform );
    }

    public struct Transform : IEquatable<Transform>
    {
        public Vector3 Scale;
        public Quaternion Rotation;
        public Vector3 Translation;
        
        public bool Equals( Transform other )
            => Scale.Equals( other.Scale ) && Rotation.Equals( other.Rotation ) && Translation.Equals( other.Translation );
    }
    
    public bool Equals( XivSkeleton? other )
    {
        if (ReferenceEquals( null, other )) return false;
        if (ReferenceEquals( this, other )) return true;
        return Bones.SequenceEqual( other.Bones );
    }
}

/// <summary> Representation of a glTF-compatible skeleton. </summary>
public struct GltfSkeleton
{
    /// <summary> Root node of the skeleton. </summary>
    public NodeBuilder Root;

    /// <summary> Flattened list of skeleton nodes. </summary>
    public List<NodeBuilder> Joints;

    /// <summary> Mapping of bone names to their index within the joints array. </summary>
    public Dictionary<string, int> Names;

    public (NodeBuilder, int) GenerateBone(string name)
    {
        var node = new NodeBuilder(name);
        var index = Joints.Count;
        Names[name] = index;
        Joints.Add(node);
        Root.AddNode(node);
        return (node, index);
    }
}
