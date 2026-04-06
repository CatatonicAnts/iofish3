using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CGameDotNet;

/// <summary>
/// Q3 player rendering — 3-part model (legs/torso/head) with skeletal tag
/// attachment, per-part animation via RunLerpFrame, and first-person weapon.
/// Equivalent to cg_players.c + cg_weapons.c (view weapon) from the C cgame.
/// </summary>
public static unsafe class Player
{
    // ── Constants ──
    private const int MAX_CLIENTS = 64;
    private const int MAX_GENTITIES = 1024;
    private const int ANIM_TOGGLEBIT = 128;

    // ── Animation number constants (animNumber_t) ──
    public const int BOTH_DEATH1 = 0;
    public const int BOTH_DEAD1 = 1;
    public const int BOTH_DEATH2 = 2;
    public const int BOTH_DEAD2 = 3;
    public const int BOTH_DEATH3 = 4;
    public const int BOTH_DEAD3 = 5;
    public const int TORSO_GESTURE = 6;
    public const int TORSO_ATTACK = 7;
    public const int TORSO_ATTACK2 = 8;
    public const int TORSO_DROP = 9;
    public const int TORSO_RAISE = 10;
    public const int TORSO_STAND = 11;
    public const int TORSO_STAND2 = 12;
    public const int LEGS_WALKCR = 13;
    public const int LEGS_WALK = 14;
    public const int LEGS_RUN = 15;
    public const int LEGS_BACK = 16;
    public const int LEGS_SWIM = 17;
    public const int LEGS_JUMP = 18;
    public const int LEGS_LAND = 19;
    public const int LEGS_JUMPB = 20;
    public const int LEGS_LANDB = 21;
    public const int LEGS_IDLE = 22;
    public const int LEGS_IDLECR = 23;
    public const int LEGS_TURN = 24;
    public const int TORSO_GETFLAG = 25;
    public const int TORSO_GUARDBASE = 26;
    public const int TORSO_PATROL = 27;
    public const int TORSO_FOLLOWME = 28;
    public const int TORSO_AFFIRMATIVE = 29;
    public const int TORSO_NEGATIVE = 30;
    public const int MAX_ANIMATIONS = 31;
    public const int LEGS_BACKCR = 31;
    public const int LEGS_BACKWALK = 32;
    public const int FLAG_RUN = 33;
    public const int FLAG_STAND = 34;
    public const int FLAG_STAND2RUN = 35;
    public const int MAX_TOTALANIMATIONS = 36;

    // Powerup indices
    private const int PW_HASTE = 3;

    // Muzzle flash
    private const int MUZZLE_FLASH_TIME = 20;
    private const int EF_FIRING = 0x00000100;

    // ── Orientation from R_LerpTag (48 bytes) ──
    [StructLayout(LayoutKind.Sequential)]
    private struct Orientation
    {
        public float OriginX, OriginY, OriginZ;
        public float Axis0X, Axis0Y, Axis0Z;
        public float Axis1X, Axis1Y, Axis1Z;
        public float Axis2X, Axis2Y, Axis2Z;
    }

    // ── Animation entry ──
    public struct Animation
    {
        public int FirstFrame;
        public int NumFrames;
        public int LoopFrames;
        public int FrameLerp;
        public int InitialLerp;
        public bool Reversed;
        public bool FlipFlop;
    }

    // ── LerpFrame — per-animation-part tracking ──
    public struct LerpFrame
    {
        public int OldFrame;
        public int OldFrameTime;
        public int Frame;
        public int FrameTime;
        public float Backlerp;
        public float YawAngle;
        public bool Yawing;
        public float PitchAngle;
        public bool Pitching;
        public int AnimationNumber;
        public int AnimationIndex;
        public int AnimationTime;
    }

    // ── PlayerEntity — persistent per-entity player state ──
    public struct PlayerEntity
    {
        public LerpFrame Legs;
        public LerpFrame Torso;
        public int PainTime;
        public int PainDirection;
    }

    // ── ClientInfo — per-client data ──
    public class ClientInfo
    {
        public string Name = "";
        public int Team;
        public bool InfoValid;

        public int LegsModel;
        public int LegsSkin;
        public int TorsoModel;
        public int TorsoSkin;
        public int HeadModel;
        public int HeadSkin;
        public int ModelIcon;

        public Animation[] Animations = new Animation[MAX_TOTALANIMATIONS];

        public string ModelName = "sarge";
        public string SkinName = "default";
        public string HeadModelName = "sarge";
        public string HeadSkinName = "default";

        public bool NewAnims;
    }

    // ── Static state ──
    private static ClientInfo[] _clientInfo = null!;
    private static PlayerEntity[] _playerEntities = null!;

    // Weapon hand models (for first-person view weapon)
    private static int _handModel;

    // ── Public API ──

    public static void Init()
    {
        _clientInfo = new ClientInfo[MAX_CLIENTS];
        for (int i = 0; i < MAX_CLIENTS; i++)
            _clientInfo[i] = new ClientInfo();
        _playerEntities = new PlayerEntity[MAX_GENTITIES];

        _handModel = Syscalls.R_RegisterModel("models/weapons2/shotgun/shotgun_hand.md3");
        Syscalls.Print("[.NET cgame] Player system initialized\n");
    }

    public static ClientInfo? GetClientInfo(int clientNum)
    {
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return null;
        return _clientInfo[clientNum];
    }

    public static string GetClientName(int clientNum)
    {
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return "";
        var ci = _clientInfo[clientNum];
        return ci.InfoValid ? ci.Name : "";
    }

    // ── Info string parsing ──

    public static string InfoValueForKey(string infoString, string key)
    {
        if (string.IsNullOrEmpty(infoString)) return "";

        int i = 0;
        while (i < infoString.Length)
        {
            // skip leading backslash
            if (infoString[i] == '\\') i++;
            if (i >= infoString.Length) break;

            // read key
            int keyStart = i;
            while (i < infoString.Length && infoString[i] != '\\')
                i++;
            string k = infoString[keyStart..i];

            // skip separator
            if (i < infoString.Length && infoString[i] == '\\') i++;

            // read value
            int valStart = i;
            while (i < infoString.Length && infoString[i] != '\\')
                i++;
            string v = infoString[valStart..i];

            if (k == key) return v;
        }
        return "";
    }

    // ── NewClientInfo ──

    public static void NewClientInfo(int clientNum, byte* gameStateRaw)
    {
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return;

        string cs = Q3GameState.GetConfigString(gameStateRaw, Q3GameState.CS_PLAYERS + clientNum);
        if (string.IsNullOrEmpty(cs))
        {
            _clientInfo[clientNum].InfoValid = false;
            return;
        }

        var ci = _clientInfo[clientNum];
        ci.Name = InfoValueForKey(cs, "n");
        if (string.IsNullOrEmpty(ci.Name)) ci.Name = $"Player{clientNum}";

        // model/skin
        string model = InfoValueForKey(cs, "model");
        if (string.IsNullOrEmpty(model)) model = "sarge";
        int slash = model.IndexOf('/');
        if (slash >= 0)
        {
            ci.ModelName = model[..slash];
            ci.SkinName = model[(slash + 1)..];
        }
        else
        {
            ci.ModelName = model;
            ci.SkinName = "default";
        }

        // head model/skin
        string hmodel = InfoValueForKey(cs, "hmodel");
        if (string.IsNullOrEmpty(hmodel)) hmodel = model;
        slash = hmodel.IndexOf('/');
        if (slash >= 0)
        {
            ci.HeadModelName = hmodel[..slash];
            ci.HeadSkinName = hmodel[(slash + 1)..];
        }
        else
        {
            ci.HeadModelName = hmodel;
            ci.HeadSkinName = ci.SkinName;
        }

        // team
        string teamStr = InfoValueForKey(cs, "t");
        ci.Team = int.TryParse(teamStr, out int t) ? t : 0;

        RegisterClientModel(ci);
        ParseAnimationFile(ci);

        ci.InfoValid = ci.LegsModel != 0 && ci.TorsoModel != 0 && ci.HeadModel != 0;
    }

    // ── RegisterClientModel ──

    private static void RegisterClientModel(ClientInfo ci)
    {
        // Load player model parts
        ci.LegsModel = Syscalls.R_RegisterModel($"models/players/{ci.ModelName}/lower.md3");
        ci.TorsoModel = Syscalls.R_RegisterModel($"models/players/{ci.ModelName}/upper.md3");
        ci.HeadModel = Syscalls.R_RegisterModel($"models/players/{ci.HeadModelName}/head.md3");

        // Fallback to sarge if model not found
        if (ci.LegsModel == 0 || ci.TorsoModel == 0 || ci.HeadModel == 0)
        {
            ci.LegsModel = Syscalls.R_RegisterModel("models/players/sarge/lower.md3");
            ci.TorsoModel = Syscalls.R_RegisterModel("models/players/sarge/upper.md3");
            ci.HeadModel = Syscalls.R_RegisterModel("models/players/sarge/head.md3");
            ci.ModelName = "sarge";
            ci.HeadModelName = "sarge";
            ci.SkinName = "default";
            ci.HeadSkinName = "default";
        }

        // Load skins — try specific skin first, fallback to default
        ci.LegsSkin = Syscalls.R_RegisterSkin($"models/players/{ci.ModelName}/lower_{ci.SkinName}.skin");
        if (ci.LegsSkin == 0)
            ci.LegsSkin = Syscalls.R_RegisterSkin($"models/players/{ci.ModelName}/lower_default.skin");

        ci.TorsoSkin = Syscalls.R_RegisterSkin($"models/players/{ci.ModelName}/upper_{ci.SkinName}.skin");
        if (ci.TorsoSkin == 0)
            ci.TorsoSkin = Syscalls.R_RegisterSkin($"models/players/{ci.ModelName}/upper_default.skin");

        ci.HeadSkin = Syscalls.R_RegisterSkin($"models/players/{ci.HeadModelName}/head_{ci.HeadSkinName}.skin");
        if (ci.HeadSkin == 0)
            ci.HeadSkin = Syscalls.R_RegisterSkin($"models/players/{ci.HeadModelName}/head_default.skin");

        // Load icon
        ci.ModelIcon = Syscalls.R_RegisterShaderNoMip($"models/players/{ci.HeadModelName}/icon_{ci.HeadSkinName}");
        if (ci.ModelIcon == 0)
            ci.ModelIcon = Syscalls.R_RegisterShaderNoMip($"models/players/{ci.HeadModelName}/icon_default");

        // Check for new animation system by testing for tag_flag
        Orientation tag;
        ci.NewAnims = Syscalls.R_LerpTag(&tag, ci.TorsoModel, 0, 0, 0, "tag_flag") >= 0;
    }

    // ── ParseAnimationFile ──

    private static void ParseAnimationFile(ClientInfo ci)
    {
        string path = $"models/players/{ci.ModelName}/animation.cfg";

        // Read file via engine filesystem
        int fileHandle;
        int fileLen = Syscalls.FOpenFile(path, &fileHandle, 0); // 0 = FS_READ
        if (fileLen <= 0)
        {
            // Fallback: set minimal stand animation
            SetDefaultAnimations(ci);
            return;
        }

        const int maxFileSize = 16384;
        if (fileLen > maxFileSize) fileLen = maxFileSize;

        byte* buf = stackalloc byte[maxFileSize + 1];
        Syscalls.FRead(buf, fileLen, fileHandle);
        Syscalls.FCloseFile(fileHandle);
        buf[fileLen] = 0;

        string content = Marshal.PtrToStringUTF8((nint)buf) ?? "";

        // Parse animation entries
        int animIndex = 0;
        int legsStartFrame = 0;
        bool foundLegsStart = false;

        string[] lines = content.Split('\n', StringSplitOptions.None);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '/' || line[0] == '#') continue;
            if (line.StartsWith("sex", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("headoffset", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("footsteps", StringComparison.OrdinalIgnoreCase)) continue;

            // Parse: firstFrame numFrames loopFrames fps [...]
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], out int firstFrame)) continue;
            if (!int.TryParse(parts[1], out int numFrames)) continue;
            if (!int.TryParse(parts[2], out int loopFrames)) continue;
            if (!int.TryParse(parts[3], out int fps)) continue;

            if (animIndex >= MAX_TOTALANIMATIONS) break;

            ref var anim = ref ci.Animations[animIndex];
            anim.FirstFrame = firstFrame;
            anim.NumFrames = numFrames;
            anim.LoopFrames = loopFrames;
            anim.Reversed = false;
            anim.FlipFlop = false;

            if (fps == 0) fps = 1;
            anim.FrameLerp = 1000 / Math.Abs(fps);
            anim.InitialLerp = 1000 / Math.Abs(fps);
            if (fps < 0) anim.Reversed = true;

            // For legacy models (no tag_flag), legs frames use an offset
            if (!ci.NewAnims && animIndex >= LEGS_WALKCR)
            {
                if (!foundLegsStart)
                {
                    legsStartFrame = firstFrame;
                    foundLegsStart = true;
                }
                anim.FirstFrame -= legsStartFrame;
            }

            animIndex++;
        }

        // If we didn't get enough animations, fill with defaults
        if (animIndex < MAX_ANIMATIONS)
            SetDefaultAnimations(ci);
    }

    private static void SetDefaultAnimations(ClientInfo ci)
    {
        for (int i = 0; i < MAX_TOTALANIMATIONS; i++)
        {
            ref var a = ref ci.Animations[i];
            a.FirstFrame = 0;
            a.NumFrames = 1;
            a.LoopFrames = 1;
            a.FrameLerp = 100;
            a.InitialLerp = 100;
            a.Reversed = false;
            a.FlipFlop = false;
        }
    }

    // ── ClearLerpFrame ──

    public static void ClearLerpFrame(ClientInfo ci, ref LerpFrame lf, int animationNumber)
    {
        lf.FrameTime = 0;
        lf.OldFrameTime = 0;
        lf.AnimationTime = 0;
        lf.AnimationNumber = animationNumber;
        lf.AnimationIndex = animationNumber & ~ANIM_TOGGLEBIT;

        if (lf.AnimationIndex < 0) lf.AnimationIndex = 0;
        if (lf.AnimationIndex >= MAX_TOTALANIMATIONS) lf.AnimationIndex = 0;

        ref var anim = ref ci.Animations[lf.AnimationIndex];
        lf.Frame = anim.FirstFrame;
        lf.OldFrame = anim.FirstFrame;
        lf.Backlerp = 0;
    }

    // ── RunLerpFrame ──

    public static void RunLerpFrame(ClientInfo ci, ref LerpFrame lf, int newAnimation, float speedScale, int time)
    {
        // Check for animation change
        int newIndex = newAnimation & ~ANIM_TOGGLEBIT;
        if (newIndex < 0 || newIndex >= MAX_TOTALANIMATIONS)
            newIndex = 0;

        if (lf.AnimationNumber != newAnimation)
        {
            lf.AnimationNumber = newAnimation;
            lf.AnimationIndex = newIndex;
            lf.AnimationTime = lf.FrameTime + ci.Animations[newIndex].InitialLerp;
        }

        ref var anim = ref ci.Animations[lf.AnimationIndex];

        // If the frame time is in the future, don't advance
        if (lf.FrameTime == 0)
        {
            lf.FrameTime = time;
            lf.OldFrameTime = time;
            lf.AnimationTime = time;
            lf.OldFrame = anim.FirstFrame;
            lf.Frame = anim.FirstFrame;
            lf.Backlerp = 0;
            return;
        }

        if (anim.NumFrames <= 0)
        {
            lf.OldFrame = 0;
            lf.Frame = 0;
            lf.Backlerp = 0;
            return;
        }

        int frameLerp = anim.FrameLerp;
        if (speedScale != 0)
            frameLerp = (int)(frameLerp / speedScale);
        if (frameLerp < 1) frameLerp = 1;

        // Advance frames
        if (time < lf.AnimationTime)
            lf.AnimationTime = time;

        while (lf.FrameTime + frameLerp <= time)
        {
            lf.OldFrame = lf.Frame;
            lf.OldFrameTime = lf.FrameTime;
            lf.FrameTime += frameLerp;

            // Calculate which animation frame
            int elapsed = lf.FrameTime - lf.AnimationTime;
            int frameNum = elapsed / frameLerp;

            if (anim.NumFrames > 0)
            {
                if (anim.FlipFlop)
                {
                    int cycle = anim.NumFrames * 2 - 2;
                    if (cycle > 0) frameNum %= cycle;
                    if (frameNum >= anim.NumFrames)
                        frameNum = cycle - frameNum;
                }
                else if (anim.LoopFrames > 0)
                {
                    // Looping animation
                    if (frameNum >= anim.NumFrames)
                    {
                        int loopStart = anim.NumFrames - anim.LoopFrames;
                        if (loopStart < 0) loopStart = 0;
                        frameNum = loopStart + ((frameNum - loopStart) % anim.LoopFrames);
                    }
                }
                else
                {
                    // Non-looping: clamp to last frame
                    if (frameNum >= anim.NumFrames)
                        frameNum = anim.NumFrames - 1;
                }
            }

            if (anim.Reversed)
                lf.Frame = anim.FirstFrame + anim.NumFrames - 1 - frameNum;
            else
                lf.Frame = anim.FirstFrame + frameNum;
        }

        // Calculate backlerp for interpolation
        if (lf.FrameTime == lf.OldFrameTime)
            lf.Backlerp = 0;
        else
            lf.Backlerp = 1.0f - (float)(time - lf.OldFrameTime) / (lf.FrameTime - lf.OldFrameTime);
    }

    // ── PlayerAnimation ──

    public static void PlayerAnimation(int clientNum, ref PlayerEntity pe, Q3EntityState* es, int time)
    {
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return;
        var ci = _clientInfo[clientNum];
        if (!ci.InfoValid) return;

        // Speed scale: 1.5 if haste powerup active
        float speedScale = ((es->Powerups & (1 << PW_HASTE)) != 0) ? 1.5f : 1.0f;

        RunLerpFrame(ci, ref pe.Legs, es->LegsAnim, speedScale, time);
        RunLerpFrame(ci, ref pe.Torso, es->TorsoAnim, speedScale, time);
    }

    // ── Render (CG_Player equivalent) ──

    public static void Render(ref Q3EntityState currentState,
        float lerpOriginX, float lerpOriginY, float lerpOriginZ,
        float lerpAnglesX, float lerpAnglesY, float lerpAnglesZ,
        int entityNum, int myClientNum, int time)
    {
        int clientNum = currentState.ClientNum;
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return;

        var ci = _clientInfo[clientNum];
        if (!ci.InfoValid) return;

        ref var pe = ref _playerEntities[entityNum];

        // Run animation
        PlayerAnimation(clientNum, ref pe, (Q3EntityState*)Unsafe.AsPointer(ref currentState), time);

        // Render flags
        int renderfx = 0;
        if (entityNum == myClientNum)
            renderfx |= Q3RefEntity.RF_THIRD_PERSON;

        // ── Build legs refEntity ──
        Q3RefEntity legs = default;
        legs.ReType = Q3RefEntity.RT_MODEL;
        legs.RenderFx = renderfx;
        legs.HModel = ci.LegsModel;
        legs.CustomSkin = ci.LegsSkin;
        legs.FrameNum = pe.Legs.Frame;
        legs.OldFrame = pe.Legs.OldFrame;
        legs.Backlerp = pe.Legs.Backlerp;

        legs.OriginX = lerpOriginX;
        legs.OriginY = lerpOriginY;
        legs.OriginZ = lerpOriginZ;
        legs.OldOriginX = lerpOriginX;
        legs.OldOriginY = lerpOriginY;
        legs.OldOriginZ = lerpOriginZ;

        // Lighting origin same as actual origin
        legs.LightingOriginX = lerpOriginX;
        legs.LightingOriginY = lerpOriginY;
        legs.LightingOriginZ = lerpOriginZ;
        legs.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

        // Build axis from yaw only for legs (players rotate on yaw axis)
        AnglesToAxis(0, lerpAnglesY, 0, ref legs);

        legs.ShaderRGBA_R = 255; legs.ShaderRGBA_G = 255;
        legs.ShaderRGBA_B = 255; legs.ShaderRGBA_A = 255;

        Syscalls.R_AddRefEntityToScene(&legs);

        // ── Build torso refEntity ──
        Q3RefEntity torso = default;
        torso.ReType = Q3RefEntity.RT_MODEL;
        torso.RenderFx = renderfx;
        torso.HModel = ci.TorsoModel;
        torso.CustomSkin = ci.TorsoSkin;
        torso.FrameNum = pe.Torso.Frame;
        torso.OldFrame = pe.Torso.OldFrame;
        torso.Backlerp = pe.Torso.Backlerp;
        torso.ShaderRGBA_R = 255; torso.ShaderRGBA_G = 255;
        torso.ShaderRGBA_B = 255; torso.ShaderRGBA_A = 255;

        // Copy lighting origin from legs
        torso.LightingOriginX = lerpOriginX;
        torso.LightingOriginY = lerpOriginY;
        torso.LightingOriginZ = lerpOriginZ;
        torso.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

        // Initialize torso axis from pitch+yaw (pitch for looking up/down)
        AnglesToAxis(lerpAnglesX, lerpAnglesY, 0, ref torso);

        PositionRotatedEntityOnTag(ref torso, ref legs, ci.LegsModel, "tag_torso");
        Syscalls.R_AddRefEntityToScene(&torso);

        // ── Build head refEntity ──
        Q3RefEntity head = default;
        head.ReType = Q3RefEntity.RT_MODEL;
        head.RenderFx = renderfx;
        head.HModel = ci.HeadModel;
        head.CustomSkin = ci.HeadSkin;
        head.ShaderRGBA_R = 255; head.ShaderRGBA_G = 255;
        head.ShaderRGBA_B = 255; head.ShaderRGBA_A = 255;

        head.LightingOriginX = lerpOriginX;
        head.LightingOriginY = lerpOriginY;
        head.LightingOriginZ = lerpOriginZ;
        head.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

        // Head inherits torso axis
        head.Axis0X = torso.Axis0X; head.Axis0Y = torso.Axis0Y; head.Axis0Z = torso.Axis0Z;
        head.Axis1X = torso.Axis1X; head.Axis1Y = torso.Axis1Y; head.Axis1Z = torso.Axis1Z;
        head.Axis2X = torso.Axis2X; head.Axis2Y = torso.Axis2Y; head.Axis2Z = torso.Axis2Z;

        PositionRotatedEntityOnTag(ref head, ref torso, ci.TorsoModel, "tag_head");
        Syscalls.R_AddRefEntityToScene(&head);
    }

    // ── PositionRotatedEntityOnTag ──

    public static void PositionRotatedEntityOnTag(ref Q3RefEntity entity, ref Q3RefEntity parent,
        int parentModel, string tagName)
    {
        Orientation lerped;
        Syscalls.R_LerpTag(&lerped, parentModel, parent.OldFrame, parent.FrameNum,
            1.0f - parent.Backlerp, tagName);

        // entity.origin = parent.origin + tag.origin rotated by parent.axis
        entity.OriginX = parent.OriginX
            + lerped.OriginX * parent.Axis0X
            + lerped.OriginY * parent.Axis1X
            + lerped.OriginZ * parent.Axis2X;
        entity.OriginY = parent.OriginY
            + lerped.OriginX * parent.Axis0Y
            + lerped.OriginY * parent.Axis1Y
            + lerped.OriginZ * parent.Axis2Y;
        entity.OriginZ = parent.OriginZ
            + lerped.OriginX * parent.Axis0Z
            + lerped.OriginY * parent.Axis1Z
            + lerped.OriginZ * parent.Axis2Z;

        entity.OldOriginX = entity.OriginX;
        entity.OldOriginY = entity.OriginY;
        entity.OldOriginZ = entity.OriginZ;

        // entity.axis = MatrixMultiply(entity.axis, lerped.axis)
        // then MatrixMultiply(result, parent.axis)
        float e0x = entity.Axis0X, e0y = entity.Axis0Y, e0z = entity.Axis0Z;
        float e1x = entity.Axis1X, e1y = entity.Axis1Y, e1z = entity.Axis1Z;
        float e2x = entity.Axis2X, e2y = entity.Axis2Y, e2z = entity.Axis2Z;

        // Step 1: tmp = entity.axis * lerped.axis
        float t0x = e0x * lerped.Axis0X + e0y * lerped.Axis0Y + e0z * lerped.Axis0Z;
        float t0y = e0x * lerped.Axis1X + e0y * lerped.Axis1Y + e0z * lerped.Axis1Z;
        float t0z = e0x * lerped.Axis2X + e0y * lerped.Axis2Y + e0z * lerped.Axis2Z;

        float t1x = e1x * lerped.Axis0X + e1y * lerped.Axis0Y + e1z * lerped.Axis0Z;
        float t1y = e1x * lerped.Axis1X + e1y * lerped.Axis1Y + e1z * lerped.Axis1Z;
        float t1z = e1x * lerped.Axis2X + e1y * lerped.Axis2Y + e1z * lerped.Axis2Z;

        float t2x = e2x * lerped.Axis0X + e2y * lerped.Axis0Y + e2z * lerped.Axis0Z;
        float t2y = e2x * lerped.Axis1X + e2y * lerped.Axis1Y + e2z * lerped.Axis1Z;
        float t2z = e2x * lerped.Axis2X + e2y * lerped.Axis2Y + e2z * lerped.Axis2Z;

        // Step 2: result = tmp * parent.axis
        entity.Axis0X = t0x * parent.Axis0X + t0y * parent.Axis1X + t0z * parent.Axis2X;
        entity.Axis0Y = t0x * parent.Axis0Y + t0y * parent.Axis1Y + t0z * parent.Axis2Y;
        entity.Axis0Z = t0x * parent.Axis0Z + t0y * parent.Axis1Z + t0z * parent.Axis2Z;

        entity.Axis1X = t1x * parent.Axis0X + t1y * parent.Axis1X + t1z * parent.Axis2X;
        entity.Axis1Y = t1x * parent.Axis0Y + t1y * parent.Axis1Y + t1z * parent.Axis2Y;
        entity.Axis1Z = t1x * parent.Axis0Z + t1y * parent.Axis1Z + t1z * parent.Axis2Z;

        entity.Axis2X = t2x * parent.Axis0X + t2y * parent.Axis1X + t2z * parent.Axis2X;
        entity.Axis2Y = t2x * parent.Axis0Y + t2y * parent.Axis1Y + t2z * parent.Axis2Y;
        entity.Axis2Z = t2x * parent.Axis0Z + t2y * parent.Axis1Z + t2z * parent.Axis2Z;
    }

    // ── AddViewWeapon (first-person weapon rendering) ──

    public static void AddViewWeapon(Q3PlayerState* ps, int time,
        float viewOrgX, float viewOrgY, float viewOrgZ,
        float viewAxis0X, float viewAxis0Y, float viewAxis0Z,
        float viewAxis1X, float viewAxis1Y, float viewAxis1Z,
        float viewAxis2X, float viewAxis2Y, float viewAxis2Z,
        int fov, int muzzleFlashTime, int eFlags)
    {
        // Don't draw weapon for spectators or during intermission
        if (ps->PmType == PmType.PM_SPECTATOR || ps->PmType == PmType.PM_INTERMISSION)
            return;
        if (ps->PmType == PmType.PM_SPINTERMISSION)
            return;

        int clientNum = ps->ClientNum;
        if (clientNum < 0 || clientNum >= MAX_CLIENTS) return;
        var ci = _clientInfo[clientNum];
        if (!ci.InfoValid) return;

        int weaponNum = ps->Weapon;
        if (weaponNum <= Weapons.WP_NONE || weaponNum >= Weapons.WP_NUM_WEAPONS) return;

        // Get hand model for this weapon
        int handsModel = WeaponEffects.GetHandsModel(weaponNum);
        if (handsModel == 0) handsModel = _handModel;
        if (handsModel == 0) return;

        Q3RefEntity hand = default;
        hand.ReType = Q3RefEntity.RT_MODEL;
        hand.RenderFx = Q3RefEntity.RF_DEPTHHACK | Q3RefEntity.RF_FIRST_PERSON | Q3RefEntity.RF_MINLIGHT;

        // Use the torso animation for the weapon hand frame
        ref var pe = ref _playerEntities[clientNum];
        hand.FrameNum = MapTorsoToWeaponFrame(ci, pe.Torso.Frame);
        hand.OldFrame = MapTorsoToWeaponFrame(ci, pe.Torso.OldFrame);
        hand.Backlerp = pe.Torso.Backlerp;

        hand.HModel = handsModel;

        // Position at view origin
        hand.OriginX = viewOrgX;
        hand.OriginY = viewOrgY;
        hand.OriginZ = viewOrgZ;

        // Apply FOV offset for wide FOV
        if (fov > 90)
        {
            float fovOffset = -0.2f * (fov - 90);
            hand.OriginX += fovOffset * viewAxis0X;
            hand.OriginY += fovOffset * viewAxis0Y;
            hand.OriginZ += fovOffset * viewAxis0Z;
        }

        hand.OldOriginX = hand.OriginX;
        hand.OldOriginY = hand.OriginY;
        hand.OldOriginZ = hand.OriginZ;

        // Copy view axis
        hand.Axis0X = viewAxis0X; hand.Axis0Y = viewAxis0Y; hand.Axis0Z = viewAxis0Z;
        hand.Axis1X = viewAxis1X; hand.Axis1Y = viewAxis1Y; hand.Axis1Z = viewAxis1Z;
        hand.Axis2X = viewAxis2X; hand.Axis2Y = viewAxis2Y; hand.Axis2Z = viewAxis2Z;

        hand.LightingOriginX = hand.OriginX;
        hand.LightingOriginY = hand.OriginY;
        hand.LightingOriginZ = hand.OriginZ;
        hand.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

        hand.ShaderRGBA_R = 255; hand.ShaderRGBA_G = 255;
        hand.ShaderRGBA_B = 255; hand.ShaderRGBA_A = 255;

        Syscalls.R_AddRefEntityToScene(&hand);

        // Attach weapon model at tag_weapon on the hand
        int weaponModel = WeaponEffects.GetWeaponModel(weaponNum);
        if (weaponModel != 0)
        {
            Q3RefEntity gun = default;
            gun.ReType = Q3RefEntity.RT_MODEL;
            gun.RenderFx = hand.RenderFx;
            gun.HModel = weaponModel;

            // Identity axis for the weapon
            gun.Axis0X = 1; gun.Axis0Y = 0; gun.Axis0Z = 0;
            gun.Axis1X = 0; gun.Axis1Y = 1; gun.Axis1Z = 0;
            gun.Axis2X = 0; gun.Axis2Y = 0; gun.Axis2Z = 1;

            gun.LightingOriginX = hand.LightingOriginX;
            gun.LightingOriginY = hand.LightingOriginY;
            gun.LightingOriginZ = hand.LightingOriginZ;
            gun.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

            gun.ShaderRGBA_R = 255; gun.ShaderRGBA_G = 255;
            gun.ShaderRGBA_B = 255; gun.ShaderRGBA_A = 255;

            PositionRotatedEntityOnTag(ref gun, ref hand, handsModel, "tag_weapon");
            Syscalls.R_AddRefEntityToScene(&gun);

            // Attach barrel model at tag_barrel on the weapon
            int barrelModel = WeaponEffects.GetBarrelModel(weaponNum);
            if (barrelModel != 0)
            {
                Q3RefEntity barrel = default;
                barrel.ReType = Q3RefEntity.RT_MODEL;
                barrel.RenderFx = hand.RenderFx;
                barrel.HModel = barrelModel;

                barrel.Axis0X = 1; barrel.Axis0Y = 0; barrel.Axis0Z = 0;
                barrel.Axis1X = 0; barrel.Axis1Y = 1; barrel.Axis1Z = 0;
                barrel.Axis2X = 0; barrel.Axis2Y = 0; barrel.Axis2Z = 1;

                barrel.LightingOriginX = hand.LightingOriginX;
                barrel.LightingOriginY = hand.LightingOriginY;
                barrel.LightingOriginZ = hand.LightingOriginZ;
                barrel.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

                barrel.ShaderRGBA_R = 255; barrel.ShaderRGBA_G = 255;
                barrel.ShaderRGBA_B = 255; barrel.ShaderRGBA_A = 255;

                PositionRotatedEntityOnTag(ref barrel, ref gun, weaponModel, "tag_barrel");
                Syscalls.R_AddRefEntityToScene(&barrel);
            }

            // Muzzle flash model at tag_flash
            bool showFlash = false;
            if (weaponNum == Weapons.WP_LIGHTNING || weaponNum == Weapons.WP_GAUNTLET ||
                weaponNum == Weapons.WP_GRAPPLING_HOOK)
            {
                // Continuous flash while firing
                showFlash = (eFlags & EF_FIRING) != 0;
            }
            else
            {
                // Impulse flash — show for MUZZLE_FLASH_TIME after fire event
                showFlash = (time - muzzleFlashTime) <= MUZZLE_FLASH_TIME && muzzleFlashTime > 0;
            }

            if (showFlash)
            {
                int flashModel = WeaponEffects.GetFlashModel(weaponNum);
                if (flashModel != 0)
                {
                    Q3RefEntity flash = default;
                    flash.ReType = Q3RefEntity.RT_MODEL;
                    flash.RenderFx = hand.RenderFx;
                    flash.HModel = flashModel;

                    // Random roll for visual variety
                    float rollAngle = (Random.Shared.Next() % 21 - 10) * MathF.PI / 180.0f;
                    float cr = MathF.Cos(rollAngle);
                    float sr = MathF.Sin(rollAngle);
                    flash.Axis0X = 1; flash.Axis0Y = 0; flash.Axis0Z = 0;
                    flash.Axis1X = 0; flash.Axis1Y = cr; flash.Axis1Z = -sr;
                    flash.Axis2X = 0; flash.Axis2Y = sr; flash.Axis2Z = cr;

                    flash.LightingOriginX = hand.LightingOriginX;
                    flash.LightingOriginY = hand.LightingOriginY;
                    flash.LightingOriginZ = hand.LightingOriginZ;
                    flash.RenderFx |= Q3RefEntity.RF_LIGHTING_ORIGIN;

                    flash.ShaderRGBA_R = 255; flash.ShaderRGBA_G = 255;
                    flash.ShaderRGBA_B = 255; flash.ShaderRGBA_A = 255;

                    PositionRotatedEntityOnTag(ref flash, ref gun, weaponModel, "tag_flash");
                    Syscalls.R_AddRefEntityToScene(&flash);

                    // Add dynamic light at flash position
                    WeaponEffects.MuzzleFlash(flash.OriginX, flash.OriginY, flash.OriginZ, weaponNum, time);
                }
            }
        }
    }

    // ── MapTorsoToWeaponFrame ──

    private static int MapTorsoToWeaponFrame(ClientInfo ci, int frame)
    {
        // Map torso animation frames to weapon hand model frames.
        // Matches CG_MapTorsoToWeaponFrame from cg_weapons.c.
        ref var drop = ref ci.Animations[TORSO_DROP];
        if (frame >= drop.FirstFrame && frame < drop.FirstFrame + 9)
            return frame - drop.FirstFrame + 6;

        ref var attack = ref ci.Animations[TORSO_ATTACK];
        if (frame >= attack.FirstFrame && frame < attack.FirstFrame + 6)
            return 1 + frame - attack.FirstFrame;

        ref var attack2 = ref ci.Animations[TORSO_ATTACK2];
        if (frame >= attack2.FirstFrame && frame < attack2.FirstFrame + 6)
            return 1 + frame - attack2.FirstFrame;

        return 0;
    }

    // ── AnglesToAxis ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AnglesToAxis(float pitchDeg, float yawDeg, float rollDeg, ref Q3RefEntity rent)
    {
        float pitch = pitchDeg * (MathF.PI / 180.0f);
        float yaw = yawDeg * (MathF.PI / 180.0f);
        float roll = rollDeg * (MathF.PI / 180.0f);

        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);

        rent.Axis0X = cp * cy;
        rent.Axis0Y = cp * sy;
        rent.Axis0Z = -sp;
        rent.Axis1X = -sr * sp * cy + cr * -sy;
        rent.Axis1Y = -sr * sp * sy + cr * cy;
        rent.Axis1Z = -sr * cp;
        rent.Axis2X = cr * sp * cy + sr * -sy;
        rent.Axis2Y = cr * sp * sy + sr * cy;
        rent.Axis2Z = cr * cp;
    }
}
