using System;

namespace CapeEditor;

/// A rectangle on a texture in UV terms: u0..u1 left to right, v0..v1 bottom to top (Unity's way round).
internal readonly struct UvRect
{
    public readonly float U0, V0, U1, V1;

    public UvRect(float u0, float v0, float u1, float v1)
    {
        U0 = u0; V0 = v0; U1 = u1; V1 = v1;
    }

    public float Width => U1 - U0;
    public float Height => V1 - V0;
}

/// Where a picture goes on one family of capes. Every cape in a family shares a mesh, so it shares the UV layout:
/// - Classic: Red, Black, Polkadot and Plix (mesh SM_Cape_Default).
/// - Wizard: the same layout on a wider cape (SM_Wizard_Cape).
/// - Tattered: Webby, DanBay, RossBurger and Sasavot (SM_WebbyCape).
/// Each has an outside panel (the face you see from behind Flippy) and an inside panel (the face against the phone).
/// The rest of the texture is the cape's thin edges, which get the edge colour.
/// Measured from the game's meshes (UV islands against vertex positions and normals; see README). On the Classic and
/// Wizard capes both panels are stored rotated 180°: their u runs right to left and v top to bottom when seen from
/// that panel's own side.
internal sealed class CapeLayout
{
    public readonly string Id, Name;
    /// Width / height of the panel as it hangs on Flippy: the shape of the crop frame.
    public readonly float Aspect;
    public readonly UvRect Outside, Inside;
    /// The picture is stored rotated 180° in both panels.
    public readonly bool Turned;

    CapeLayout(string id, string name, float aspect, UvRect outside, UvRect inside, bool turned)
    {
        Id = id; Name = name; Aspect = aspect; Outside = outside; Inside = inside; Turned = turned;
    }

    public static readonly CapeLayout Classic = new("classic", "Classic", 0.69f,
        new UvRect(0f, 0f, 0.503f, 0.698f), new UvRect(0.503f, 0f, 1f, 0.690f), true);

    public static readonly CapeLayout Wizard = new("wizard", "Wizard", 0.91f,
        new UvRect(0f, 0f, 0.503f, 0.698f), new UvRect(0.503f, 0f, 1f, 0.690f), true);

    public static readonly CapeLayout Tattered = new("tattered", "Tattered", 0.68f,
        new UvRect(0.002f, 0.300f, 0.497f, 0.992f), new UvRect(0.497f, 0.300f, 0.994f, 0.992f), false);

    public static readonly CapeLayout[] All = { Classic, Wizard, Tattered };

    /// The family of a cape renderer's mesh (LOD0, LOD1 and LOD2 all count), or null for a back item we can't paint.
    public static CapeLayout ForMesh(string meshName)
    {
        // the black outline shells (SM_Cape_Default_Outline_LOD0...) have their own material: never paint them
        if (string.IsNullOrEmpty(meshName) || meshName.Contains("Outline")) return null;
        if (meshName.StartsWith("SM_Cape_Default", StringComparison.Ordinal)) return Classic;
        if (meshName.StartsWith("SM_Wizard_Cape", StringComparison.Ordinal)) return Wizard;
        if (meshName.StartsWith("SM_WebbyCape", StringComparison.Ordinal)) return Tattered;
        return null;
    }

    public static CapeLayout ById(string id)
    {
        foreach (var layout in All)
            if (layout.Id == id) return layout;
        return Classic;
    }

    /// The base-game capes this mod can paint, by the text in their skin key, and the names shown in the menu.
    public static readonly (string KeyPart, string Name, CapeLayout Layout)[] KnownCapes =
    {
        ("CapeRed", "Red", Classic),
        ("CapeBlack", "Black", Classic),
        ("Polkadot", "Polkadot", Classic),
        ("Plix", "Plix", Classic),
        ("Wizard", "Wizard", Wizard),
        ("Webby", "Webby", Tattered),
        ("DanBay", "DanBay", Tattered),
        ("RossBurger", "RossBurger", Tattered),
        ("Sasavot", "Sasavot", Tattered),
    };

    /// Which known cape a skin key is, or -1.
    public static int KnownIndex(string skinKey)
    {
        if (string.IsNullOrEmpty(skinKey)) return -1;
        for (int i = 0; i < KnownCapes.Length; i++)
            if (skinKey.IndexOf(KnownCapes[i].KeyPart, StringComparison.OrdinalIgnoreCase) >= 0) return i;
        return -1;
    }
}
