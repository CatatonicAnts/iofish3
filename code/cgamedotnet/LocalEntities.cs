using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// Local entity system — client-side temporary visual entities.
/// Handles explosions, debris, brass casings, smoke puffs, score plums, etc.
/// Equivalent to cg_localents.c from the C cgame.
/// </summary>
public static unsafe class LocalEntities
{
    // ── Types ──

    public enum LeType
    {
        Mark,              // decal on world surface
        Explosion,         // animated model explosion
        SpriteExplosion,   // sprite-based explosion (scales + fades)
        Fragment,          // physics debris (gibs, brass)
        MoveScaleFade,     // moving sprite that scales and fades
        FallScaleFade,     // falling sprite (blood trails)
        FadeRGB,           // fading RGB entity (teleporters, rail)
        ScaleFade,         // stationary sprite that scales and fades
        ScorePlum,         // floating +score text
    }

    [Flags]
    public enum LeFlags
    {
        None = 0,
        PuffDontScale = 0x0001,
        Tumble = 0x0002,
    }

    public struct LocalEntity
    {
        public bool Active;
        public LeType Type;
        public LeFlags Flags;
        public int StartTime;
        public int EndTime;
        public float LifeRate; // 1.0 / (endTime - startTime)

        // Position trajectory
        public int PosType;     // TrajectoryType
        public int PosTime;
        public int PosDuration;
        public float PosBaseX, PosBaseY, PosBaseZ;
        public float PosDeltaX, PosDeltaY, PosDeltaZ;

        // Angle trajectory (for tumbling fragments)
        public int AngType;
        public int AngTime;
        public float AngBaseX, AngBaseY, AngBaseZ;
        public float AngDeltaX, AngDeltaY, AngDeltaZ;

        public float BounceFactor;

        public float ColorR, ColorG, ColorB, ColorA;

        // Ref entity data
        public int ReType;
        public int HModel;
        public int CustomShader;
        public float OriginX, OriginY, OriginZ;
        public float OldOriginX, OldOriginY, OldOriginZ;
        public float Radius;
        public float Rotation;
        public int ShaderTime;
        public byte ShaderR, ShaderG, ShaderB, ShaderA;

        // Dynamic light
        public float Light;
        public float LightR, LightG, LightB;
    }

    // ── Pool ──

    private const int MAX_LOCAL_ENTITIES = 512;
    private static readonly LocalEntity[] _pool = new LocalEntity[MAX_LOCAL_ENTITIES];
    private static int _count;

    private const int DEFAULT_GRAVITY = 800;

    // ── Media ──
    private static int _smokePuffShader;
    private static int _bloodTrailShader;
    private static int _burnMarkShader;
    private static int _bloodMarkShader;
    private static int _explosionShader;

    // Number shaders for score plums (set externally by CGame)
    private static int[] _numberShaders = Array.Empty<int>();

    public static void SetNumberShaders(int[] shaders) => _numberShaders = shaders;

    public static void Init()
    {
        _count = 0;
        Array.Clear(_pool);

        _smokePuffShader = Syscalls.R_RegisterShader("smokePuff");
        _bloodTrailShader = Syscalls.R_RegisterShader("bloodTrail");
        _burnMarkShader = Syscalls.R_RegisterShader("gfx/damage/burn_med_mrk");
        _bloodMarkShader = Syscalls.R_RegisterShader("bloodMark");
        _explosionShader = Syscalls.R_RegisterShader("rocketExplosion");
        Syscalls.Print("[.NET cgame] Local entity system initialized\n");
    }

    public static ref LocalEntity Alloc()
    {
        // Find a free slot or recycle the oldest
        int bestIdx = -1;
        int oldestTime = int.MaxValue;

        for (int i = 0; i < MAX_LOCAL_ENTITIES; i++)
        {
            if (!_pool[i].Active)
            {
                bestIdx = i;
                break;
            }
            if (_pool[i].StartTime < oldestTime)
            {
                oldestTime = _pool[i].StartTime;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) bestIdx = 0;
        _pool[bestIdx] = default;
        _pool[bestIdx].Active = true;
        if (bestIdx >= _count) _count = bestIdx + 1;
        return ref _pool[bestIdx];
    }

    // ── Per-frame processing ──

    public static void AddToScene(int time)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var le = ref _pool[i];
            if (!le.Active) continue;

            if (time >= le.EndTime)
            {
                le.Active = false;
                continue;
            }

            switch (le.Type)
            {
                case LeType.SpriteExplosion:
                    AddSpriteExplosion(ref le, time);
                    break;
                case LeType.Fragment:
                    AddFragment(ref le, time);
                    break;
                case LeType.MoveScaleFade:
                    AddMoveScaleFade(ref le, time);
                    break;
                case LeType.FadeRGB:
                    AddFadeRGB(ref le, time);
                    break;
                case LeType.ScaleFade:
                    AddScaleFade(ref le, time);
                    break;
                case LeType.FallScaleFade:
                    AddFallScaleFade(ref le, time);
                    break;
                case LeType.ScorePlum:
                    AddScorePlum(ref le, time);
                    break;
            }
        }
    }

    // ── Type-specific rendering ──

    private static void AddSpriteExplosion(ref LocalEntity le, int time)
    {
        float c = (le.EndTime - time) / (float)(le.EndTime - le.StartTime);
        if (c > 1) c = 1;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_SPRITE;
        rent.CustomShader = le.CustomShader;
        rent.ShaderTime = le.ShaderTime;

        // Evaluate position
        EvalTrajectory(ref le, time, out rent.OriginX, out rent.OriginY, out rent.OriginZ);
        rent.OldOriginX = rent.OriginX;
        rent.OldOriginY = rent.OriginY;
        rent.OldOriginZ = rent.OriginZ;

        rent.ShaderRGBA_R = 255;
        rent.ShaderRGBA_G = 255;
        rent.ShaderRGBA_B = 255;
        rent.ShaderRGBA_A = (byte)(255 * c * 0.33f);

        rent.Radius = 42 * (1.0f - c) + 30;
        rent.Rotation = le.Rotation;

        // Identity axis
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;

        Syscalls.R_AddRefEntityToScene(&rent);

        // Dynamic light
        if (le.Light > 0)
        {
            float lightFrac = (float)(time - le.StartTime) / (le.EndTime - le.StartTime);
            float light = lightFrac < 0.5f ? 1.0f : 1.0f - (lightFrac - 0.5f) * 2;
            light *= le.Light;
            float* lightOrigin = stackalloc float[3];
            lightOrigin[0] = rent.OriginX; lightOrigin[1] = rent.OriginY; lightOrigin[2] = rent.OriginZ;
            Syscalls.R_AddLightToScene(lightOrigin, light, le.LightR, le.LightG, le.LightB);
        }
    }

    private static void AddFragment(ref LocalEntity le, int time)
    {
        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;
        rent.HModel = le.HModel;

        // Evaluate position with gravity
        EvalTrajectory(ref le, time, out rent.OriginX, out rent.OriginY, out rent.OriginZ);
        rent.OldOriginX = rent.OriginX;
        rent.OldOriginY = rent.OriginY;
        rent.OldOriginZ = rent.OriginZ;

        // Tumble: evaluate angles
        if ((le.Flags & LeFlags.Tumble) != 0)
        {
            float angDt = (time - le.AngTime) * 0.001f;
            float ax = (le.AngBaseX + le.AngDeltaX * angDt) * MathF.PI / 180.0f;
            float ay = (le.AngBaseY + le.AngDeltaY * angDt) * MathF.PI / 180.0f;
            float az = (le.AngBaseZ + le.AngDeltaZ * angDt) * MathF.PI / 180.0f;

            float sp = MathF.Sin(ax), cp = MathF.Cos(ax);
            float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
            float sr = MathF.Sin(az), cr = MathF.Cos(az);

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
        else
        {
            rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;
        }

        rent.ShaderRGBA_R = 255;
        rent.ShaderRGBA_G = 255;
        rent.ShaderRGBA_B = 255;
        rent.ShaderRGBA_A = 255;

        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddMoveScaleFade(ref LocalEntity le, int time)
    {
        float c = (le.EndTime - time) * le.LifeRate;
        if (c > 1) c = 1;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_SPRITE;
        rent.CustomShader = le.CustomShader;

        EvalTrajectory(ref le, time, out rent.OriginX, out rent.OriginY, out rent.OriginZ);
        rent.OldOriginX = rent.OriginX;
        rent.OldOriginY = rent.OriginY;
        rent.OldOriginZ = rent.OriginZ;

        rent.ShaderRGBA_R = (byte)(le.ColorR * 255);
        rent.ShaderRGBA_G = (byte)(le.ColorG * 255);
        rent.ShaderRGBA_B = (byte)(le.ColorB * 255);
        rent.ShaderRGBA_A = (byte)(255 * c);

        rent.Radius = (le.Flags & LeFlags.PuffDontScale) != 0
            ? le.Radius
            : le.Radius * (1.0f - c) + 8;

        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;

        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddFadeRGB(ref LocalEntity le, int time)
    {
        float c = (le.EndTime - time) * le.LifeRate;
        if (c > 1) c = 1;

        Q3RefEntity rent = default;
        rent.ReType = le.ReType;
        rent.HModel = le.HModel;
        rent.CustomShader = le.CustomShader;

        EvalTrajectory(ref le, time, out rent.OriginX, out rent.OriginY, out rent.OriginZ);
        // For rail core beams, use stored old origin (endpoint)
        if (le.OldOriginX != 0 || le.OldOriginY != 0 || le.OldOriginZ != 0)
        {
            rent.OldOriginX = le.OldOriginX;
            rent.OldOriginY = le.OldOriginY;
            rent.OldOriginZ = le.OldOriginZ;
        }
        else
        {
            rent.OldOriginX = rent.OriginX;
            rent.OldOriginY = rent.OriginY;
            rent.OldOriginZ = rent.OriginZ;
        }

        rent.ShaderRGBA_R = (byte)(le.ColorR * c * 255);
        rent.ShaderRGBA_G = (byte)(le.ColorG * c * 255);
        rent.ShaderRGBA_B = (byte)(le.ColorB * c * 255);
        rent.ShaderRGBA_A = (byte)(le.ColorA * c * 255);

        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;

        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddScaleFade(ref LocalEntity le, int time)
    {
        float c = (le.EndTime - time) * le.LifeRate;
        if (c > 1) c = 1;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_SPRITE;
        rent.CustomShader = le.CustomShader;

        rent.OriginX = le.PosBaseX;
        rent.OriginY = le.PosBaseY;
        rent.OriginZ = le.PosBaseZ;
        rent.OldOriginX = rent.OriginX;
        rent.OldOriginY = rent.OriginY;
        rent.OldOriginZ = rent.OriginZ;

        rent.ShaderRGBA_R = (byte)(le.ColorR * 255);
        rent.ShaderRGBA_G = (byte)(le.ColorG * 255);
        rent.ShaderRGBA_B = (byte)(le.ColorB * 255);
        rent.ShaderRGBA_A = (byte)(255 * c);

        rent.Radius = le.Radius * (1.0f - c) + 8;
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;

        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddFallScaleFade(ref LocalEntity le, int time)
    {
        float c = (le.EndTime - time) * le.LifeRate;
        if (c > 1) c = 1;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_SPRITE;
        rent.CustomShader = le.CustomShader;

        EvalTrajectory(ref le, time, out rent.OriginX, out rent.OriginY, out rent.OriginZ);
        rent.OldOriginX = rent.OriginX;
        rent.OldOriginY = rent.OriginY;
        rent.OldOriginZ = rent.OriginZ;

        rent.ShaderRGBA_R = (byte)(le.ColorR * 255);
        rent.ShaderRGBA_G = (byte)(le.ColorG * 255);
        rent.ShaderRGBA_B = (byte)(le.ColorB * 255);
        rent.ShaderRGBA_A = (byte)(255 * c);

        rent.Radius = le.Radius * (1.0f - c) + 16;
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;

        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddScorePlum(ref LocalEntity le, int time)
    {
        if (_numberShaders.Length < 11) return;

        float c = (le.EndTime - time) * le.LifeRate;
        if (c > 1) c = 1;

        // Origin: rise then fall, oscillate laterally
        float ox = le.PosBaseX + MathF.Sin(c * 2 * MathF.PI) * 20;
        float oy = le.PosBaseY;
        float oz = le.PosBaseZ + 110 - c * 100;

        int score = (int)le.Radius;
        bool negative = score < 0;
        if (negative) score = -score;

        // Color by score range
        byte sr, sg, sb;
        if (negative)              { sr = 0xff; sg = 0x11; sb = 0x11; }
        else if (score < 2)        { sr = 0xff; sg = 0xff; sb = 0xff; }
        else if (score < 10)       { sr = 0xff; sg = 0xff; sb = 0x00; }
        else if (score < 20)       { sr = 0x00; sg = 0xff; sb = 0xff; }
        else if (score < 50)       { sr = 0x00; sg = 0x00; sb = 0xff; }
        else                       { sr = 0xff; sg = 0x00; sb = 0x00; }

        const float NUMBER_SIZE = 8;

        // Convert score to digits
        Span<int> digits = stackalloc int[10];
        int numDigits = 0;
        int temp = score;
        do
        {
            digits[numDigits++] = temp % 10;
            temp /= 10;
        } while (temp > 0 && numDigits < 10);

        // Render each digit as a sprite (right to left, then flip)
        float totalWidth = numDigits * NUMBER_SIZE;
        if (negative) totalWidth += NUMBER_SIZE;
        float startX = -(totalWidth / 2);

        for (int i = numDigits - 1; i >= 0; i--)
        {
            Q3RefEntity rent = default;
            rent.ReType = Q3RefEntity.RT_SPRITE;
            rent.CustomShader = _numberShaders[digits[i]];
            rent.Radius = NUMBER_SIZE / 2 + NUMBER_SIZE / 2 * (1.0f - c);
            rent.ShaderRGBA_R = sr; rent.ShaderRGBA_G = sg;
            rent.ShaderRGBA_B = sb; rent.ShaderRGBA_A = (byte)(255 * c);
            rent.OriginX = ox + startX;
            rent.OriginY = oy;
            rent.OriginZ = oz;
            rent.OldOriginX = rent.OriginX;
            rent.OldOriginY = rent.OriginY;
            rent.OldOriginZ = rent.OriginZ;
            rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;
            Syscalls.R_AddRefEntityToScene(&rent);
            startX += NUMBER_SIZE;
        }

        if (negative)
        {
            Q3RefEntity minus = default;
            minus.ReType = Q3RefEntity.RT_SPRITE;
            minus.CustomShader = _numberShaders[10]; // minus sign
            minus.Radius = NUMBER_SIZE / 2 + NUMBER_SIZE / 2 * (1.0f - c);
            minus.ShaderRGBA_R = sr; minus.ShaderRGBA_G = sg;
            minus.ShaderRGBA_B = sb; minus.ShaderRGBA_A = (byte)(255 * c);
            minus.OriginX = ox + startX;
            minus.OriginY = oy;
            minus.OriginZ = oz;
            minus.OldOriginX = minus.OriginX;
            minus.OldOriginY = minus.OriginY;
            minus.OldOriginZ = minus.OriginZ;
            minus.Axis0X = 1; minus.Axis1Y = 1; minus.Axis2Z = 1;
            Syscalls.R_AddRefEntityToScene(&minus);
        }
    }

    // ── Trajectory evaluation for local entities ──

    private static void EvalTrajectory(ref LocalEntity le, int time,
        out float rx, out float ry, out float rz)
    {
        float dt = (time - le.PosTime) * 0.001f;

        switch (le.PosType)
        {
            case TrajectoryType.TR_LINEAR:
                rx = le.PosBaseX + le.PosDeltaX * dt;
                ry = le.PosBaseY + le.PosDeltaY * dt;
                rz = le.PosBaseZ + le.PosDeltaZ * dt;
                break;

            case TrajectoryType.TR_GRAVITY:
                rx = le.PosBaseX + le.PosDeltaX * dt;
                ry = le.PosBaseY + le.PosDeltaY * dt;
                rz = le.PosBaseZ + le.PosDeltaZ * dt - 0.5f * DEFAULT_GRAVITY * dt * dt;
                break;

            default:
                rx = le.PosBaseX;
                ry = le.PosBaseY;
                rz = le.PosBaseZ;
                break;
        }
    }

    // ── Factory methods ──

    /// <summary>Creates a sprite explosion at the given position.</summary>
    public static void MakeExplosion(float x, float y, float z, int shader, int msec,
        float light, float lightR, float lightG, float lightB, int time)
    {
        ref var le = ref Alloc();
        le.Type = LeType.SpriteExplosion;
        int offset = Random.Shared.Next(64);
        le.StartTime = time - offset;
        le.EndTime = le.StartTime + msec;
        le.LifeRate = 1.0f / msec;

        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosBaseX = x;
        le.PosBaseY = y;
        le.PosBaseZ = z;
        le.PosTime = time;

        le.CustomShader = shader;
        le.ShaderTime = le.StartTime;
        le.Rotation = Random.Shared.Next(360);

        le.Light = light;
        le.LightR = lightR;
        le.LightG = lightG;
        le.LightB = lightB;
    }

    /// <summary>Creates a smoke puff that rises and fades.</summary>
    public static void SmokePuff(float x, float y, float z,
        float velX, float velY, float velZ,
        float radius, float r, float g, float b, float a,
        int duration, int time, int shader)
    {
        ref var le = ref Alloc();
        le.Type = LeType.MoveScaleFade;
        le.Flags = LeFlags.PuffDontScale;
        le.StartTime = time;
        le.EndTime = time + duration;
        le.LifeRate = 1.0f / duration;

        le.PosType = TrajectoryType.TR_LINEAR;
        le.PosTime = time;
        le.PosBaseX = x; le.PosBaseY = y; le.PosBaseZ = z;
        le.PosDeltaX = velX; le.PosDeltaY = velY; le.PosDeltaZ = velZ;

        le.ColorR = r; le.ColorG = g; le.ColorB = b; le.ColorA = a;
        le.Radius = radius;
        le.CustomShader = shader != 0 ? shader : _smokePuffShader;
    }

    /// <summary>Creates a floating score plum above a position.</summary>
    public static void MakeScorePlum(float x, float y, float z, int score, int time)
    {
        ref var le = ref Alloc();
        le.Type = LeType.ScorePlum;
        le.StartTime = time;
        le.EndTime = time + 4000;
        le.LifeRate = 1.0f / 4000.0f;

        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosBaseX = x;
        le.PosBaseY = y;
        le.PosBaseZ = z + 16; // offset up
        le.PosTime = time;

        le.Radius = score; // store score in radius
        le.ColorR = 1; le.ColorG = 1; le.ColorB = 1; le.ColorA = 1;
    }
}
