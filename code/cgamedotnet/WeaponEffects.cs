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

        // First-person view models
        public int WeaponModel;
        public int HandsModel;
        public int BarrelModel;
        public int FlashModel;

        // Firing sounds (up to 4 variants)
        public int FlashSound0, FlashSound1, FlashSound2, FlashSound3;
        public int FlashSoundCount;
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

    // Impact media
    private static int _bulletFlashModel;
    private static int _dishFlashModel;
    private static int _ringFlashModel;
    private static int _bulletExplosionShader;
    private static int _railExplosionShader;
    private static int _bfgExplosionShader;
    private static int _bulletMarkShader;
    private static int _burnMarkShader;
    private static int _holeMarkShader;
    private static int _energyMarkShader;
    private static int _bloodExplosionShader;
    private static int _bloodMarkShader;

    // Impact sounds
    private static int _sfxRockExp;
    private static int _sfxPlasmaExp;
    private static int _sfxRic1, _sfxRic2, _sfxRic3;
    private static int _sfxLgHit1, _sfxLgHit2, _sfxLgHit3;

    // Additional media
    private static int _waterBubbleShader;
    private static int _teleportEffectModel;
    private static int _teleportEffectShader;
    private static int _shotgunSmokePuffShader;
    private static int _selectSound;

    // Gib media
    private static int _gibSkull;
    private static int _gibBrain;
    private static int _gibAbdomen;
    private static int _gibArm;
    private static int _gibChest;
    private static int _gibFist;
    private static int _gibFoot;
    private static int _gibForearm;
    private static int _gibIntestine;
    private static int _gibLeg;
    private static int _gibSplash;

    // Brass models
    private static int _machinegunBrassModel;
    private static int _shotgunBrassModel;

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

        // Impact models and shaders
        _bulletFlashModel = Syscalls.R_RegisterModel("models/weaphits/bullet.md3");
        _dishFlashModel = Syscalls.R_RegisterModel("models/weaphits/boom01.md3");
        _ringFlashModel = Syscalls.R_RegisterModel("models/weaphits/ring02.md3");
        _bulletExplosionShader = Syscalls.R_RegisterShader("bulletExplosion");
        _railExplosionShader = Syscalls.R_RegisterShader("railExplosion");
        _bfgExplosionShader = Syscalls.R_RegisterShader("bfgExplosion");
        _bulletMarkShader = Syscalls.R_RegisterShader("gfx/damage/bullet_mrk");
        _burnMarkShader = Syscalls.R_RegisterShader("gfx/damage/burn_med_mrk");
        _holeMarkShader = Syscalls.R_RegisterShader("gfx/damage/hole_lg_mrk");
        _energyMarkShader = Syscalls.R_RegisterShader("gfx/damage/plasma_mrk");
        _bloodExplosionShader = Syscalls.R_RegisterShader("bloodExplosion");
        _bloodMarkShader = Syscalls.R_RegisterShader("bloodMark");

        // Impact sounds
        _sfxRockExp = Syscalls.S_RegisterSound("sound/weapons/rocket/rocklx1a.wav", 0);
        _sfxPlasmaExp = Syscalls.S_RegisterSound("sound/weapons/plasma/plasmx1a.wav", 0);
        _sfxRic1 = Syscalls.S_RegisterSound("sound/weapons/machinegun/ric1.wav", 0);
        _sfxRic2 = Syscalls.S_RegisterSound("sound/weapons/machinegun/ric2.wav", 0);
        _sfxRic3 = Syscalls.S_RegisterSound("sound/weapons/machinegun/ric3.wav", 0);
        _sfxLgHit1 = Syscalls.S_RegisterSound("sound/weapons/lightning/lg_hit.wav", 0);
        _sfxLgHit2 = Syscalls.S_RegisterSound("sound/weapons/lightning/lg_hit2.wav", 0);
        _sfxLgHit3 = Syscalls.S_RegisterSound("sound/weapons/lightning/lg_hit3.wav", 0);

        // Gib models
        _gibSkull = Syscalls.R_RegisterModel("models/gibs/skull.md3");
        _gibBrain = Syscalls.R_RegisterModel("models/gibs/brain.md3");
        _gibAbdomen = Syscalls.R_RegisterModel("models/gibs/abdomen.md3");
        _gibArm = Syscalls.R_RegisterModel("models/gibs/arm.md3");
        _gibChest = Syscalls.R_RegisterModel("models/gibs/chest.md3");
        _gibFist = Syscalls.R_RegisterModel("models/gibs/fist.md3");
        _gibFoot = Syscalls.R_RegisterModel("models/gibs/foot.md3");
        _gibForearm = Syscalls.R_RegisterModel("models/gibs/forearm.md3");
        _gibIntestine = Syscalls.R_RegisterModel("models/gibs/intestine.md3");
        _gibLeg = Syscalls.R_RegisterModel("models/gibs/leg.md3");
        _gibSplash = Syscalls.S_RegisterSound("sound/gibs/splat.wav", 0);

        // Brass models
        _machinegunBrassModel = Syscalls.R_RegisterModel("models/weapons2/shells/m_shell.md3");
        _shotgunBrassModel = Syscalls.R_RegisterModel("models/weapons2/shells/s_shell.md3");

        // Additional media
        _waterBubbleShader = Syscalls.R_RegisterShader("waterBubble");
        _teleportEffectModel = Syscalls.R_RegisterModel("models/misc/telep.md3");
        _teleportEffectShader = Syscalls.R_RegisterShader("teleportEffect");
        _shotgunSmokePuffShader = Syscalls.R_RegisterShader("shotgunSmokePuff");
        _selectSound = Syscalls.S_RegisterSound("sound/weapons/change.wav", 0);

        RegisterWeapons();
        Syscalls.Print("[.NET cgame] Weapon effects initialized\n");
    }

    private static void RegisterWeapons()
    {
        // Gauntlet
        ref var gauntlet = ref _weapons[Weapons.WP_GAUNTLET];
        gauntlet.FlashDlight = 300;
        gauntlet.FlashDlightR = 1; gauntlet.FlashDlightG = 1; gauntlet.FlashDlightB = 1;
        gauntlet.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/melee/fstatck.wav", 0);
        gauntlet.FlashSoundCount = 1;

        // Machinegun
        ref var mg = ref _weapons[Weapons.WP_MACHINEGUN];
        mg.FlashDlight = 300;
        mg.FlashDlightR = 1; mg.FlashDlightG = 1; mg.FlashDlightB = 0;
        mg.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf1b.wav", 0);
        mg.FlashSound1 = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf2b.wav", 0);
        mg.FlashSound2 = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf3b.wav", 0);
        mg.FlashSound3 = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf4b.wav", 0);
        mg.FlashSoundCount = 4;

        // Shotgun
        ref var sg = ref _weapons[Weapons.WP_SHOTGUN];
        sg.FlashDlight = 300;
        sg.FlashDlightR = 1; sg.FlashDlightG = 1; sg.FlashDlightB = 0;
        sg.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/shotgun/sshotf1b.wav", 0);
        sg.FlashSoundCount = 1;

        // Grenade launcher
        ref var grenade = ref _weapons[Weapons.WP_GRENADE_LAUNCHER];
        grenade.MissileModel = Syscalls.R_RegisterModel("models/ammo/grenade1.md3");
        grenade.FlashDlight = 300;
        grenade.FlashDlightR = 1; grenade.FlashDlightG = 0.7f; grenade.FlashDlightB = 0;
        grenade.TrailRadius = 32;
        grenade.TrailTime = 700;
        grenade.HasTrail = true;
        grenade.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/grenade/grenlf1a.wav", 0);
        grenade.FlashSoundCount = 1;

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
        rocket.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/rocket/rocklf1a.wav", 0);
        rocket.FlashSoundCount = 1;

        // Lightning gun
        ref var lg = ref _weapons[Weapons.WP_LIGHTNING];
        lg.FlashDlight = 300;
        lg.FlashDlightR = 0.6f; lg.FlashDlightG = 0.6f; lg.FlashDlightB = 1;
        lg.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/lightning/lg_fire.wav", 0);
        lg.FlashSoundCount = 1;

        // Railgun
        ref var rail = ref _weapons[Weapons.WP_RAILGUN];
        rail.FlashDlight = 300;
        rail.FlashDlightR = 1; rail.FlashDlightG = 0.5f; rail.FlashDlightB = 0;
        rail.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/railgun/railgf1a.wav", 0);
        rail.FlashSoundCount = 1;

        // Plasma gun
        ref var plasma = ref _weapons[Weapons.WP_PLASMAGUN];
        plasma.MissileDlight = 150;
        plasma.MissileDlightR = 0.6f; plasma.MissileDlightG = 0.6f; plasma.MissileDlightB = 1;
        plasma.FlashDlight = 300;
        plasma.FlashDlightR = 0.6f; plasma.FlashDlightG = 0.6f; plasma.FlashDlightB = 1;
        plasma.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/plasma/hyprbf1a.wav", 0);
        plasma.FlashSoundCount = 1;

        // BFG
        ref var bfg = ref _weapons[Weapons.WP_BFG];
        bfg.MissileModel = Syscalls.R_RegisterModel("models/weaphits/bfg.md3");
        bfg.MissileDlight = 200;
        bfg.MissileDlightR = 1; bfg.MissileDlightG = 0.7f; bfg.MissileDlightB = 1;
        bfg.FlashDlight = 300;
        bfg.FlashDlightR = 1; bfg.FlashDlightG = 0.7f; bfg.FlashDlightB = 1;
        bfg.FlashSound0 = Syscalls.S_RegisterSound("sound/weapons/bfg/bfg_fire.wav", 0);
        bfg.FlashSoundCount = 1;

        // Register first-person view models for each weapon
        RegisterViewModels(Weapons.WP_GAUNTLET, "models/weapons2/gauntlet/gauntlet");
        RegisterViewModels(Weapons.WP_MACHINEGUN, "models/weapons2/machinegun/machinegun");
        RegisterViewModels(Weapons.WP_SHOTGUN, "models/weapons2/shotgun/shotgun");
        RegisterViewModels(Weapons.WP_GRENADE_LAUNCHER, "models/weapons2/grenadel/grenadel");
        RegisterViewModels(Weapons.WP_ROCKET_LAUNCHER, "models/weapons2/rocketl/rocketl");
        RegisterViewModels(Weapons.WP_LIGHTNING, "models/weapons2/lightning/lightning");
        RegisterViewModels(Weapons.WP_RAILGUN, "models/weapons2/railgun/railgun");
        RegisterViewModels(Weapons.WP_PLASMAGUN, "models/weapons2/plasma/plasma");
        RegisterViewModels(Weapons.WP_BFG, "models/weapons2/bfg/bfg");
    }

    private static void RegisterViewModels(int weapon, string basePath)
    {
        ref var wi = ref _weapons[weapon];
        wi.WeaponModel = Syscalls.R_RegisterModel(basePath + ".md3");
        wi.HandsModel = Syscalls.R_RegisterModel(basePath + "_hand.md3");
        if (wi.HandsModel == 0)
            wi.HandsModel = Syscalls.R_RegisterModel("models/weapons2/shotgun/shotgun_hand.md3");
        wi.BarrelModel = Syscalls.R_RegisterModel(basePath + "_barrel.md3");
        wi.FlashModel = Syscalls.R_RegisterModel(basePath + "_flash.md3");
    }

    /// <summary>Get weapon model handle for first-person rendering.</summary>
    public static int GetWeaponModel(int weapon) =>
        (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS) ? _weapons[weapon].WeaponModel : 0;

    /// <summary>Get hand model handle for first-person rendering.</summary>
    public static int GetHandsModel(int weapon) =>
        (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS) ? _weapons[weapon].HandsModel : 0;

    /// <summary>Get barrel model handle for first-person rendering.</summary>
    public static int GetBarrelModel(int weapon) =>
        (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS) ? _weapons[weapon].BarrelModel : 0;

    /// <summary>Get missile model handle for projectile rendering.</summary>
    public static int GetMissileModel(int weapon) =>
        (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS) ? _weapons[weapon].MissileModel : 0;

    /// <summary>Get flash model handle for first-person rendering.</summary>
    public static int GetFlashModel(int weapon) =>
        (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS) ? _weapons[weapon].FlashModel : 0;

    /// <summary>Add muzzle flash dynamic light at weapon position.</summary>
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

        // Time-based smoke puff spawning at 50ms intervals (matches original CG_RocketTrail)
        int step = 50;
        int startTime = _trailTime[entityNum];
        int t = step * ((startTime + step) / step); // round up to next step boundary

        _trailTime[entityNum] = time;

        if (t > time)
            return; // no new time bucket has elapsed

        // Get last position and current position for interpolation
        float lx = _trailLastX[entityNum];
        float ly = _trailLastY[entityNum];
        float lz = _trailLastZ[entityNum];

        _trailLastX[entityNum] = nx;
        _trailLastY[entityNum] = ny;
        _trailLastZ[entityNum] = nz;

        int duration = time - startTime;
        if (duration <= 0) return;

        int count = 0;
        for (; t <= time; t += step)
        {
            float frac = (float)(t - startTime) / duration;
            float px = lx + (nx - lx) * frac;
            float py = ly + (ny - ly) * frac;
            float pz = lz + (nz - lz) * frac;

            LocalEntities.SmokePuff(px, py, pz,
                0, 0, 20,
                wi.TrailRadius, 1, 1, 1, 0.33f,
                wi.TrailTime, t, _smokePuffShader);

            if (++count >= 20) break; // safety cap
        }
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
        le.EndTime = time + 1200;
        le.LifeRate = 1.0f / 1200.0f;
        le.ReType = Q3RefEntity.RT_RAIL_CORE;
        le.CustomShader = _railCoreShader;
        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosBaseX = sx; le.PosBaseY = sy; le.PosBaseZ = sz - 4;
        le.OldOriginX = ex; le.OldOriginY = ey; le.OldOriginZ = ez;
        le.ColorR = 1; le.ColorG = 0.5f; le.ColorB = 0; le.ColorA = 1;

        // Rail rings along the beam path
        float dx = ex - sx, dy = ey - sy, dz = (ez) - (sz - 4);
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1) return;
        float invLen = 1.0f / len;
        dx *= invLen; dy *= invLen; dz *= invLen;

        // Perpendicular vector for ring positions
        float perpX, perpY, perpZ;
        if (MathF.Abs(dx) < 0.9f) { perpX = 0; perpY = -dz; perpZ = dy; }
        else { perpX = -dz; perpY = 0; perpZ = dx; }
        float plen = MathF.Sqrt(perpX * perpX + perpY * perpY + perpZ * perpZ);
        if (plen > 0) { perpX /= plen; perpY /= plen; perpZ /= plen; }

        const float SPACING = 5;
        const float RADIUS = 4;
        float mx = sx + dx * 20, my = sy + dy * 20, mz = (sz - 4) + dz * 20;
        int j = 18;
        for (float i = 0; i < len; i += SPACING)
        {
            float angle = j * 10 * MathF.PI / 180.0f;
            float ca = MathF.Cos(angle), sa = MathF.Sin(angle);

            // Rotate perp around the beam direction
            float rx = perpX * ca + (dy * perpZ - dz * perpY) * sa;
            float ry = perpY * ca + (dz * perpX - dx * perpZ) * sa;
            float rz = perpZ * ca + (dx * perpY - dy * perpX) * sa;

            ref var ring = ref LocalEntities.Alloc();
            ring.Type = LocalEntities.LeType.MoveScaleFade;
            ring.Flags = LocalEntities.LeFlags.PuffDontScale;
            ring.StartTime = time;
            ring.EndTime = time + (int)(i / 2) + 600;
            ring.LifeRate = 1.0f / (ring.EndTime - ring.StartTime);
            ring.PosType = TrajectoryType.TR_LINEAR;
            ring.PosTime = time;
            ring.PosBaseX = mx + rx * RADIUS;
            ring.PosBaseY = my + ry * RADIUS;
            ring.PosBaseZ = mz + rz * RADIUS;
            ring.PosDeltaX = rx * 6;
            ring.PosDeltaY = ry * 6;
            ring.PosDeltaZ = rz * 6;
            ring.CustomShader = _railRingsShader;
            ring.Radius = 1.1f;
            ring.ShaderTime = time / 1000.0f;
            ring.ColorR = 1; ring.ColorG = 0.6f; ring.ColorB = 0; ring.ColorA = 1;

            mx += dx * SPACING;
            my += dy * SPACING;
            mz += dz * SPACING;
            j = (j + 1) % 36;
        }
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
    public static int SelectSound => _selectSound;
    public static int LightningShader => _lightningShader;

    // ── Impact Sound Type ──
    public const int IMPACTSOUND_DEFAULT = 0;
    public const int IMPACTSOUND_METAL = 1;
    public const int IMPACTSOUND_FLESH = 2;

    // ── Surface/Content Flags ──
    private const int CONTENTS_SOLID = 1;
    private const int CONTENTS_WATER = 32;
    private const int CONTENTS_BODY = 0x2000000;
    private const int CONTENTS_CORPSE = 0x4000000;
    private const int MASK_SHOT = CONTENTS_SOLID | CONTENTS_BODY | CONTENTS_CORPSE;
    private const int MASK_WATER = CONTENTS_WATER | 8 | 16; // water|lava|slime
    private const int SURF_NOIMPACT = 0x10;
    private const int SURF_METALSTEPS = 0x1000;

    // Shotgun spread constants
    private const int DEFAULT_SHOTGUN_COUNT = 11;
    private const int DEFAULT_SHOTGUN_SPREAD = 700;

    // Gib constants
    private const int GIB_VELOCITY = 250;
    private const int GIB_JUMP = 250;

    // ── MissileHitWall ──

    /// <summary>
    /// Per-weapon impact effect: explosion sprite + decal mark + sound + dynamic light.
    /// Matches CG_MissileHitWall from cg_weapons.c.
    /// </summary>
    public static void MissileHitWall(int weapon, int clientNum,
        float ox, float oy, float oz, float dx, float dy, float dz,
        int soundType, int time)
    {
        int mod = 0, shader = 0, mark = 0, sfx = 0;
        float radius = 0, light = 0;
        float lightR = 1, lightG = 1, lightB = 0;
        bool isSprite = false;
        int duration = 600;
        bool alphaFade;

        switch (weapon)
        {
            case Weapons.WP_LIGHTNING:
            {
                int r = Random.Shared.Next(4);
                sfx = r < 2 ? _sfxLgHit2 : r == 2 ? _sfxLgHit1 : _sfxLgHit3;
                mark = _holeMarkShader;
                radius = 12;
                break;
            }

            case Weapons.WP_GRENADE_LAUNCHER:
                mod = _dishFlashModel;
                shader = _grenadeExplosionShader;
                sfx = _sfxRockExp;
                mark = _burnMarkShader;
                radius = 64;
                light = 300;
                isSprite = true;
                break;

            case Weapons.WP_ROCKET_LAUNCHER:
                mod = _dishFlashModel;
                shader = _rocketExplosionShader;
                sfx = _sfxRockExp;
                mark = _burnMarkShader;
                radius = 64;
                light = 300;
                isSprite = true;
                duration = 1000;
                lightR = 1; lightG = 0.75f; lightB = 0;
                break;

            case Weapons.WP_RAILGUN:
                mod = _ringFlashModel;
                shader = _railExplosionShader;
                sfx = _sfxPlasmaExp;
                mark = _energyMarkShader;
                radius = 24;
                break;

            case Weapons.WP_PLASMAGUN:
                mod = _ringFlashModel;
                shader = _plasmaExplosionShader;
                sfx = _sfxPlasmaExp;
                mark = _energyMarkShader;
                radius = 16;
                break;

            case Weapons.WP_BFG:
                mod = _dishFlashModel;
                shader = _bfgExplosionShader;
                sfx = _sfxRockExp;
                mark = _burnMarkShader;
                radius = 32;
                isSprite = true;
                break;

            case Weapons.WP_SHOTGUN:
                mod = _bulletFlashModel;
                shader = _bulletExplosionShader;
                mark = _bulletMarkShader;
                sfx = 0;
                radius = 4;
                break;

            case Weapons.WP_MACHINEGUN:
                mod = _bulletFlashModel;
                shader = _bulletExplosionShader;
                mark = _bulletMarkShader;
                radius = 8;
                {
                    int r = Random.Shared.Next(3);
                    sfx = r == 0 ? _sfxRic1 : r == 1 ? _sfxRic2 : _sfxRic3;
                }
                break;

            default:
                mark = _holeMarkShader;
                radius = 12;
                sfx = 0;
                break;
        }

        // Play impact sound
        if (sfx != 0)
        {
            float* sndOrigin = stackalloc float[3];
            sndOrigin[0] = ox; sndOrigin[1] = oy; sndOrigin[2] = oz;
            Syscalls.S_StartSound(sndOrigin, EntityNum.ENTITYNUM_WORLD, SoundChannel.CHAN_AUTO, sfx);
        }

        // Create explosion
        if (mod != 0)
        {
            if (isSprite)
            {
                // Offset origin along dir for sprite explosions
                float ex = ox + dx * 16;
                float ey = oy + dy * 16;
                float ez = oz + dz * 16;
                LocalEntities.MakeExplosion(ex, ey, ez, shader, duration,
                    light, lightR, lightG, lightB, time);
            }
            else
            {
                LocalEntities.MakeExplosion(ox, oy, oz, shader, duration,
                    light, lightR, lightG, lightB, time);
            }
        }

        // Impact mark
        alphaFade = (mark == _energyMarkShader);
        if (mark != 0 && radius > 0)
        {
            Marks.ImpactMark(mark, ox, oy, oz, dx, dy, dz, radius,
                1, 1, 1, 1, alphaFade, false);
        }
    }

    // ── MissileHitPlayer ──

    /// <summary>
    /// Blood spray + optional splash explosion when a missile hits a player.
    /// Matches CG_MissileHitPlayer from cg_weapons.c.
    /// </summary>
    public static void MissileHitPlayer(int weapon,
        float ox, float oy, float oz, float dx, float dy, float dz,
        int entityNum, int time)
    {
        Bleed(ox, oy, oz, entityNum, time);

        // Splash damage weapons also create a wall-hit explosion
        if (weapon == Weapons.WP_GRENADE_LAUNCHER ||
            weapon == Weapons.WP_ROCKET_LAUNCHER)
        {
            MissileHitWall(weapon, 0, ox, oy, oz, dx, dy, dz, IMPACTSOUND_FLESH, time);
        }
    }

    // ── Bleed ──

    /// <summary>
    /// Blood spray sprite explosion on hit.
    /// Matches CG_Bleed from cg_effects.c.
    /// </summary>
    public static void Bleed(float ox, float oy, float oz, int entityNum, int time)
    {
        if (_bloodExplosionShader == 0) return;

        ref var le = ref LocalEntities.Alloc();
        le.Type = LocalEntities.LeType.SpriteExplosion;
        le.StartTime = time;
        le.EndTime = time + 500;
        le.LifeRate = 1.0f / 500.0f;

        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosBaseX = ox; le.PosBaseY = oy; le.PosBaseZ = oz;
        le.PosTime = time;

        le.CustomShader = _bloodExplosionShader;
        le.ShaderTime = time / 1000.0f;
        le.Rotation = Random.Shared.Next(360);
        le.Radius = 24;
    }

    // ── Bullet ──

    /// <summary>
    /// Bullet impact effect — blood or wall mark + ricochet sound.
    /// Matches CG_Bullet from cg_weapons.c.
    /// </summary>
    public static void Bullet(float ex, float ey, float ez,
        int sourceEntityNum,
        float nx, float ny, float nz,
        bool flesh, int fleshEntityNum, int time)
    {
        if (flesh)
        {
            Bleed(ex, ey, ez, fleshEntityNum, time);
        }
        else
        {
            MissileHitWall(Weapons.WP_MACHINEGUN, 0, ex, ey, ez, nx, ny, nz,
                IMPACTSOUND_DEFAULT, time);
        }
    }

    // ── ShotgunFire ──

    // Q3-compatible deterministic random for shotgun spread
    private static int Q_rand(ref int seed)
    {
        seed = (int)(69069U * (uint)seed + 1U);
        return seed;
    }

    private static float Q_random(ref int seed)
    {
        return (Q_rand(ref seed) & 0xffff) / (float)0x10000;
    }

    private static float Q_crandom(ref int seed)
    {
        return 2.0f * (Q_random(ref seed) - 0.5f);
    }

    /// <summary>
    /// Render shotgun pellet impacts. Matches CG_ShotgunFire from cg_weapons.c.
    /// Uses the deterministic Q3 spread pattern from CG_ShotgunPattern.
    /// </summary>
    public static void ShotgunFire(
        float baseX, float baseY, float baseZ,
        float origin2X, float origin2Y, float origin2Z,
        int seed, int otherEntNum, int time)
    {
        // Smoke puff at muzzle
        float vx = origin2X - baseX;
        float vy = origin2Y - baseY;
        float vz = origin2Z - baseZ;
        float vlen = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        if (vlen > 0.001f)
        {
            float inv = 32.0f / vlen;
            vx *= inv; vy *= inv; vz *= inv;
        }
        float puffX = baseX + vx;
        float puffY = baseY + vy;
        float puffZ = baseZ + vz;

        if (_shotgunSmokePuffShader != 0)
        {
            LocalEntities.SmokePuff(puffX, puffY, puffZ,
                0, 0, 8, 32, 1, 1, 1, 0.33f, 900, time, _shotgunSmokePuffShader);
        }

        // Derive forward/right/up from direction
        float fx = origin2X - baseX;
        float fy = origin2Y - baseY;
        float fz = origin2Z - baseZ;
        float flen = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        if (flen > 0.001f) { float fi = 1.0f / flen; fx *= fi; fy *= fi; fz *= fi; }

        // PerpendicularVector
        float rx, ry, rz;
        {
            float ax = MathF.Abs(fx), ay = MathF.Abs(fy), az = MathF.Abs(fz);
            float tx, ty, tz;
            if (ax <= ay && ax <= az) { tx = 1; ty = 0; tz = 0; }
            else if (ay <= ax && ay <= az) { tx = 0; ty = 1; tz = 0; }
            else { tx = 0; ty = 0; tz = 1; }
            // right = cross(forward, temp)
            float d = fx * tx + fy * ty + fz * tz;
            tx -= fx * d; ty -= fy * d; tz -= fz * d;
            float tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tl > 0.001f) { float ti = 1.0f / tl; tx *= ti; ty *= ti; tz *= ti; }
            rx = tx; ry = ty; rz = tz;
        }
        // up = cross(forward, right)
        float ux = fy * rz - fz * ry;
        float uy = fz * rx - fx * rz;
        float uz = fx * ry - fy * rx;

        for (int i = 0; i < DEFAULT_SHOTGUN_COUNT; i++)
        {
            float r = Q_crandom(ref seed) * DEFAULT_SHOTGUN_SPREAD * 16;
            float u = Q_crandom(ref seed) * DEFAULT_SHOTGUN_SPREAD * 16;

            float endX = baseX + fx * 8192 * 16 + rx * r + ux * u;
            float endY = baseY + fy * 8192 * 16 + ry * r + uy * u;
            float endZ = baseZ + fz * 8192 * 16 + rz * r + uz * u;

            ShotgunPellet(baseX, baseY, baseZ, endX, endY, endZ, otherEntNum, time);
        }
    }

    private static void ShotgunPellet(
        float sx, float sy, float sz,
        float ex, float ey, float ez,
        int skipNum, int time)
    {
        float* start = stackalloc float[3]; start[0] = sx; start[1] = sy; start[2] = sz;
        float* end = stackalloc float[3]; end[0] = ex; end[1] = ey; end[2] = ez;

        Q3Trace tr;
        Prediction.Trace(&tr, start, null, null, end, skipNum, MASK_SHOT);

        if ((tr.SurfaceFlags & SURF_NOIMPACT) != 0) return;

        if (tr.EntityNum < MAX_GENTITIES && CGame.GetEntityType(tr.EntityNum) == EntityType.ET_PLAYER)
        {
            MissileHitPlayer(Weapons.WP_SHOTGUN,
                tr.EndPosX, tr.EndPosY, tr.EndPosZ,
                tr.PlaneNormalX, tr.PlaneNormalY, tr.PlaneNormalZ,
                tr.EntityNum, time);
        }
        else
        {
            int sndType = (tr.SurfaceFlags & SURF_METALSTEPS) != 0
                ? IMPACTSOUND_METAL : IMPACTSOUND_DEFAULT;
            MissileHitWall(Weapons.WP_SHOTGUN, 0,
                tr.EndPosX, tr.EndPosY, tr.EndPosZ,
                tr.PlaneNormalX, tr.PlaneNormalY, tr.PlaneNormalZ,
                sndType, time);
        }
    }

    // ── GibPlayer ──

    /// <summary>
    /// Launch gib models with fragment physics.
    /// Matches CG_GibPlayer from cg_effects.c.
    /// </summary>
    public static void GibPlayer(float ox, float oy, float oz, int time)
    {
        // Skull or brain
        int headModel = (Random.Shared.Next(2) == 0) ? _gibSkull : _gibBrain;
        LaunchGib(ox, oy, oz, headModel, time);

        // Remaining gibs
        LaunchGib(ox, oy, oz, _gibAbdomen, time);
        LaunchGib(ox, oy, oz, _gibArm, time);
        LaunchGib(ox, oy, oz, _gibChest, time);
        LaunchGib(ox, oy, oz, _gibFist, time);
        LaunchGib(ox, oy, oz, _gibFoot, time);
        LaunchGib(ox, oy, oz, _gibForearm, time);
        LaunchGib(ox, oy, oz, _gibIntestine, time);
        LaunchGib(ox, oy, oz, _gibLeg, time);
        LaunchGib(ox, oy, oz, _gibLeg, time); // 2 legs
    }

    private static void LaunchGib(float ox, float oy, float oz, int model, int time)
    {
        if (model == 0) return;

        ref var le = ref LocalEntities.Alloc();
        le.Type = LocalEntities.LeType.Fragment;
        le.Flags = LocalEntities.LeFlags.Tumble;
        le.StartTime = time;
        le.EndTime = time + 5000 + Random.Shared.Next(3000);
        le.LifeRate = 1.0f / (le.EndTime - le.StartTime);

        le.PosType = TrajectoryType.TR_GRAVITY;
        le.PosTime = time;
        le.PosBaseX = ox; le.PosBaseY = oy; le.PosBaseZ = oz;
        le.PosDeltaX = CRandom() * GIB_VELOCITY;
        le.PosDeltaY = CRandom() * GIB_VELOCITY;
        le.PosDeltaZ = GIB_JUMP + CRandom() * GIB_VELOCITY;

        le.BounceFactor = 0.6f;

        // Tumble rotation
        le.AngType = TrajectoryType.TR_LINEAR;
        le.AngTime = time;
        le.AngDeltaX = CRandom() * 600;
        le.AngDeltaY = CRandom() * 600;
        le.AngDeltaZ = CRandom() * 600;

        le.HModel = model;
    }

    // ── BubbleTrail ──

    /// <summary>
    /// Underwater bubble trail. Matches CG_BubbleTrail from cg_effects.c.
    /// </summary>
    public static void BubbleTrail(float sx, float sy, float sz,
        float ex, float ey, float ez, float spacing, int time)
    {
        if (_waterBubbleShader == 0) return;

        float dx = ex - sx, dy = ey - sy, dz = ez - sz;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1) return;
        float inv = 1.0f / len;
        dx *= inv; dy *= inv; dz *= inv;

        float moveX = sx, moveY = sy, moveZ = sz;

        // Advance random initial amount
        float i = Random.Shared.Next((int)spacing);
        moveX += dx * i; moveY += dy * i; moveZ += dz * i;

        for (; i < len; i += spacing)
        {
            ref var le = ref LocalEntities.Alloc();
            le.Type = LocalEntities.LeType.MoveScaleFade;
            le.Flags = LocalEntities.LeFlags.PuffDontScale;
            le.StartTime = time;
            le.EndTime = time + 1000 + Random.Shared.Next(250);
            le.LifeRate = 1.0f / (le.EndTime - le.StartTime);

            le.PosType = TrajectoryType.TR_LINEAR;
            le.PosTime = time;
            le.PosBaseX = moveX; le.PosBaseY = moveY; le.PosBaseZ = moveZ;
            le.PosDeltaX = CRandom() * 5;
            le.PosDeltaY = CRandom() * 5;
            le.PosDeltaZ = CRandom() * 5 + 6;

            le.CustomShader = _waterBubbleShader;
            le.Radius = 3;
            le.ColorR = 1; le.ColorG = 1; le.ColorB = 1; le.ColorA = 1;

            moveX += dx * spacing;
            moveY += dy * spacing;
            moveZ += dz * spacing;
        }
    }

    // ── SpawnEffect ──

    /// <summary>
    /// Teleport/spawn visual effect. Matches CG_SpawnEffect from cg_effects.c.
    /// </summary>
    public static void SpawnEffect(float ox, float oy, float oz, int time)
    {
        if (_teleportEffectModel == 0) return;

        ref var le = ref LocalEntities.Alloc();
        le.Type = LocalEntities.LeType.FadeRGB;
        le.StartTime = time;
        le.EndTime = time + 500;
        le.LifeRate = 1.0f / 500.0f;

        le.ColorR = 1; le.ColorG = 1; le.ColorB = 1; le.ColorA = 1;

        le.ReType = Q3RefEntity.RT_MODEL;
        le.HModel = _teleportEffectModel;
        le.CustomShader = _teleportEffectShader;
        le.ShaderTime = time / 1000.0f;

        le.PosType = TrajectoryType.TR_STATIONARY;
        le.PosTime = time;
        le.PosBaseX = ox; le.PosBaseY = oy; le.PosBaseZ = oz - 24;
    }

    // ── MachinegunEjectBrass ──

    /// <summary>
    /// Eject a machinegun brass casing. Matches CG_MachineGunEjectBrass from cg_weapons.c.
    /// </summary>
    public static void MachinegunEjectBrass(float ox, float oy, float oz, int time)
    {
        if (_machinegunBrassModel == 0) return;
        EjectBrass(ox, oy, oz, _machinegunBrassModel, time);
    }

    /// <summary>
    /// Eject a shotgun brass casing. Matches CG_ShotgunEjectBrass from cg_weapons.c.
    /// </summary>
    public static void ShotgunEjectBrass(float ox, float oy, float oz, int time)
    {
        if (_shotgunBrassModel == 0) return;
        EjectBrass(ox, oy, oz, _shotgunBrassModel, time);
    }

    private static void EjectBrass(float ox, float oy, float oz, int model, int time)
    {
        ref var le = ref LocalEntities.Alloc();
        le.Type = LocalEntities.LeType.Fragment;
        le.Flags = LocalEntities.LeFlags.Tumble;
        le.StartTime = time;
        le.EndTime = time + 2000 + Random.Shared.Next(1000);
        le.LifeRate = 1.0f / (le.EndTime - le.StartTime);

        le.PosType = TrajectoryType.TR_GRAVITY;
        le.PosTime = time;
        le.PosBaseX = ox; le.PosBaseY = oy; le.PosBaseZ = oz;
        // Random ejection velocity (up and to the right)
        le.PosDeltaX = CRandom() * 50 + 80;
        le.PosDeltaY = CRandom() * 50;
        le.PosDeltaZ = Random.Shared.Next(50) + 50;

        le.BounceFactor = 0.4f;

        le.AngType = TrajectoryType.TR_LINEAR;
        le.AngTime = time;
        le.AngDeltaX = CRandom() * 600;
        le.AngDeltaY = CRandom() * 600;
        le.AngDeltaZ = CRandom() * 600;

        le.HModel = model;
    }

    // ── Utility ──

    /// <summary>crandom() equivalent: returns -1.0 to 1.0</summary>
    private static float CRandom() => (float)(Random.Shared.NextDouble() * 2.0 - 1.0);

    /// <summary>
    /// Convert a byte index (0-161) to a normalized direction vector.
    /// Matches ByteToDir from q_math.c using the Q3 vertex normals table.
    /// </summary>
    public static void ByteToDir(int b, out float dx, out float dy, out float dz)
    {
        if (b < 0 || b >= 162)
        {
            dx = dy = dz = 0;
            return;
        }
        dx = _byteDirs[b * 3];
        dy = _byteDirs[b * 3 + 1];
        dz = _byteDirs[b * 3 + 2];
    }

    // 162 vertex normals × 3 components
    private static readonly float[] _byteDirs =
    {
        -0.525731f,  0.000000f,  0.850651f, -0.442863f,  0.238856f,  0.864188f,
        -0.295242f,  0.000000f,  0.955423f, -0.309017f,  0.500000f,  0.809017f,
        -0.162460f,  0.262866f,  0.951056f,  0.000000f,  0.000000f,  1.000000f,
         0.000000f,  0.850651f,  0.525731f, -0.147621f,  0.716567f,  0.681718f,
         0.147621f,  0.716567f,  0.681718f,  0.000000f,  0.525731f,  0.850651f,
         0.309017f,  0.500000f,  0.809017f,  0.525731f,  0.000000f,  0.850651f,
         0.295242f,  0.000000f,  0.955423f,  0.442863f,  0.238856f,  0.864188f,
         0.162460f,  0.262866f,  0.951056f, -0.681718f,  0.147621f,  0.716567f,
        -0.809017f,  0.309017f,  0.500000f, -0.587785f,  0.425325f,  0.688191f,
        -0.850651f,  0.525731f,  0.000000f, -0.864188f,  0.442863f,  0.238856f,
        -0.716567f,  0.681718f,  0.147621f, -0.688191f,  0.587785f,  0.425325f,
        -0.500000f,  0.809017f,  0.309017f, -0.238856f,  0.864188f,  0.442863f,
        -0.425325f,  0.688191f,  0.587785f, -0.716567f,  0.681718f, -0.147621f,
        -0.500000f,  0.809017f, -0.309017f, -0.525731f,  0.850651f,  0.000000f,
         0.000000f,  0.850651f, -0.525731f, -0.238856f,  0.864188f, -0.442863f,
         0.000000f,  0.955423f, -0.295242f, -0.262866f,  0.951056f, -0.162460f,
         0.000000f,  1.000000f,  0.000000f,  0.000000f,  0.955423f,  0.295242f,
        -0.262866f,  0.951056f,  0.162460f,  0.238856f,  0.864188f,  0.442863f,
         0.262866f,  0.951056f,  0.162460f,  0.500000f,  0.809017f,  0.309017f,
         0.238856f,  0.864188f, -0.442863f,  0.262866f,  0.951056f, -0.162460f,
         0.500000f,  0.809017f, -0.309017f,  0.850651f,  0.525731f,  0.000000f,
         0.716567f,  0.681718f,  0.147621f,  0.716567f,  0.681718f, -0.147621f,
         0.525731f,  0.850651f,  0.000000f,  0.425325f,  0.688191f,  0.587785f,
         0.864188f,  0.442863f,  0.238856f,  0.688191f,  0.587785f,  0.425325f,
         0.809017f,  0.309017f,  0.500000f,  0.681718f,  0.147621f,  0.716567f,
         0.587785f,  0.425325f,  0.688191f,  0.955423f,  0.295242f,  0.000000f,
         1.000000f,  0.000000f,  0.000000f,  0.951056f,  0.162460f,  0.262866f,
         0.850651f, -0.525731f,  0.000000f,  0.955423f, -0.295242f,  0.000000f,
         0.864188f, -0.442863f,  0.238856f,  0.951056f, -0.162460f,  0.262866f,
         0.809017f, -0.309017f,  0.500000f,  0.681718f, -0.147621f,  0.716567f,
         0.850651f,  0.000000f,  0.525731f,  0.864188f,  0.442863f, -0.238856f,
         0.809017f,  0.309017f, -0.500000f,  0.951056f,  0.162460f, -0.262866f,
         0.525731f,  0.000000f, -0.850651f,  0.681718f,  0.147621f, -0.716567f,
         0.681718f, -0.147621f, -0.716567f,  0.850651f,  0.000000f, -0.525731f,
         0.809017f, -0.309017f, -0.500000f,  0.864188f, -0.442863f, -0.238856f,
         0.951056f, -0.162460f, -0.262866f,  0.147621f,  0.716567f, -0.681718f,
         0.309017f,  0.500000f, -0.809017f,  0.425325f,  0.688191f, -0.587785f,
         0.442863f,  0.238856f, -0.864188f,  0.587785f,  0.425325f, -0.688191f,
         0.688191f,  0.587785f, -0.425325f, -0.147621f,  0.716567f, -0.681718f,
        -0.309017f,  0.500000f, -0.809017f,  0.000000f,  0.525731f, -0.850651f,
        -0.525731f,  0.000000f, -0.850651f, -0.442863f,  0.238856f, -0.864188f,
        -0.295242f,  0.000000f, -0.955423f, -0.162460f,  0.262866f, -0.951056f,
         0.000000f,  0.000000f, -1.000000f,  0.295242f,  0.000000f, -0.955423f,
         0.162460f,  0.262866f, -0.951056f, -0.442863f, -0.238856f, -0.864188f,
        -0.309017f, -0.500000f, -0.809017f, -0.162460f, -0.262866f, -0.951056f,
         0.000000f, -0.850651f, -0.525731f, -0.147621f, -0.716567f, -0.681718f,
         0.147621f, -0.716567f, -0.681718f,  0.000000f, -0.525731f, -0.850651f,
         0.309017f, -0.500000f, -0.809017f,  0.442863f, -0.238856f, -0.864188f,
         0.162460f, -0.262866f, -0.951056f,  0.238856f, -0.864188f, -0.442863f,
         0.500000f, -0.809017f, -0.309017f,  0.425325f, -0.688191f, -0.587785f,
         0.716567f, -0.681718f, -0.147621f,  0.688191f, -0.587785f, -0.425325f,
         0.587785f, -0.425325f, -0.688191f,  0.000000f, -0.955423f, -0.295242f,
         0.000000f, -1.000000f,  0.000000f,  0.262866f, -0.951056f, -0.162460f,
         0.000000f, -0.850651f,  0.525731f,  0.000000f, -0.955423f,  0.295242f,
         0.238856f, -0.864188f,  0.442863f,  0.262866f, -0.951056f,  0.162460f,
         0.500000f, -0.809017f,  0.309017f,  0.716567f, -0.681718f,  0.147621f,
         0.525731f, -0.850651f,  0.000000f, -0.238856f, -0.864188f, -0.442863f,
        -0.500000f, -0.809017f, -0.309017f, -0.262866f, -0.951056f, -0.162460f,
        -0.850651f, -0.525731f,  0.000000f, -0.716567f, -0.681718f, -0.147621f,
        -0.716567f, -0.681718f,  0.147621f, -0.525731f, -0.850651f,  0.000000f,
        -0.500000f, -0.809017f,  0.309017f, -0.238856f, -0.864188f,  0.442863f,
        -0.262866f, -0.951056f,  0.162460f, -0.864188f, -0.442863f,  0.238856f,
        -0.809017f, -0.309017f,  0.500000f, -0.688191f, -0.587785f,  0.425325f,
        -0.681718f, -0.147621f,  0.716567f, -0.442863f, -0.238856f,  0.864188f,
        -0.587785f, -0.425325f,  0.688191f, -0.309017f, -0.500000f,  0.809017f,
        -0.147621f, -0.716567f,  0.681718f, -0.425325f, -0.688191f,  0.587785f,
        -0.162460f, -0.262866f,  0.951056f,  0.442863f, -0.238856f,  0.864188f,
         0.162460f, -0.262866f,  0.951056f,  0.309017f, -0.500000f,  0.809017f,
         0.147621f, -0.716567f,  0.681718f,  0.000000f, -0.525731f,  0.850651f,
         0.425325f, -0.688191f,  0.587785f,  0.587785f, -0.425325f,  0.688191f,
         0.688191f, -0.587785f,  0.425325f, -0.955423f,  0.295242f,  0.000000f,
        -0.951056f,  0.162460f,  0.262866f, -1.000000f,  0.000000f,  0.000000f,
        -0.850651f,  0.000000f,  0.525731f, -0.955423f, -0.295242f,  0.000000f,
        -0.951056f, -0.162460f,  0.262866f, -0.864188f,  0.442863f, -0.238856f,
        -0.951056f,  0.162460f, -0.262866f, -0.809017f,  0.309017f, -0.500000f,
        -0.864188f, -0.442863f, -0.238856f, -0.951056f, -0.162460f, -0.262866f,
        -0.809017f, -0.309017f, -0.500000f, -0.681718f,  0.147621f, -0.716567f,
        -0.681718f, -0.147621f, -0.716567f, -0.850651f,  0.000000f, -0.525731f,
        -0.688191f,  0.587785f, -0.425325f, -0.587785f,  0.425325f, -0.688191f,
        -0.425325f,  0.688191f, -0.587785f, -0.425325f, -0.688191f, -0.587785f,
        -0.587785f, -0.425325f, -0.688191f, -0.688191f, -0.587785f, -0.425325f,
    };
}
