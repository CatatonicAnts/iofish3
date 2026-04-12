using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UiMod;

public static class RenderFlags
{
    public const int RT_MODEL = 0;
    public const int RF_LIGHTING_ORIGIN = 0x0080;
    public const int RF_NOSHADOW = 0x0040;
    public const int RDF_NOWORLDMODEL = 0x0001;
}

/// <summary>
/// Matches C refEntity_t exactly (140 bytes).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 140)]
public unsafe struct RefEntity
{
    [FieldOffset(0)]   public int ReType;
    [FieldOffset(4)]   public int RenderFx;
    [FieldOffset(8)]   public int HModel;
    [FieldOffset(12)]  public fixed float LightingOrigin[3];
    [FieldOffset(24)]  public float ShadowPlane;
    [FieldOffset(28)]  public fixed float Axis[9];
    [FieldOffset(64)]  public int NonNormalizedAxes;
    [FieldOffset(68)]  public fixed float Origin[3];
    [FieldOffset(80)]  public int Frame;
    [FieldOffset(84)]  public fixed float OldOrigin[3];
    [FieldOffset(96)]  public int OldFrame;
    [FieldOffset(100)] public float Backlerp;
    [FieldOffset(104)] public int SkinNum;
    [FieldOffset(108)] public int CustomSkin;
    [FieldOffset(112)] public int CustomShader;
    [FieldOffset(116)] public fixed byte ShaderRGBA[4];
    [FieldOffset(120)] public fixed float ShaderTexCoord[2];
    [FieldOffset(128)] public int ShaderTime;
    [FieldOffset(132)] public float Radius;
    [FieldOffset(136)] public float Rotation;
}

/// <summary>
/// Matches C refdef_t exactly (368 bytes).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 368)]
public unsafe struct RefDef
{
    [FieldOffset(0)]   public int X;
    [FieldOffset(4)]   public int Y;
    [FieldOffset(8)]   public int Width;
    [FieldOffset(12)]  public int Height;
    [FieldOffset(16)]  public float FovX;
    [FieldOffset(20)]  public float FovY;
    [FieldOffset(24)]  public fixed float ViewOrg[3];
    [FieldOffset(36)]  public fixed float ViewAxis[9];
    [FieldOffset(72)]  public int Time;
    [FieldOffset(76)]  public int RdFlags;
    [FieldOffset(80)]  public fixed byte AreaMask[32];
    [FieldOffset(112)] public fixed byte Text[256];
}

/// <summary>
/// Matches C orientation_t (48 bytes): origin[3] + axis[3][3].
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public unsafe struct TagOrientation
{
    [FieldOffset(0)]  public fixed float Origin[3];
    [FieldOffset(12)] public fixed float Axis[9];
}

/// <summary>
/// Loads and renders a Q3 player model (legs + torso + head) with tag-based attachment.
/// </summary>
public unsafe class PlayerModel
{
    private int _legsModel, _torsoModel, _headModel;
    private int _legsSkin, _torsoSkin, _headSkin;
    private string _modelName = "";
    private string _skinName = "default";

    public bool IsLoaded => _legsModel != 0 && _torsoModel != 0 && _headModel != 0;

    public void Load(string modelName, string skinName = "default")
    {
        _modelName = modelName;
        _skinName = skinName;

        string bp = $"models/players/{modelName}";
        _legsModel  = Syscalls.R_RegisterModel($"{bp}/lower.md3");
        _torsoModel = Syscalls.R_RegisterModel($"{bp}/upper.md3");
        _headModel  = Syscalls.R_RegisterModel($"{bp}/head.md3");

        _legsSkin  = Syscalls.R_RegisterSkin($"{bp}/lower_{skinName}.skin");
        _torsoSkin = Syscalls.R_RegisterSkin($"{bp}/upper_{skinName}.skin");
        _headSkin  = Syscalls.R_RegisterSkin($"{bp}/head_{skinName}.skin");
    }

    public void SetSkin(string skinName)
    {
        if (_skinName == skinName) return;
        _skinName = skinName;
        string bp = $"models/players/{_modelName}";
        _legsSkin  = Syscalls.R_RegisterSkin($"{bp}/lower_{skinName}.skin");
        _torsoSkin = Syscalls.R_RegisterSkin($"{bp}/upper_{skinName}.skin");
        _headSkin  = Syscalls.R_RegisterSkin($"{bp}/head_{skinName}.skin");
    }

    /// <summary>
    /// Render the 3D model preview in virtual 640x480 coordinates.
    /// </summary>
    public void Draw(float vx, float vy, float vw, float vh, int realtime)
    {
        if (!IsLoaded || vw <= 0 || vh <= 0) return;

        float yaw = (realtime / 20.0f) % 360.0f;

        // Convert virtual coords to pixel coords
        float px = vx * Drawing.XScale;
        float py = vy * Drawing.YScale;
        float pw = vw * Drawing.XScale;
        float ph = vh * Drawing.YScale;

        // Setup refdef
        RefDef rd = default;
        rd.RdFlags = RenderFlags.RDF_NOWORLDMODEL;
        rd.X = (int)px;
        rd.Y = (int)py;
        rd.Width = (int)pw;
        rd.Height = (int)ph;
        rd.Time = realtime;
        SetIdentityAxis(rd.ViewAxis);

        // FOV (matching Q3's UI_DrawPlayer)
        rd.FovX = vw / 640.0f * 90.0f;
        float xx = vw / MathF.Tan(rd.FovX / 360.0f * MathF.PI);
        rd.FovY = MathF.Atan2(vh, xx) * (360.0f / MathF.PI);

        // Player bounds: mins=(-16,-16,-24), maxs=(16,16,32)
        const float MINS_Z = -24f, MAXS_Z = 32f;
        float len = 0.7f * (MAXS_Z - MINS_Z);
        float distance = len / MathF.Tan(rd.FovX * 0.5f * MathF.PI / 180.0f);
        float originZ = -0.5f * (MINS_Z + MAXS_Z);

        Syscalls.R_ClearScene();

        int renderfx = RenderFlags.RF_LIGHTING_ORIGIN | RenderFlags.RF_NOSHADOW;

        // --- Legs ---
        RefEntity legs = default;
        legs.HModel = _legsModel;
        legs.CustomSkin = _legsSkin;
        legs.RenderFx = renderfx;
        SetVec3(legs.Origin, distance, 0, originZ);
        SetVec3(legs.OldOrigin, distance, 0, originZ);
        SetVec3(legs.LightingOrigin, distance, 0, originZ);
        SetYawAxis(legs.Axis, yaw);

        Syscalls.R_AddRefEntityToScene(&legs);

        // --- Torso (on tag_torso) ---
        RefEntity torso = default;
        torso.HModel = _torsoModel;
        torso.CustomSkin = _torsoSkin;
        torso.RenderFx = renderfx;
        SetVec3(torso.LightingOrigin, distance, 0, originZ);
        SetIdentityAxis(torso.Axis);

        PositionOnTag(&torso, &legs, _legsModel, "tag_torso");
        Syscalls.R_AddRefEntityToScene(&torso);

        // --- Head (on tag_head) ---
        RefEntity head = default;
        head.HModel = _headModel;
        head.CustomSkin = _headSkin;
        head.RenderFx = renderfx;
        SetVec3(head.LightingOrigin, distance, 0, originZ);
        SetIdentityAxis(head.Axis);

        PositionOnTag(&head, &torso, _torsoModel, "tag_head");
        Syscalls.R_AddRefEntityToScene(&head);

        // Accent lights (Q3 style: front-left white, front-right red)
        float* lightOrg = stackalloc float[3];
        lightOrg[0] = distance - 100;
        lightOrg[1] = 100;
        lightOrg[2] = originZ + 100;
        Syscalls.R_AddLightToScene(lightOrg, 500, 1.0f, 1.0f, 1.0f);

        lightOrg[0] = distance - 200;
        lightOrg[1] = -100;
        lightOrg[2] = originZ;
        Syscalls.R_AddLightToScene(lightOrg, 500, 1.0f, 0.0f, 0.0f);

        Syscalls.R_RenderScene(&rd);
    }

    private static void PositionOnTag(RefEntity* entity, RefEntity* parent, int parentModel, string tagName)
    {
        TagOrientation tag = default;
        Syscalls.CM_LerpTag(&tag, parentModel, parent->OldFrame, parent->Frame,
            1.0f - parent->Backlerp, tagName);

        // entity->origin = parent->origin + sum(tag.origin[i] * parent->axis[i])
        entity->Origin[0] = parent->Origin[0];
        entity->Origin[1] = parent->Origin[1];
        entity->Origin[2] = parent->Origin[2];

        for (int i = 0; i < 3; i++)
        {
            entity->Origin[0] += tag.Origin[i] * parent->Axis[i * 3 + 0];
            entity->Origin[1] += tag.Origin[i] * parent->Axis[i * 3 + 1];
            entity->Origin[2] += tag.Origin[i] * parent->Axis[i * 3 + 2];
        }

        // entity->axis = entity->axis * tag.axis * parent->axis
        float* temp = stackalloc float[9];
        MatMul3x3(entity->Axis, tag.Axis, temp);
        MatMul3x3(temp, parent->Axis, entity->Axis);

        entity->OldOrigin[0] = entity->Origin[0];
        entity->OldOrigin[1] = entity->Origin[1];
        entity->OldOrigin[2] = entity->Origin[2];
    }

    private static void MatMul3x3(float* a, float* b, float* result)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                result[i * 3 + j] =
                    a[i * 3 + 0] * b[0 * 3 + j] +
                    a[i * 3 + 1] * b[1 * 3 + j] +
                    a[i * 3 + 2] * b[2 * 3 + j];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetVec3(float* v, float x, float y, float z)
    {
        v[0] = x; v[1] = y; v[2] = z;
    }

    private static void SetYawAxis(float* axis, float yawDeg)
    {
        float rad = yawDeg * MathF.PI / 180.0f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        axis[0] = c;  axis[1] = s;  axis[2] = 0;
        axis[3] = -s; axis[4] = c;  axis[5] = 0;
        axis[6] = 0;  axis[7] = 0;  axis[8] = 1;
    }

    private static void SetIdentityAxis(float* axis)
    {
        for (int i = 0; i < 9; i++) axis[i] = 0;
        axis[0] = 1; axis[4] = 1; axis[8] = 1;
    }

    // --- Discovery ---

    public static string[] DiscoverModels()
    {
        string[] dirs = Syscalls.FS_GetFileList("models/players", "/");
        if (dirs.Length == 0)
            return ["sarge", "grunt", "major", "visor", "slash", "razor",
                    "keel", "lucy", "tankjr", "bitterman", "xaero", "uriel",
                    "hunter", "klesk", "anarki", "orbb", "bones", "crash",
                    "doom", "mynx", "patriot", "ranger", "sorlag", "stripe"];

        var valid = new System.Collections.Generic.List<string>();
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d) || d == "." || d == "..") continue;
            valid.Add(d);
        }
        return valid.Count > 0 ? valid.ToArray() : ["sarge"];
    }

    public static string[] DiscoverSkins(string modelName)
    {
        string[] files = Syscalls.FS_GetFileList($"models/players/{modelName}", ".skin");
        var skins = new System.Collections.Generic.HashSet<string>();
        foreach (var f in files)
        {
            if (f.StartsWith("lower_") && f.EndsWith(".skin"))
            {
                string skin = f.Substring(6, f.Length - 11);
                if (!string.IsNullOrEmpty(skin)) skins.Add(skin);
            }
        }
        if (skins.Count == 0) skins.Add("default");
        var result = new string[skins.Count];
        skins.CopyTo(result);
        System.Array.Sort(result);
        return result;
    }
}
