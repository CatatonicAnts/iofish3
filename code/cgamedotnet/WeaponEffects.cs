namespace CGameDotNet;

/// <summary>
/// Weapon effects — projectile trails, muzzle flash, and impact rendering.
/// Equivalent to parts of cg_weapons.c from the C cgame.
/// </summary>
public static unsafe class WeaponEffects
{
    // ── Per-weapon configuration ──
    private struct WeaponInfo
    {
        public int MissileModel;
        public int MissileSound;
        public float MissileDlight;
        public float MissileDlightR, MissileDlightG, MissileDlightB;
        public float FlashDlight;
        public float FlashDlightR, FlashDlightG, FlashDlightB;
        public float TrailRadius;
        public int TrailTime;
        public bool HasTrail;
    }

    private static readonly WeaponInfo[] _weapons = new WeaponInfo[Weapons.WP_NUM_WEAPONS];

    // ── Media ──
    private static int _smokePuffShader;
    private static int _rocketExplosionShader;
    private static int _grenadeExplosionShader;
    private static int _plasmaExplosionShader;
    private static int _railCoreShader;
    private static int _railRingsShader;
    private static int _lightningShader;
    private static int _plasmaShader;
    private static int _lightningExplosionModel;

    // Trail state per entity
    private const int MAX_GENTITIES = 1024;
    private static readonly int[] _trailTime = new int[MAX_GENTITIES];
    private static readonly float[] _trailLastX = new float[MAX_GENTITIES];
    private static readonly float[] _trailLastY = new float[MAX_GENTITIES];
    private static readonly float[] _trailLastZ = new float[MAX_GENTITIES];

    public static void Init()
    {
        Array.Clear(_weapons);
        Array.Clear(_trailTime);

        _smokePuffShader = Syscalls.R_RegisterShader("smokePuff");
        _rocketExplosionShader = Syscalls.R_RegisterShader("rocketExplosion");
        _grenadeExplosionShader = Syscalls.R_RegisterShader("grenadeExplosion");
        _plasmaExplosionShader = Syscalls.R_RegisterShader("plasmaExplosion");
        _railCoreShader = Syscalls.R_RegisterShader("railCore");
        _railRingsShader = Syscalls.R_RegisterShader("railDisc");
        _lightningShader = Syscalls.R_RegisterShader("lightningBoltNew");
        _plasmaShader = Syscalls.R_RegisterShader("sprites/plasma1");
        _lightningExplosionModel = Syscalls.R_RegisterModel("models/weaphits/crackle.md3");

        RegisterWeapons();
        Syscalls.Print("[.NET cgame] Weapon effects initialized\n");
    }

    private static void RegisterWeapons()
    {
        // Rocket launcher
        ref var rocket = ref _weapons[Weapons.WP_ROCKET_LAUNCHER];
        rocket.MissileModel = Syscalls.R_RegisterModel("models/ammo/rocket/rocket.md3");
        rocket.MissileSound = Syscalls.S_RegisterSound("sound/weapons/rocket/rockfly.wav", 0);
        rocket.MissileDlight = 200;
        rocket.MissileDlightR = 1; rocket.MissileDlightG = 0.75f; rocket.MissileDlightB = 0;
        rocket.FlashDlight = 300;
        rocket.FlashDlightR = 1; rocket.FlashDlightG = 0.75f; rocket.FlashDlightB = 0;
        rocket.TrailRadius = 64;
        rocket.TrailTime = 2000;
        rocket.HasTrail = true;

        // Grenade launcher
        ref var grenade = ref _weapons[Weapons.WP_GRENADE_LAUNCHER];
        grenade.MissileModel = Syscalls.R_RegisterModel("models/ammo/grenade1.md3");
        grenade.FlashDlight = 300;
        grenade.FlashDlightR = 1; grenade.FlashDlightG = 0.7f; grenade.FlashDlightB = 0;
        grenade.TrailRadius = 32;
        grenade.TrailTime = 700;
        grenade.HasTrail = true;

        // Plasma gun
        ref var plasma = ref _weapons[Weapons.WP_PLASMAGUN];
        plasma.MissileDlight = 150;
        plasma.MissileDlightR = 0.6f; plasma.MissileDlightG = 0.6f; plasma.MissileDlightB = 1;
        plasma.FlashDlight = 300;
        plasma.FlashDlightR = 0.6f; plasma.FlashDlightG = 0.6f; plasma.FlashDlightB = 1;

        // Lightning gun
        ref var lg = ref _weapons[Weapons.WP_LIGHTNING];
        lg.FlashDlight = 300;
        lg.FlashDlightR = 0.6f; lg.FlashDlightG = 0.6f; lg.FlashDlightB = 1;

        // Railgun
        ref var rail = ref _weapons[Weapons.WP_RAILGUN];
        rail.FlashDlight = 300;
        rail.FlashDlightR = 1; rail.FlashDlightG = 0.5f; rail.FlashDlightB = 0;

        // BFG
        ref var bfg = ref _weapons[Weapons.WP_BFG];
        bfg.MissileModel = Syscalls.R_RegisterModel("models/weaphits/bfg.md3");
        bfg.MissileDlight = 200;
        bfg.MissileDlightR = 1; bfg.MissileDlightG = 0.7f; bfg.MissileDlightB = 1;
        bfg.FlashDlight = 300;
        bfg.FlashDlightR = 1; bfg.FlashDlightG = 0.7f; bfg.FlashDlightB = 1;

        // Machinegun / shotgun — no missile trail
        ref var mg = ref _weapons[Weapons.WP_MACHINEGUN];
        mg.FlashDlight = 300;
        mg.FlashDlightR = 1; mg.FlashDlightG = 1; mg.FlashDlightB = 0;

        ref var sg = ref _weapons[Weapons.WP_SHOTGUN];
        sg.FlashDlight = 300;
        sg.FlashDlightR = 1; sg.FlashDlightG = 1; sg.FlashDlightB = 0;
    }

    /// <summary>Add muzzle flash dynamic light when weapon fires.</summary>
    public static void MuzzleFlash(float x, float y, float z, int weapon, int time)
    {
        if (weapon <= 0 || weapon >= Weapons.WP_NUM_WEAPONS) return;
        ref var wi = ref _weapons[weapon];
        if (wi.FlashDlight <= 0) return;

        float intensity = wi.FlashDlight + (Random.Shared.Next() & 31);
        float* origin = stackalloc float[3];
        origin[0] = x; origin[1] = y; origin[2] = z;
        Syscalls.R_AddLightToScene(origin, intensity,
            wi.FlashDlightR, wi.FlashDlightG, wi.FlashDlightB);
    }

    /// <summary>
    /// Add projectile trail effect (smoke puffs for rockets/grenades).
    /// Called each frame for each visible missile entity.
    /// </summary>
    public static void MissileTrail(int entityNum, int weapon,
        float ox, float oy, float oz, float nx, float ny, float nz, int time)
    {
        if (weapon <= 0 || weapon >= Weapons.WP_NUM_WEAPONS) return;
        ref var wi = ref _weapons[weapon];

        // Projectile dynamic light
        if (wi.MissileDlight > 0)
        {
            float* dlOrigin = stackalloc float[3];
            dlOrigin[0] = nx; dlOrigin[1] = ny; dlOrigin[2] = nz;
            Syscalls.R_AddLightToScene(dlOrigin, wi.MissileDlight,
                wi.MissileDlightR, wi.MissileDlightG, wi.MissileDlightB);
        }

        // Looping flight sound
        if (wi.MissileSound != 0)
        {
            float* sndOrigin = stackalloc float[3];
            sndOrigin[0] = nx; sndOrigin[1] = ny; sndOrigin[2] = nz;
            float* velocity = stackalloc float[3];
            velocity[0] = 0; velocity[1] = 0; velocity[2] = 0;
            Syscalls.S_AddLoopingSound(entityNum, sndOrigin, velocity, wi.MissileSound);
        }

        if (!wi.HasTrail) return;

        // Initialize trail position on first frame
        if (_trailTime[entityNum] == 0)
        {
            _trailTime[entityNum] = time;
            _trailLastX[entityNum] = ox;
            _trailLastY[entityNum] = oy;
            _trailLastZ[entityNum] = oz;
            return;
        }

        // Spawn smoke puffs along trail path at ~50ms intervals
        float lx = _trailLastX[entityNum];
        float ly = _trailLastY[entityNum];
        float lz = _trailLastZ[entityNum];

        float dx = nx - lx, dy = ny - ly, dz = nz - lz;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1) return;

        float step = 50.0f; // units between puffs
        int numSteps = (int)(dist / step);
        if (numSteps < 1) numSteps = 1;
        if (numSteps > 20) numSteps = 20;

        float invDist = 1.0f / dist;
        dx *= invDist; dy *= invDist; dz *= invDist;

        for (int i = 0; i < numSteps; i++)
        {
            float t = (i + 1.0f) / numSteps;
            float px = lx + (nx - lx) * t;
            float py = ly + (ny - ly) * t;
            float pz = lz + (nz - lz) * t;

            LocalEntities.SmokePuff(px, py, pz,
                0, 0, 20, // slight upward drift
                wi.TrailRadius, 1, 1, 1, 0.33f,
                wi.TrailTime, time, _smokePuffShader);
        }

        _trailLastX[entityNum] = nx;
        _trailLastY[entityNum] = ny;
        _trailLastZ[entityNum] = nz;
        _trailTime[entityNum] = time;
    }

    /// <summary>Render plasma bolt as sprite.</summary>
    public static void AddPlasma(float x, float y, float z, int time)
    {
        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_SPRITE;
        rent.CustomShader = _plasmaShader;
        rent.OriginX = x; rent.OriginY = y; rent.OriginZ = z;
        rent.OldOriginX = x; rent.OldOriginY = y; rent.OldOriginZ = z;
        rent.Radius = 16;
        rent.Rotation = time / 4.0f;
        rent.ShaderRGBA_R = 255; rent.ShaderRGBA_G = 255;
        rent.ShaderRGBA_B = 255; rent.ShaderRGBA_A = 255;
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;
        Syscalls.R_AddRefEntityToScene(&rent);
    }

    /// <summary>Render lightning beam between two points.</summary>
    public static void LightningBolt(float sx, float sy, float sz,
        float ex, float ey, float ez)
    {
        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_LIGHTNING;
        rent.CustomShader = _lightningShader;
        rent.OriginX = sx; rent.OriginY = sy; rent.OriginZ = sz;
        rent.OldOriginX = ex; rent.OldOriginY = ey; rent.OldOriginZ = ez;
        rent.ShaderRGBA_R = 255; rent.ShaderRGBA_G = 255;
        rent.ShaderRGBA_B = 255; rent.ShaderRGBA_A = 255;
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;
        Syscalls.R_AddRefEntityToScene(&rent);
    }

    /// <summary>Render rail trail beam between two points.</summary>
    public static void RailTrail(float sx, float sy, float sz,
        float ex, float ey, float ez, int time)
    {
        // Core beam as a fading local entity
        ref var le = ref LocalEntities.Alloc();
        le.Type = LocalEntities.LeType.FadeRGB;
        le.StartTime = time;
        le.EndTime = time + 1200; // rail trail lingers ~1.2s
        le.LifeRate = 1.0f / 1200.0f;
        le.ReType = Q3RefEntity.RT_RAIL_CORE;
        le.CustomShader = _railCoreShader;
        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosBaseX = sx; le.PosBaseY = sy; le.PosBaseZ = sz;
        le.ColorR = 1; le.ColorG = 0.5f; le.ColorB = 0; le.ColorA = 1;

        // Store end point in origin (we'll need special handling)
        // For rail core, the FadeRGB renderer uses RT_RAIL_CORE
        // which needs oldorigin. Since LocalEntities doesn't support
        // oldorigin directly, render it immediately instead.
        le.Active = false; // cancel the local entity approach

        // Render immediately as a direct refEntity (will show for one frame,
        // but that's acceptable for now)
        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_RAIL_CORE;
        rent.CustomShader = _railCoreShader;
        rent.OriginX = sx; rent.OriginY = sy; rent.OriginZ = sz;
        rent.OldOriginX = ex; rent.OldOriginY = ey; rent.OldOriginZ = ez;
        rent.ShaderRGBA_R = 255; rent.ShaderRGBA_G = 128; rent.ShaderRGBA_B = 0; rent.ShaderRGBA_A = 255;
        rent.Axis0X = 1; rent.Axis1Y = 1; rent.Axis2Z = 1;
        Syscalls.R_AddRefEntityToScene(&rent);
    }

    /// <summary>Reset trail state for an entity (on death/removal).</summary>
    public static void ResetTrail(int entityNum)
    {
        if (entityNum >= 0 && entityNum < MAX_GENTITIES)
            _trailTime[entityNum] = 0;
    }

    // Expose shaders for event handlers
    public static int RocketExplosionShader => _rocketExplosionShader;
    public static int GrenadeExplosionShader => _grenadeExplosionShader;
    public static int PlasmaExplosionShader => _plasmaExplosionShader;
    public static int LightningExplosionModel => _lightningExplosionModel;
}
