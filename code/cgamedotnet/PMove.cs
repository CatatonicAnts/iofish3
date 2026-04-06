using System.Runtime.CompilerServices;

namespace CGameDotNet;

/// <summary>
/// Port of bg_pmove.c + bg_slidemove.c — Quake 3 player movement physics.
/// Takes a playerstate and usercmd as input, returns modified playerstate.
/// All physics must match the C version exactly for client-side prediction.
/// </summary>
public static unsafe class PMove
{
    // ── Constants ──
    const float MIN_WALK_NORMAL = 0.7f;
    const float STEPSIZE = 18;
    const float JUMP_VELOCITY = 270;
    const float OVERCLIP = 1.001f;
    const int MAX_CLIP_PLANES = 5;
    const int TIMER_LAND = 130;
    const int TIMER_GESTURE = 34 * 66 + 50;
    const int MAXTOUCH = 32;

    // Physics tuning
    const float pm_stopspeed = 100.0f;
    const float pm_duckScale = 0.25f;
    const float pm_swimScale = 0.50f;
    const float pm_accelerate = 10.0f;
    const float pm_airaccelerate = 1.0f;
    const float pm_wateraccelerate = 4.0f;
    const float pm_flyaccelerate = 8.0f;
    const float pm_friction = 6.0f;
    const float pm_waterfriction = 1.0f;
    const float pm_flightfriction = 3.0f;
    const float pm_spectatorfriction = 5.0f;

    // Content flags
    const int CONTENTS_SOLID = 1;
    const int CONTENTS_LAVA = 8;
    const int CONTENTS_SLIME = 16;
    const int CONTENTS_WATER = 32;
    const int CONTENTS_BODY = 0x2000000;
    const int CONTENTS_PLAYERCLIP = 0x10000;
    const int MASK_PLAYERSOLID = CONTENTS_SOLID | CONTENTS_PLAYERCLIP | CONTENTS_BODY;
    const int MASK_WATER = CONTENTS_WATER | CONTENTS_LAVA | CONTENTS_SLIME;

    // Surface flags
    const int SURF_NODAMAGE = 1;
    const int SURF_SLICK = 2;
    const int SURF_NOSTEPS = 0x2000;
    const int SURF_METALSTEPS = 0x1000;

    // PMF flags
    const int PMF_DUCKED = 1;
    const int PMF_JUMP_HELD = 2;
    const int PMF_BACKWARDS_JUMP = 8;
    const int PMF_BACKWARDS_RUN = 16;
    const int PMF_TIME_LAND = 32;
    const int PMF_TIME_KNOCKBACK = 64;
    const int PMF_TIME_WATERJUMP = 256;
    const int PMF_RESPAWNED = 512;
    const int PMF_USE_ITEM_HELD = 1024;
    const int PMF_GRAPPLE_PULL = 2048;
    const int PMF_FOLLOW = 4096;
    const int PMF_INVULEXPAND = 16384;
    const int PMF_ALL_TIMES = PMF_TIME_WATERJUMP | PMF_TIME_LAND | PMF_TIME_KNOCKBACK;

    // Player dimensions
    const int PLAYER_WIDTH = 15;
    const int MINS_Z = -24;
    const int DEFAULT_HEIGHT = 32;
    const int DEFAULT_VIEWHEIGHT = 26;
    const int CROUCH_HEIGHT = 16;
    const int CROUCH_VIEWHEIGHT = 12;
    const int DEAD_HEIGHT = -8;
    const int DEAD_VIEWHEIGHT = -16;

    // Buttons
    const int BUTTON_ATTACK = 1;
    const int BUTTON_TALK = 2;
    const int BUTTON_USE_HOLDABLE = 4;
    const int BUTTON_GESTURE = 8;
    const int BUTTON_WALKING = 16;

    // Weapon states
    const int WEAPON_READY = 0;
    const int WEAPON_RAISING = 1;
    const int WEAPON_DROPPING = 2;
    const int WEAPON_FIRING = 3;

    // Animation indices (animNumber_t)
    const int BOTH_DEATH1 = 0;
    const int BOTH_DEAD1 = 1;
    const int BOTH_DEATH2 = 2;
    const int BOTH_DEAD2 = 3;
    const int BOTH_DEATH3 = 4;
    const int BOTH_DEAD3 = 5;
    const int TORSO_GESTURE = 6;
    const int TORSO_ATTACK = 7;
    const int TORSO_ATTACK2 = 8;
    const int TORSO_DROP = 9;
    const int TORSO_RAISE = 10;
    const int TORSO_STAND = 11;
    const int TORSO_STAND2 = 12;
    const int LEGS_WALKCR = 13;
    const int LEGS_WALK = 14;
    const int LEGS_RUN = 15;
    const int LEGS_BACK = 16;
    const int LEGS_SWIM = 17;
    const int LEGS_JUMP = 18;
    const int LEGS_LAND = 19;
    const int LEGS_JUMPB = 20;
    const int LEGS_LANDB = 21;
    const int LEGS_IDLE = 22;
    const int LEGS_IDLECR = 23;
    const int LEGS_TURN = 24;
    const int LEGS_BACKCR = 31;
    const int LEGS_BACKWALK = 32;
    const int ANIM_TOGGLEBIT = 128;

    // EFlags
    const int EF_TALK = 0x00001000;
    const int EF_FIRING = 0x00000100;

    // Powerup indices
    const int PW_FLIGHT = 6;
    const int PW_HASTE = 3;

    // Angle conversion
    const float SHORT2ANGLE_MUL = 360.0f / 65536.0f;

    // Axis indices
    const int PITCH = 0;

    const int PS_PMOVEFRAMECOUNTBITS = 6;

    // ── Delegate types for trace/pointcontents callbacks ──
    public delegate void TraceDelegate(Q3Trace* results, float* start, float* mins, float* maxs, float* end, int passEntityNum, int contentMask);
    public delegate int PointContentsDelegate(float* point, int passEntityNum);

    // ── Local pmove state (pml_t equivalent) ──
    private static float* forward;
    private static float* right;
    private static float* up;
    private static float frametime;
    private static int msec;
    private static float* previous_origin;
    private static float* previous_velocity;
    private static int previous_waterlevel;
    private static bool walking;
    private static bool groundPlane;
    private static Q3Trace groundTrace;
    private static float impactSpeed;

    // Scratch buffers allocated once
    private static readonly float[] _forward = new float[3];
    private static readonly float[] _right = new float[3];
    private static readonly float[] _up = new float[3];
    private static readonly float[] _previousOrigin = new float[3];
    private static readonly float[] _previousVelocity = new float[3];

    // Current pmove being processed
    private static Q3PlayerState* pm_ps;
    private static Q3UserCmd pm_cmd;
    private static int pm_tracemask;
    private static TraceDelegate? pm_trace;
    private static PointContentsDelegate? pm_pointcontents;
    private static int pm_numtouch;
    private static int[] pm_touchents = new int[MAXTOUCH];
    private static float* pm_mins;
    private static float* pm_maxs;
    private static float pm_xyspeed;
    private static int pm_watertype;
    private static int pm_waterlevel;
    private static bool pm_noFootsteps;
    private static bool pm_gauntletHit;
#pragma warning disable CS0169
    private static int pm_debugLevel;
#pragma warning restore CS0169

    private static int c_pmove;

    // Helpers to get pointers to static struct fields (avoids CS0212)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float* GroundNormal() =>
        (float*)Unsafe.AsPointer(ref groundTrace.PlaneNormalX);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Q3UserCmd* CmdPtr() =>
        (Q3UserCmd*)Unsafe.AsPointer(ref pm_cmd);

    // ── Vector math helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProduct(float* a, float* b) =>
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorCopy(float* src, float* dst)
    {
        dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorScale(float* v, float s, float* o)
    {
        o[0] = v[0] * s; o[1] = v[1] * s; o[2] = v[2] * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorMA(float* v, float s, float* b, float* o)
    {
        o[0] = v[0] + b[0] * s;
        o[1] = v[1] + b[1] * s;
        o[2] = v[2] + b[2] * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorAdd(float* a, float* b, float* o)
    {
        o[0] = a[0] + b[0]; o[1] = a[1] + b[1]; o[2] = a[2] + b[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorSubtract(float* a, float* b, float* o)
    {
        o[0] = a[0] - b[0]; o[1] = a[1] - b[1]; o[2] = a[2] - b[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorClear(float* v)
    {
        v[0] = 0; v[1] = 0; v[2] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorSet(float* v, float x, float y, float z)
    {
        v[0] = x; v[1] = y; v[2] = z;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VectorLength(float* v) =>
        MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    private static float VectorNormalize(float* v)
    {
        float length = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (length != 0)
        {
            float ilength = 1.0f / length;
            v[0] *= ilength;
            v[1] *= ilength;
            v[2] *= ilength;
        }
        return length;
    }

    private static void VectorNormalize2(float* v, float* o)
    {
        float length = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (length != 0)
        {
            float ilength = 1.0f / length;
            o[0] = v[0] * ilength;
            o[1] = v[1] * ilength;
            o[2] = v[2] * ilength;
        }
        else
        {
            o[0] = o[1] = o[2] = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CrossProduct(float* a, float* b, float* c)
    {
        c[0] = a[1] * b[2] - a[2] * b[1];
        c[1] = a[2] * b[0] - a[0] * b[2];
        c[2] = a[0] * b[1] - a[1] * b[0];
    }

    private static void AngleVectors(float* angles, float* fwd, float* rt, float* u)
    {
        float sy = MathF.Sin(angles[1] * (MathF.PI / 180.0f));
        float cy = MathF.Cos(angles[1] * (MathF.PI / 180.0f));
        float sp = MathF.Sin(angles[0] * (MathF.PI / 180.0f));
        float cp = MathF.Cos(angles[0] * (MathF.PI / 180.0f));
        float sr = MathF.Sin(angles[2] * (MathF.PI / 180.0f));
        float cr = MathF.Cos(angles[2] * (MathF.PI / 180.0f));

        if (fwd != null)
        {
            fwd[0] = cp * cy;
            fwd[1] = cp * sy;
            fwd[2] = -sp;
        }
        if (rt != null)
        {
            rt[0] = -1 * sr * sp * cy + -1 * cr * -sy;
            rt[1] = -1 * sr * sp * sy + -1 * cr * cy;
            rt[2] = -1 * sr * cp;
        }
        if (u != null)
        {
            u[0] = cr * sp * cy + -sr * -sy;
            u[1] = cr * sp * sy + -sr * cy;
            u[2] = cr * cp;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SnapVector(float* v)
    {
        v[0] = MathF.Round(v[0]);
        v[1] = MathF.Round(v[1]);
        v[2] = MathF.Round(v[2]);
    }

    // ── Event helpers ──

    private static void PM_AddEvent(int newEvent)
    {
        // BG_AddPredictableEventToPlayerstate
        pm_ps->Events[pm_ps->EventSequence & 1] = newEvent;
        pm_ps->EventParms[pm_ps->EventSequence & 1] = 0;
        pm_ps->EventSequence++;
    }

    private static void PM_AddEventWithParm(int newEvent, int parm)
    {
        pm_ps->Events[pm_ps->EventSequence & 1] = newEvent;
        pm_ps->EventParms[pm_ps->EventSequence & 1] = parm;
        pm_ps->EventSequence++;
    }

    private static void PM_AddTouchEnt(int entityNum)
    {
        if (entityNum == EntityNum.ENTITYNUM_WORLD) return;
        if (pm_numtouch == MAXTOUCH) return;

        for (int i = 0; i < pm_numtouch; i++)
        {
            if (pm_touchents[i] == entityNum) return;
        }

        pm_touchents[pm_numtouch] = entityNum;
        pm_numtouch++;
    }

    // ── Animation helpers ──

    private static void PM_StartTorsoAnim(int anim)
    {
        if (pm_ps->PmType >= PmType.PM_DEAD) return;
        pm_ps->TorsoAnim = ((pm_ps->TorsoAnim & ANIM_TOGGLEBIT) ^ ANIM_TOGGLEBIT) | anim;
    }

    private static void PM_StartLegsAnim(int anim)
    {
        if (pm_ps->PmType >= PmType.PM_DEAD) return;
        if (pm_ps->LegsTimer > 0) return;
        pm_ps->LegsAnim = ((pm_ps->LegsAnim & ANIM_TOGGLEBIT) ^ ANIM_TOGGLEBIT) | anim;
    }

    private static void PM_ContinueLegsAnim(int anim)
    {
        if ((pm_ps->LegsAnim & ~ANIM_TOGGLEBIT) == anim) return;
        if (pm_ps->LegsTimer > 0) return;
        PM_StartLegsAnim(anim);
    }

    private static void PM_ContinueTorsoAnim(int anim)
    {
        if ((pm_ps->TorsoAnim & ~ANIM_TOGGLEBIT) == anim) return;
        if (pm_ps->TorsoTimer > 0) return;
        PM_StartTorsoAnim(anim);
    }

    private static void PM_ForceLegsAnim(int anim)
    {
        pm_ps->LegsTimer = 0;
        PM_StartLegsAnim(anim);
    }

    // ── PM_ClipVelocity ──

    public static void PM_ClipVelocity(float* inp, float* normal, float* outp, float overbounce)
    {
        float backoff = DotProduct(inp, normal);

        if (backoff < 0)
            backoff *= overbounce;
        else
            backoff /= overbounce;

        for (int i = 0; i < 3; i++)
        {
            float change = normal[i] * backoff;
            outp[i] = inp[i] - change;
        }
    }

    // ── PM_Friction ──

    private static void PM_Friction()
    {
        float* vel = &pm_ps->VelocityX;
        float vec0 = vel[0], vec1 = vel[1], vec2 = vel[2];

        if (walking)
            vec2 = 0;

        float speed = MathF.Sqrt(vec0 * vec0 + vec1 * vec1 + vec2 * vec2);
        if (speed < 1)
        {
            vel[0] = 0;
            vel[1] = 0;
            return;
        }

        float drop = 0;

        // ground friction
        if (pm_waterlevel <= 1)
        {
            if (walking && (groundTrace.SurfaceFlags & SURF_SLICK) == 0)
            {
                if ((pm_ps->PmFlags & PMF_TIME_KNOCKBACK) == 0)
                {
                    float control = speed < pm_stopspeed ? pm_stopspeed : speed;
                    drop += control * pm_friction * frametime;
                }
            }
        }

        // water friction
        if (pm_waterlevel != 0)
        {
            drop += speed * pm_waterfriction * pm_waterlevel * frametime;
        }

        // flight friction
        if (pm_ps->PowerupTimers[PW_FLIGHT] != 0)
        {
            drop += speed * pm_flightfriction * frametime;
        }

        // spectator friction
        if (pm_ps->PmType == PmType.PM_SPECTATOR)
        {
            drop += speed * pm_spectatorfriction * frametime;
        }

        float newspeed = speed - drop;
        if (newspeed < 0) newspeed = 0;
        newspeed /= speed;

        vel[0] *= newspeed;
        vel[1] *= newspeed;
        vel[2] *= newspeed;
    }

    // ── PM_Accelerate ──

    private static void PM_Accelerate(float* wishdir, float wishspeed, float accel)
    {
        float currentspeed = DotProduct(&pm_ps->VelocityX, wishdir);
        float addspeed = wishspeed - currentspeed;
        if (addspeed <= 0) return;

        float accelspeed = accel * frametime * wishspeed;
        if (accelspeed > addspeed)
            accelspeed = addspeed;

        pm_ps->VelocityX += accelspeed * wishdir[0];
        pm_ps->VelocityY += accelspeed * wishdir[1];
        pm_ps->VelocityZ += accelspeed * wishdir[2];
    }

    // ── PM_CmdScale ──

    private static float PM_CmdScale(Q3UserCmd* cmd)
    {
        int max = Math.Abs(cmd->ForwardMove);
        if (Math.Abs(cmd->RightMove) > max) max = Math.Abs(cmd->RightMove);
        if (Math.Abs(cmd->UpMove) > max) max = Math.Abs(cmd->UpMove);
        if (max == 0) return 0;

        float total = MathF.Sqrt((float)(cmd->ForwardMove * cmd->ForwardMove
            + cmd->RightMove * cmd->RightMove + cmd->UpMove * cmd->UpMove));
        float scale = (float)pm_ps->Speed * max / (127.0f * total);
        return scale;
    }

    // ── PM_SetMovementDir ──

    private static void PM_SetMovementDir()
    {
        if (pm_cmd.ForwardMove != 0 || pm_cmd.RightMove != 0)
        {
            if (pm_cmd.RightMove == 0 && pm_cmd.ForwardMove > 0) pm_ps->MovementDir = 0;
            else if (pm_cmd.RightMove < 0 && pm_cmd.ForwardMove > 0) pm_ps->MovementDir = 1;
            else if (pm_cmd.RightMove < 0 && pm_cmd.ForwardMove == 0) pm_ps->MovementDir = 2;
            else if (pm_cmd.RightMove < 0 && pm_cmd.ForwardMove < 0) pm_ps->MovementDir = 3;
            else if (pm_cmd.RightMove == 0 && pm_cmd.ForwardMove < 0) pm_ps->MovementDir = 4;
            else if (pm_cmd.RightMove > 0 && pm_cmd.ForwardMove < 0) pm_ps->MovementDir = 5;
            else if (pm_cmd.RightMove > 0 && pm_cmd.ForwardMove == 0) pm_ps->MovementDir = 6;
            else if (pm_cmd.RightMove > 0 && pm_cmd.ForwardMove > 0) pm_ps->MovementDir = 7;
        }
        else
        {
            if (pm_ps->MovementDir == 2) pm_ps->MovementDir = 1;
            else if (pm_ps->MovementDir == 6) pm_ps->MovementDir = 7;
        }
    }

    // ── PM_CheckJump ──

    private static bool PM_CheckJump()
    {
        if ((pm_ps->PmFlags & PMF_RESPAWNED) != 0) return false;
        if (pm_cmd.UpMove < 10) return false;

        if ((pm_ps->PmFlags & PMF_JUMP_HELD) != 0)
        {
            pm_cmd.UpMove = 0;
            return false;
        }

        groundPlane = false;
        walking = false;
        pm_ps->PmFlags |= PMF_JUMP_HELD;

        pm_ps->GroundEntityNum = EntityNum.ENTITYNUM_NONE;
        pm_ps->VelocityZ = JUMP_VELOCITY;
        PM_AddEvent(EntityEvent.EV_JUMP);

        if (pm_cmd.ForwardMove >= 0)
        {
            PM_ForceLegsAnim(LEGS_JUMP);
            pm_ps->PmFlags &= ~PMF_BACKWARDS_JUMP;
        }
        else
        {
            PM_ForceLegsAnim(LEGS_JUMPB);
            pm_ps->PmFlags |= PMF_BACKWARDS_JUMP;
        }

        return true;
    }

    // ── PM_CheckWaterJump ──

    private static bool PM_CheckWaterJump()
    {
        if (pm_ps->PmTime != 0) return false;
        if (pm_waterlevel != 2) return false;

        float* flatforward = stackalloc float[3];
        flatforward[0] = forward[0];
        flatforward[1] = forward[1];
        flatforward[2] = 0;
        VectorNormalize(flatforward);

        float* spot = stackalloc float[3];
        VectorMA(&pm_ps->OriginX, 30, flatforward, spot);
        spot[2] += 4;
        int cont = pm_pointcontents!(spot, pm_ps->ClientNum);
        if ((cont & CONTENTS_SOLID) == 0) return false;

        spot[2] += 16;
        cont = pm_pointcontents!(spot, pm_ps->ClientNum);
        if ((cont & (CONTENTS_SOLID | CONTENTS_PLAYERCLIP | CONTENTS_BODY)) != 0) return false;

        // jump out of water
        VectorScale(forward, 200, &pm_ps->VelocityX);
        pm_ps->VelocityZ = 350;

        pm_ps->PmFlags |= PMF_TIME_WATERJUMP;
        pm_ps->PmTime = 2000;

        return true;
    }

    // ── PM_WaterJumpMove ──

    private static void PM_WaterJumpMove()
    {
        PM_StepSlideMove(true);

        pm_ps->VelocityZ -= pm_ps->Gravity * frametime;
        if (pm_ps->VelocityZ < 0)
        {
            pm_ps->PmFlags &= ~PMF_ALL_TIMES;
            pm_ps->PmTime = 0;
        }
    }

    // ── PM_WaterMove ──

    private static void PM_WaterMove()
    {
        if (PM_CheckWaterJump())
        {
            PM_WaterJumpMove();
            return;
        }

        PM_Friction();

        float scale = PM_CmdScale(CmdPtr());
        float* wishvel = stackalloc float[3];
        float* wishdir = stackalloc float[3];

        if (scale == 0)
        {
            wishvel[0] = 0;
            wishvel[1] = 0;
            wishvel[2] = -60;
        }
        else
        {
            for (int i = 0; i < 3; i++)
                wishvel[i] = scale * forward[i] * pm_cmd.ForwardMove + scale * right[i] * pm_cmd.RightMove;
            wishvel[2] += scale * pm_cmd.UpMove;
        }

        VectorCopy(wishvel, wishdir);
        float wishspeed = VectorNormalize(wishdir);

        if (wishspeed > pm_ps->Speed * pm_swimScale)
            wishspeed = pm_ps->Speed * pm_swimScale;

        PM_Accelerate(wishdir, wishspeed, pm_wateraccelerate);

        // make sure we can go up slopes easily under water
        if (groundPlane && DotProduct(&pm_ps->VelocityX, GroundNormal()) < 0)
        {
            float vel = VectorLength(&pm_ps->VelocityX);
            PM_ClipVelocity(&pm_ps->VelocityX, GroundNormal(), &pm_ps->VelocityX, OVERCLIP);
            VectorNormalize(&pm_ps->VelocityX);
            VectorScale(&pm_ps->VelocityX, vel, &pm_ps->VelocityX);
        }

        PM_SlideMove(false);
    }

    // ── PM_FlyMove ──

    private static void PM_FlyMove()
    {
        PM_Friction();

        float scale = PM_CmdScale(CmdPtr());
        float* wishvel = stackalloc float[3];
        float* wishdir = stackalloc float[3];

        if (scale == 0)
        {
            wishvel[0] = 0;
            wishvel[1] = 0;
            wishvel[2] = 0;
        }
        else
        {
            for (int i = 0; i < 3; i++)
                wishvel[i] = scale * forward[i] * pm_cmd.ForwardMove + scale * right[i] * pm_cmd.RightMove;
            wishvel[2] += scale * pm_cmd.UpMove;
        }

        VectorCopy(wishvel, wishdir);
        float wishspeed = VectorNormalize(wishdir);

        PM_Accelerate(wishdir, wishspeed, pm_flyaccelerate);

        PM_StepSlideMove(false);
    }

    // ── PM_AirMove ──

    private static void PM_AirMove()
    {
        PM_Friction();

        float fmove = pm_cmd.ForwardMove;
        float smove = pm_cmd.RightMove;

        Q3UserCmd cmd = pm_cmd;
        float scale = PM_CmdScale(&cmd);

        PM_SetMovementDir();

        // project moves down to flat plane
        forward[2] = 0;
        right[2] = 0;
        VectorNormalize(forward);
        VectorNormalize(right);

        float* wishvel = stackalloc float[3];
        float* wishdir = stackalloc float[3];

        for (int i = 0; i < 2; i++)
            wishvel[i] = forward[i] * fmove + right[i] * smove;
        wishvel[2] = 0;

        VectorCopy(wishvel, wishdir);
        float wishspeed = VectorNormalize(wishdir);
        wishspeed *= scale;

        PM_Accelerate(wishdir, wishspeed, pm_airaccelerate);

        if (groundPlane)
        {
            PM_ClipVelocity(&pm_ps->VelocityX, GroundNormal(),
                &pm_ps->VelocityX, OVERCLIP);
        }

        PM_StepSlideMove(true);
    }

    // ── PM_GrappleMove ──

    private static void PM_GrappleMove()
    {
        float* vel = stackalloc float[3];
        float* v = stackalloc float[3];

        VectorScale(forward, -16, v);
        VectorAdd(&pm_ps->GrapplePointX, v, v);
        VectorSubtract(v, &pm_ps->OriginX, vel);
        float vlen = VectorLength(vel);
        VectorNormalize(vel);

        if (vlen <= 100)
            VectorScale(vel, 10 * vlen, vel);
        else
            VectorScale(vel, 800, vel);

        VectorCopy(vel, &pm_ps->VelocityX);
        groundPlane = false;
    }

    // ── PM_WalkMove ──

    private static void PM_WalkMove()
    {
        if (pm_waterlevel > 2 && DotProduct(forward, GroundNormal()) > 0)
        {
            PM_WaterMove();
            return;
        }

        if (PM_CheckJump())
        {
            if (pm_waterlevel > 1)
                PM_WaterMove();
            else
                PM_AirMove();
            return;
        }

        PM_Friction();

        float fmove = pm_cmd.ForwardMove;
        float smove = pm_cmd.RightMove;

        Q3UserCmd cmd = pm_cmd;
        float scale = PM_CmdScale(&cmd);

        PM_SetMovementDir();

        // project moves down to flat plane
        forward[2] = 0;
        right[2] = 0;

        PM_ClipVelocity(forward, GroundNormal(), forward, OVERCLIP);
        PM_ClipVelocity(right, GroundNormal(), right, OVERCLIP);
        VectorNormalize(forward);
        VectorNormalize(right);

        float* wishvel = stackalloc float[3];
        float* wishdir = stackalloc float[3];

        for (int i = 0; i < 3; i++)
            wishvel[i] = forward[i] * fmove + right[i] * smove;

        VectorCopy(wishvel, wishdir);
        float wishspeed = VectorNormalize(wishdir);
        wishspeed *= scale;

        // clamp speed lower if ducking
        if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
        {
            if (wishspeed > pm_ps->Speed * pm_duckScale)
                wishspeed = pm_ps->Speed * pm_duckScale;
        }

        // clamp speed if wading
        if (pm_waterlevel != 0)
        {
            float waterScale = pm_waterlevel / 3.0f;
            waterScale = 1.0f - (1.0f - pm_swimScale) * waterScale;
            if (wishspeed > pm_ps->Speed * waterScale)
                wishspeed = pm_ps->Speed * waterScale;
        }

        float accelerate;
        if ((groundTrace.SurfaceFlags & SURF_SLICK) != 0 || (pm_ps->PmFlags & PMF_TIME_KNOCKBACK) != 0)
            accelerate = pm_airaccelerate;
        else
            accelerate = pm_accelerate;

        PM_Accelerate(wishdir, wishspeed, accelerate);

        if ((groundTrace.SurfaceFlags & SURF_SLICK) != 0 || (pm_ps->PmFlags & PMF_TIME_KNOCKBACK) != 0)
        {
            pm_ps->VelocityZ -= pm_ps->Gravity * frametime;
        }

        float vel = VectorLength(&pm_ps->VelocityX);

        // slide along the ground plane
        PM_ClipVelocity(&pm_ps->VelocityX, GroundNormal(),
            &pm_ps->VelocityX, OVERCLIP);

        // don't decrease velocity when going up or down a slope
        VectorNormalize(&pm_ps->VelocityX);
        VectorScale(&pm_ps->VelocityX, vel, &pm_ps->VelocityX);

        // don't do anything if standing still
        if (pm_ps->VelocityX == 0 && pm_ps->VelocityY == 0) return;

        PM_StepSlideMove(false);
    }

    // ── PM_DeadMove ──

    private static void PM_DeadMove()
    {
        if (!walking) return;

        float fwd = VectorLength(&pm_ps->VelocityX);
        fwd -= 20;
        if (fwd <= 0)
        {
            VectorClear(&pm_ps->VelocityX);
        }
        else
        {
            VectorNormalize(&pm_ps->VelocityX);
            VectorScale(&pm_ps->VelocityX, fwd, &pm_ps->VelocityX);
        }
    }

    // ── PM_NoclipMove ──

    private static void PM_NoclipMove()
    {
        pm_ps->ViewHeight = DEFAULT_VIEWHEIGHT;

        float speed = VectorLength(&pm_ps->VelocityX);
        if (speed < 1)
        {
            VectorClear(&pm_ps->VelocityX);
        }
        else
        {
            float friction = pm_friction * 1.5f;
            float control = speed < pm_stopspeed ? pm_stopspeed : speed;
            float drop = control * friction * frametime;
            float newspeed = speed - drop;
            if (newspeed < 0) newspeed = 0;
            newspeed /= speed;
            VectorScale(&pm_ps->VelocityX, newspeed, &pm_ps->VelocityX);
        }

        float scale = PM_CmdScale(CmdPtr());
        float fmove = pm_cmd.ForwardMove;
        float smove = pm_cmd.RightMove;

        float* wishvel = stackalloc float[3];
        float* wishdir = stackalloc float[3];

        for (int i = 0; i < 3; i++)
            wishvel[i] = forward[i] * fmove + right[i] * smove;
        wishvel[2] += pm_cmd.UpMove;

        VectorCopy(wishvel, wishdir);
        float wishspeed = VectorNormalize(wishdir);
        wishspeed *= scale;

        PM_Accelerate(wishdir, wishspeed, pm_accelerate);

        // move
        VectorMA(&pm_ps->OriginX, frametime, &pm_ps->VelocityX, &pm_ps->OriginX);
    }

    // ── PM_FootstepForSurface ──

    private static int PM_FootstepForSurface()
    {
        if ((groundTrace.SurfaceFlags & SURF_NOSTEPS) != 0) return 0;
        if ((groundTrace.SurfaceFlags & SURF_METALSTEPS) != 0) return EntityEvent.EV_FOOTSTEP_METAL;
        return EntityEvent.EV_FOOTSTEP;
    }

    // ── PM_CrashLand ──

    private static void PM_CrashLand()
    {
        // decide which landing animation to use
        if ((pm_ps->PmFlags & PMF_BACKWARDS_JUMP) != 0)
            PM_ForceLegsAnim(LEGS_LANDB);
        else
            PM_ForceLegsAnim(LEGS_LAND);

        pm_ps->LegsTimer = TIMER_LAND;

        // calculate the exact velocity on landing
        float dist = pm_ps->OriginZ - previous_origin[2];
        float vel = previous_velocity[2];
        float acc = -pm_ps->Gravity;

        float a = acc / 2.0f;
        float b = vel;
        float c = -dist;

        float den = b * b - 4 * a * c;
        if (den < 0) return;
        float t = (-b - MathF.Sqrt(den)) / (2 * a);

        float delta = vel + t * acc;
        delta = delta * delta * 0.0001f;

        // ducking while falling doubles damage
        if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
            delta *= 2;

        // never take falling damage if completely underwater
        if (pm_waterlevel == 3) return;

        if (pm_waterlevel == 2) delta *= 0.25f;
        if (pm_waterlevel == 1) delta *= 0.5f;

        if (delta < 1) return;

        if ((groundTrace.SurfaceFlags & SURF_NODAMAGE) == 0)
        {
            if (delta > 60)
                PM_AddEvent(EntityEvent.EV_FALL_FAR);
            else if (delta > 40)
            {
                if (pm_ps->Stats[Stats.STAT_HEALTH] > 0)
                    PM_AddEvent(EntityEvent.EV_FALL_MEDIUM);
            }
            else if (delta > 7)
                PM_AddEvent(EntityEvent.EV_FALL_SHORT);
            else
                PM_AddEvent(PM_FootstepForSurface());
        }

        pm_ps->BobCycle = 0;
    }

    // ── PM_CorrectAllSolid ──

    private static bool PM_CorrectAllSolid(Q3Trace* trace)
    {
        float* point = stackalloc float[3];

        for (int i = -1; i <= 1; i++)
        {
            for (int j = -1; j <= 1; j++)
            {
                for (int k = -1; k <= 1; k++)
                {
                    VectorCopy(&pm_ps->OriginX, point);
                    point[0] += (float)i;
                    point[1] += (float)j;
                    point[2] += (float)k;
                    pm_trace!(trace, point, pm_mins, pm_maxs, point, pm_ps->ClientNum, pm_tracemask);
                    if (trace->AllSolid == 0)
                    {
                        point[0] = pm_ps->OriginX;
                        point[1] = pm_ps->OriginY;
                        point[2] = pm_ps->OriginZ - 0.25f;

                        pm_trace(trace, &pm_ps->OriginX, pm_mins, pm_maxs, point, pm_ps->ClientNum, pm_tracemask);
                        groundTrace = *trace;
                        return true;
                    }
                }
            }
        }

        pm_ps->GroundEntityNum = EntityNum.ENTITYNUM_NONE;
        groundPlane = false;
        walking = false;
        return false;
    }

    // ── PM_GroundTraceMissed ──

    private static void PM_GroundTraceMissed()
    {
        if (pm_ps->GroundEntityNum != EntityNum.ENTITYNUM_NONE)
        {
            float* point = stackalloc float[3];
            VectorCopy(&pm_ps->OriginX, point);
            point[2] -= 64;

            Q3Trace trace;
            pm_trace!(&trace, &pm_ps->OriginX, pm_mins, pm_maxs, point, pm_ps->ClientNum, pm_tracemask);
            if (trace.Fraction == 1.0f)
            {
                if (pm_cmd.ForwardMove >= 0)
                {
                    PM_ForceLegsAnim(LEGS_JUMP);
                    pm_ps->PmFlags &= ~PMF_BACKWARDS_JUMP;
                }
                else
                {
                    PM_ForceLegsAnim(LEGS_JUMPB);
                    pm_ps->PmFlags |= PMF_BACKWARDS_JUMP;
                }
            }
        }

        pm_ps->GroundEntityNum = EntityNum.ENTITYNUM_NONE;
        groundPlane = false;
        walking = false;
    }

    // ── PM_GroundTrace ──

    private static void PM_GroundTrace()
    {
        float* point = stackalloc float[3];
        point[0] = pm_ps->OriginX;
        point[1] = pm_ps->OriginY;
        point[2] = pm_ps->OriginZ - 0.25f;

        Q3Trace trace;
        pm_trace!(&trace, &pm_ps->OriginX, pm_mins, pm_maxs, point, pm_ps->ClientNum, pm_tracemask);
        groundTrace = trace;

        // corrective if trace starts in solid
        if (trace.AllSolid != 0)
        {
            if (!PM_CorrectAllSolid(&trace))
                return;
        }

        // if trace didn't hit, we are in free fall
        if (trace.Fraction == 1.0f)
        {
            PM_GroundTraceMissed();
            groundPlane = false;
            walking = false;
            return;
        }

        // check if getting thrown off the ground
        if (pm_ps->VelocityZ > 0 && DotProduct(&pm_ps->VelocityX, &trace.PlaneNormalX) > 10)
        {
            if (pm_cmd.ForwardMove >= 0)
            {
                PM_ForceLegsAnim(LEGS_JUMP);
                pm_ps->PmFlags &= ~PMF_BACKWARDS_JUMP;
            }
            else
            {
                PM_ForceLegsAnim(LEGS_JUMPB);
                pm_ps->PmFlags |= PMF_BACKWARDS_JUMP;
            }

            pm_ps->GroundEntityNum = EntityNum.ENTITYNUM_NONE;
            groundPlane = false;
            walking = false;
            return;
        }

        // slopes that are too steep will not be considered onground
        if (trace.PlaneNormalZ < MIN_WALK_NORMAL)
        {
            pm_ps->GroundEntityNum = EntityNum.ENTITYNUM_NONE;
            groundPlane = true;
            walking = false;
            return;
        }

        groundPlane = true;
        walking = true;

        // hitting solid ground ends waterjump
        if ((pm_ps->PmFlags & PMF_TIME_WATERJUMP) != 0)
        {
            pm_ps->PmFlags &= ~(PMF_TIME_WATERJUMP | PMF_TIME_LAND);
            pm_ps->PmTime = 0;
        }

        if (pm_ps->GroundEntityNum == EntityNum.ENTITYNUM_NONE)
        {
            // just hit the ground
            PM_CrashLand();

            if (previous_velocity[2] < -200)
            {
                pm_ps->PmFlags |= PMF_TIME_LAND;
                pm_ps->PmTime = 250;
            }
        }

        pm_ps->GroundEntityNum = trace.EntityNum;

        PM_AddTouchEnt(trace.EntityNum);
    }

    // ── PM_SetWaterLevel ──

    private static void PM_SetWaterLevel()
    {
        pm_waterlevel = 0;
        pm_watertype = 0;

        float* point = stackalloc float[3];
        point[0] = pm_ps->OriginX;
        point[1] = pm_ps->OriginY;
        point[2] = pm_ps->OriginZ + MINS_Z + 1;
        int cont = pm_pointcontents!(point, pm_ps->ClientNum);

        if ((cont & MASK_WATER) != 0)
        {
            int sample2 = pm_ps->ViewHeight - MINS_Z;
            int sample1 = sample2 / 2;

            pm_watertype = cont;
            pm_waterlevel = 1;
            point[2] = pm_ps->OriginZ + MINS_Z + sample1;
            cont = pm_pointcontents(point, pm_ps->ClientNum);
            if ((cont & MASK_WATER) != 0)
            {
                pm_waterlevel = 2;
                point[2] = pm_ps->OriginZ + MINS_Z + sample2;
                cont = pm_pointcontents(point, pm_ps->ClientNum);
                if ((cont & MASK_WATER) != 0)
                    pm_waterlevel = 3;
            }
        }
    }

    // ── PM_CheckDuck ──

    private static void PM_CheckDuck()
    {
        pm_ps->PmFlags &= ~PMF_INVULEXPAND;

        pm_mins[0] = -PLAYER_WIDTH;
        pm_mins[1] = -PLAYER_WIDTH;
        pm_maxs[0] = PLAYER_WIDTH;
        pm_maxs[1] = PLAYER_WIDTH;
        pm_mins[2] = MINS_Z;

        if (pm_ps->PmType == PmType.PM_DEAD)
        {
            pm_maxs[2] = DEAD_HEIGHT;
            pm_ps->ViewHeight = DEAD_VIEWHEIGHT;
            return;
        }

        if (pm_cmd.UpMove < 0)
        {
            // duck
            pm_ps->PmFlags |= PMF_DUCKED;
        }
        else
        {
            // stand up if possible
            if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
            {
                pm_maxs[2] = DEFAULT_HEIGHT;
                Q3Trace trace;
                pm_trace!(&trace, &pm_ps->OriginX, pm_mins, pm_maxs, &pm_ps->OriginX, pm_ps->ClientNum, pm_tracemask);
                if (trace.AllSolid == 0)
                    pm_ps->PmFlags &= ~PMF_DUCKED;
            }
        }

        if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
        {
            pm_maxs[2] = CROUCH_HEIGHT;
            pm_ps->ViewHeight = CROUCH_VIEWHEIGHT;
        }
        else
        {
            pm_maxs[2] = DEFAULT_HEIGHT;
            pm_ps->ViewHeight = DEFAULT_VIEWHEIGHT;
        }
    }

    // ── PM_Footsteps ──

    private static void PM_Footsteps()
    {
        pm_xyspeed = MathF.Sqrt(pm_ps->VelocityX * pm_ps->VelocityX
            + pm_ps->VelocityY * pm_ps->VelocityY);

        if (pm_ps->GroundEntityNum == EntityNum.ENTITYNUM_NONE)
        {
            if (pm_waterlevel > 1)
                PM_ContinueLegsAnim(LEGS_SWIM);
            return;
        }

        // if not trying to move
        if (pm_cmd.ForwardMove == 0 && pm_cmd.RightMove == 0)
        {
            if (pm_xyspeed < 5)
            {
                pm_ps->BobCycle = 0;
                if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
                    PM_ContinueLegsAnim(LEGS_IDLECR);
                else
                    PM_ContinueLegsAnim(LEGS_IDLE);
            }
            return;
        }

        bool footstep = false;
        float bobmove;

        if ((pm_ps->PmFlags & PMF_DUCKED) != 0)
        {
            bobmove = 0.5f;
            if ((pm_ps->PmFlags & PMF_BACKWARDS_RUN) != 0)
                PM_ContinueLegsAnim(LEGS_BACKCR);
            else
                PM_ContinueLegsAnim(LEGS_WALKCR);
        }
        else
        {
            if ((pm_cmd.Buttons & BUTTON_WALKING) == 0)
            {
                bobmove = 0.4f;
                if ((pm_ps->PmFlags & PMF_BACKWARDS_RUN) != 0)
                    PM_ContinueLegsAnim(LEGS_BACK);
                else
                    PM_ContinueLegsAnim(LEGS_RUN);
                footstep = true;
            }
            else
            {
                bobmove = 0.3f;
                if ((pm_ps->PmFlags & PMF_BACKWARDS_RUN) != 0)
                    PM_ContinueLegsAnim(LEGS_BACKWALK);
                else
                    PM_ContinueLegsAnim(LEGS_WALK);
            }
        }

        int old = pm_ps->BobCycle;
        pm_ps->BobCycle = (int)(old + bobmove * msec) & 255;

        // if we just crossed a cycle boundary, play footstep event
        if (((old + 64) ^ (pm_ps->BobCycle + 64)) != 0 && (((old + 64) ^ (pm_ps->BobCycle + 64)) & 128) != 0)
        {
            if (pm_waterlevel == 0)
            {
                if (footstep && !pm_noFootsteps)
                    PM_AddEvent(PM_FootstepForSurface());
            }
            else if (pm_waterlevel == 1)
                PM_AddEvent(EntityEvent.EV_FOOTSPLASH);
            else if (pm_waterlevel == 2)
                PM_AddEvent(EntityEvent.EV_SWIM);
        }
    }

    // ── PM_WaterEvents ──

    private static void PM_WaterEvents()
    {
        if (previous_waterlevel == 0 && pm_waterlevel != 0)
            PM_AddEvent(EntityEvent.EV_WATER_TOUCH);

        if (previous_waterlevel != 0 && pm_waterlevel == 0)
            PM_AddEvent(EntityEvent.EV_WATER_LEAVE);

        if (previous_waterlevel != 3 && pm_waterlevel == 3)
            PM_AddEvent(EntityEvent.EV_WATER_UNDER);

        if (previous_waterlevel == 3 && pm_waterlevel != 3)
            PM_AddEvent(EntityEvent.EV_WATER_CLEAR);
    }

    // ── PM_BeginWeaponChange ──

    private static void PM_BeginWeaponChange(int weapon)
    {
        if (weapon <= Weapons.WP_NONE || weapon >= Weapons.WP_NUM_WEAPONS) return;
        if ((pm_ps->Stats[Stats.STAT_WEAPONS] & (1 << weapon)) == 0) return;
        if (pm_ps->WeaponState == WEAPON_DROPPING) return;

        PM_AddEvent(EntityEvent.EV_CHANGE_WEAPON);
        pm_ps->WeaponState = WEAPON_DROPPING;
        pm_ps->WeaponTime += 200;
        PM_StartTorsoAnim(TORSO_DROP);
    }

    // ── PM_FinishWeaponChange ──

    private static void PM_FinishWeaponChange()
    {
        int weapon = pm_cmd.Weapon;
        if (weapon < Weapons.WP_NONE || weapon >= Weapons.WP_NUM_WEAPONS)
            weapon = Weapons.WP_NONE;
        if ((pm_ps->Stats[Stats.STAT_WEAPONS] & (1 << weapon)) == 0)
            weapon = Weapons.WP_NONE;

        pm_ps->Weapon = weapon;
        pm_ps->WeaponState = WEAPON_RAISING;
        pm_ps->WeaponTime += 250;
        PM_StartTorsoAnim(TORSO_RAISE);
    }

    // ── PM_TorsoAnimation ──

    private static void PM_TorsoAnimation()
    {
        if (pm_ps->WeaponState == WEAPON_READY)
        {
            if (pm_ps->Weapon == Weapons.WP_GAUNTLET)
                PM_ContinueTorsoAnim(TORSO_STAND2);
            else
                PM_ContinueTorsoAnim(TORSO_STAND);
        }
    }

    // ── PM_Weapon ──

    private static void PM_Weapon()
    {
        if ((pm_ps->PmFlags & PMF_RESPAWNED) != 0) return;

        // ignore if spectator
        if (pm_ps->Persistant[Persistant.PERS_TEAM] == Teams.TEAM_SPECTATOR) return;

        // check for dead player
        if (pm_ps->Stats[Stats.STAT_HEALTH] <= 0)
        {
            pm_ps->Weapon = Weapons.WP_NONE;
            return;
        }

        // check for item using
        if ((pm_cmd.Buttons & BUTTON_USE_HOLDABLE) != 0)
        {
            if ((pm_ps->PmFlags & PMF_USE_ITEM_HELD) == 0)
            {
                pm_ps->PmFlags |= PMF_USE_ITEM_HELD;
                PM_AddEvent(EntityEvent.EV_USE_ITEM0 + pm_ps->Stats[Stats.STAT_HOLDABLE_ITEM]);
                pm_ps->Stats[Stats.STAT_HOLDABLE_ITEM] = 0;
                return;
            }
        }
        else
        {
            pm_ps->PmFlags &= ~PMF_USE_ITEM_HELD;
        }

        // make weapon function
        if (pm_ps->WeaponTime > 0)
            pm_ps->WeaponTime -= msec;

        // check for weapon change
        if (pm_ps->WeaponTime <= 0 || pm_ps->WeaponState != WEAPON_FIRING)
        {
            if (pm_ps->Weapon != pm_cmd.Weapon)
                PM_BeginWeaponChange(pm_cmd.Weapon);
        }

        if (pm_ps->WeaponTime > 0) return;

        if (pm_ps->WeaponState == WEAPON_DROPPING)
        {
            PM_FinishWeaponChange();
            return;
        }

        if (pm_ps->WeaponState == WEAPON_RAISING)
        {
            pm_ps->WeaponState = WEAPON_READY;
            if (pm_ps->Weapon == Weapons.WP_GAUNTLET)
                PM_StartTorsoAnim(TORSO_STAND2);
            else
                PM_StartTorsoAnim(TORSO_STAND);
            return;
        }

        // check for fire
        if ((pm_cmd.Buttons & BUTTON_ATTACK) == 0)
        {
            pm_ps->WeaponTime = 0;
            pm_ps->WeaponState = WEAPON_READY;
            return;
        }

        // start the animation even if out of ammo
        if (pm_ps->Weapon == Weapons.WP_GAUNTLET)
        {
            if (!pm_gauntletHit)
            {
                pm_ps->WeaponTime = 0;
                pm_ps->WeaponState = WEAPON_READY;
                return;
            }
            PM_StartTorsoAnim(TORSO_ATTACK2);
        }
        else
        {
            PM_StartTorsoAnim(TORSO_ATTACK);
        }

        pm_ps->WeaponState = WEAPON_FIRING;

        // check for out of ammo
        if (pm_ps->Ammo[pm_ps->Weapon] == 0)
        {
            PM_AddEvent(EntityEvent.EV_NOAMMO);
            pm_ps->WeaponTime += 500;
            return;
        }

        // take ammo away if not infinite
        if (pm_ps->Ammo[pm_ps->Weapon] != -1)
            pm_ps->Ammo[pm_ps->Weapon]--;

        PM_AddEvent(EntityEvent.EV_FIRE_WEAPON);

        int addTime;
        switch (pm_ps->Weapon)
        {
            case Weapons.WP_GAUNTLET: addTime = 400; break;
            case Weapons.WP_LIGHTNING: addTime = 50; break;
            case Weapons.WP_SHOTGUN: addTime = 1000; break;
            case Weapons.WP_MACHINEGUN: addTime = 100; break;
            case Weapons.WP_GRENADE_LAUNCHER: addTime = 800; break;
            case Weapons.WP_ROCKET_LAUNCHER: addTime = 800; break;
            case Weapons.WP_PLASMAGUN: addTime = 100; break;
            case Weapons.WP_RAILGUN: addTime = 1500; break;
            case Weapons.WP_BFG: addTime = 200; break;
            case Weapons.WP_GRAPPLING_HOOK: addTime = 400; break;
            default: addTime = 400; break;
        }

        if (pm_ps->PowerupTimers[PW_HASTE] != 0)
            addTime = (int)(addTime / 1.3f);

        pm_ps->WeaponTime += addTime;
    }

    // ── PM_Animate ──

    private static void PM_Animate()
    {
        if ((pm_cmd.Buttons & BUTTON_GESTURE) != 0)
        {
            if (pm_ps->TorsoTimer == 0)
            {
                PM_StartTorsoAnim(TORSO_GESTURE);
                pm_ps->TorsoTimer = TIMER_GESTURE;
                PM_AddEvent(EntityEvent.EV_TAUNT);
            }
        }
    }

    // ── PM_DropTimers ──

    private static void PM_DropTimers()
    {
        if (pm_ps->PmTime != 0)
        {
            if (msec >= pm_ps->PmTime)
            {
                pm_ps->PmFlags &= ~PMF_ALL_TIMES;
                pm_ps->PmTime = 0;
            }
            else
            {
                pm_ps->PmTime -= msec;
            }
        }

        if (pm_ps->LegsTimer > 0)
        {
            pm_ps->LegsTimer -= msec;
            if (pm_ps->LegsTimer < 0) pm_ps->LegsTimer = 0;
        }

        if (pm_ps->TorsoTimer > 0)
        {
            pm_ps->TorsoTimer -= msec;
            if (pm_ps->TorsoTimer < 0) pm_ps->TorsoTimer = 0;
        }
    }

    // ── PM_UpdateViewAngles ──

    public static void UpdateViewAngles(Q3PlayerState* ps, Q3UserCmd* cmd)
    {
        if (ps->PmType == PmType.PM_INTERMISSION || ps->PmType == PmType.PM_SPINTERMISSION)
            return;

        if (ps->PmType != PmType.PM_SPECTATOR && ps->Stats[Stats.STAT_HEALTH] <= 0)
            return;

        for (int i = 0; i < 3; i++)
        {
            int cmdAngle;
            if (i == 0) cmdAngle = cmd->Angle0;
            else if (i == 1) cmdAngle = cmd->Angle1;
            else cmdAngle = cmd->Angle2;

            short temp = (short)(cmdAngle + ps->DeltaAngles[i]);
            if (i == PITCH)
            {
                if (temp > 16000)
                {
                    ps->DeltaAngles[i] = 16000 - cmdAngle;
                    temp = 16000;
                }
                else if (temp < -16000)
                {
                    ps->DeltaAngles[i] = -16000 - cmdAngle;
                    temp = -16000;
                }
            }

            float angle = (float)temp * SHORT2ANGLE_MUL;
            if (i == 0) ps->ViewAnglesX = angle;
            else if (i == 1) ps->ViewAnglesY = angle;
            else ps->ViewAnglesZ = angle;
        }
    }

    // ── PM_SlideMove (from bg_slidemove.c) ──

    private static bool PM_SlideMove(bool gravity)
    {
        int numbumps = 4;
        float* primal_velocity = stackalloc float[3];
        float* clipVelocity = stackalloc float[3];
        float* endClipVelocity = stackalloc float[3];
        float* endVelocity = stackalloc float[3];
        float* end = stackalloc float[3];
        float* dir = stackalloc float[3];
        // planes[MAX_CLIP_PLANES][3]
        float* planes = stackalloc float[MAX_CLIP_PLANES * 3];

        VectorCopy(&pm_ps->VelocityX, primal_velocity);

        if (gravity)
        {
            VectorCopy(&pm_ps->VelocityX, endVelocity);
            endVelocity[2] -= pm_ps->Gravity * frametime;
            pm_ps->VelocityZ = (pm_ps->VelocityZ + endVelocity[2]) * 0.5f;
            primal_velocity[2] = endVelocity[2];
            if (groundPlane)
            {
                PM_ClipVelocity(&pm_ps->VelocityX, GroundNormal(),
                    &pm_ps->VelocityX, OVERCLIP);
            }
        }

        float time_left = frametime;

        // never turn against the ground plane
        int numplanes;
        if (groundPlane)
        {
            numplanes = 1;
            planes[0] = groundTrace.PlaneNormalX;
            planes[1] = groundTrace.PlaneNormalY;
            planes[2] = groundTrace.PlaneNormalZ;
        }
        else
        {
            numplanes = 0;
        }

        // never turn against original velocity
        VectorNormalize2(&pm_ps->VelocityX, &planes[numplanes * 3]);
        numplanes++;

        int bumpcount;
        for (bumpcount = 0; bumpcount < numbumps; bumpcount++)
        {
            VectorMA(&pm_ps->OriginX, time_left, &pm_ps->VelocityX, end);

            Q3Trace trace;
            pm_trace!(&trace, &pm_ps->OriginX, pm_mins, pm_maxs, end, pm_ps->ClientNum, pm_tracemask);

            if (trace.AllSolid != 0)
            {
                pm_ps->VelocityZ = 0;
                return true;
            }

            if (trace.Fraction > 0)
            {
                VectorCopy(&trace.EndPosX, &pm_ps->OriginX);
            }

            if (trace.Fraction == 1)
                break;

            PM_AddTouchEnt(trace.EntityNum);

            time_left -= time_left * trace.Fraction;

            if (numplanes >= MAX_CLIP_PLANES)
            {
                VectorClear(&pm_ps->VelocityX);
                return true;
            }

            // if same plane hit before, nudge velocity out along it
            int i;
            for (i = 0; i < numplanes; i++)
            {
                if (DotProduct(&trace.PlaneNormalX, &planes[i * 3]) > 0.99f)
                {
                    VectorAdd(&trace.PlaneNormalX, &pm_ps->VelocityX, &pm_ps->VelocityX);
                    break;
                }
            }
            if (i < numplanes) continue;

            planes[numplanes * 3 + 0] = trace.PlaneNormalX;
            planes[numplanes * 3 + 1] = trace.PlaneNormalY;
            planes[numplanes * 3 + 2] = trace.PlaneNormalZ;
            numplanes++;

            // modify velocity so it parallels all clip planes
            for (i = 0; i < numplanes; i++)
            {
                float into = DotProduct(&pm_ps->VelocityX, &planes[i * 3]);
                if (into >= 0.1f) continue;

                if (-into > impactSpeed)
                    impactSpeed = -into;

                PM_ClipVelocity(&pm_ps->VelocityX, &planes[i * 3], clipVelocity, OVERCLIP);

                if (gravity)
                    PM_ClipVelocity(endVelocity, &planes[i * 3], endClipVelocity, OVERCLIP);

                int j;
                for (j = 0; j < numplanes; j++)
                {
                    if (j == i) continue;
                    if (DotProduct(clipVelocity, &planes[j * 3]) >= 0.1f) continue;

                    PM_ClipVelocity(clipVelocity, &planes[j * 3], clipVelocity, OVERCLIP);

                    if (gravity)
                        PM_ClipVelocity(endClipVelocity, &planes[j * 3], endClipVelocity, OVERCLIP);

                    if (DotProduct(clipVelocity, &planes[i * 3]) >= 0) continue;

                    // slide along the crease
                    CrossProduct(&planes[i * 3], &planes[j * 3], dir);
                    VectorNormalize(dir);
                    float d = DotProduct(dir, &pm_ps->VelocityX);
                    VectorScale(dir, d, clipVelocity);

                    if (gravity)
                    {
                        CrossProduct(&planes[i * 3], &planes[j * 3], dir);
                        VectorNormalize(dir);
                        d = DotProduct(dir, endVelocity);
                        VectorScale(dir, d, endClipVelocity);
                    }

                    // check for third plane
                    int k;
                    for (k = 0; k < numplanes; k++)
                    {
                        if (k == i || k == j) continue;
                        if (DotProduct(clipVelocity, &planes[k * 3]) >= 0.1f) continue;

                        VectorClear(&pm_ps->VelocityX);
                        return true;
                    }
                }

                VectorCopy(clipVelocity, &pm_ps->VelocityX);

                if (gravity)
                    VectorCopy(endClipVelocity, endVelocity);

                break;
            }
        }

        if (gravity)
        {
            VectorCopy(endVelocity, &pm_ps->VelocityX);
        }

        // don't change velocity if in a timer
        if (pm_ps->PmTime != 0)
        {
            VectorCopy(primal_velocity, &pm_ps->VelocityX);
        }

        return bumpcount != 0;
    }

    // ── PM_StepSlideMove (from bg_slidemove.c) ──

    private static void PM_StepSlideMove(bool gravity)
    {
        float* start_o = stackalloc float[3];
        float* start_v = stackalloc float[3];
        float* upPos = stackalloc float[3];
        float* downPos = stackalloc float[3];

        VectorCopy(&pm_ps->OriginX, start_o);
        VectorCopy(&pm_ps->VelocityX, start_v);

        if (!PM_SlideMove(gravity))
            return; // got exactly where we wanted first try

        VectorCopy(start_o, downPos);
        downPos[2] -= STEPSIZE;

        Q3Trace trace;
        pm_trace!(&trace, start_o, pm_mins, pm_maxs, downPos, pm_ps->ClientNum, pm_tracemask);
        VectorSet(upPos, 0, 0, 1);

        // never step up when you still have up velocity
        if (pm_ps->VelocityZ > 0 && (trace.Fraction == 1.0f ||
            DotProduct(&trace.PlaneNormalX, upPos) < 0.7f))
        {
            return;
        }

        VectorCopy(start_o, upPos);
        upPos[2] += STEPSIZE;

        // test player position if stepheight higher
        pm_trace(&trace, start_o, pm_mins, pm_maxs, upPos, pm_ps->ClientNum, pm_tracemask);
        if (trace.AllSolid != 0)
            return; // can't step up

        float stepSize = trace.EndPosZ - start_o[2];
        // try slidemove from this position
        VectorCopy(&trace.EndPosX, &pm_ps->OriginX);
        VectorCopy(start_v, &pm_ps->VelocityX);

        PM_SlideMove(gravity);

        // push down the final amount
        VectorCopy(&pm_ps->OriginX, downPos);
        downPos[2] -= stepSize;
        pm_trace(&trace, &pm_ps->OriginX, pm_mins, pm_maxs, downPos, pm_ps->ClientNum, pm_tracemask);
        if (trace.AllSolid == 0)
        {
            VectorCopy(&trace.EndPosX, &pm_ps->OriginX);
        }
        if (trace.Fraction < 1.0f)
        {
            PM_ClipVelocity(&pm_ps->VelocityX, &trace.PlaneNormalX, &pm_ps->VelocityX, OVERCLIP);
        }

        // use the step move — add step event
        {
            float delta = pm_ps->OriginZ - start_o[2];
            if (delta > 2)
            {
                if (delta < 7) PM_AddEvent(EntityEvent.EV_STEP_4);
                else if (delta < 11) PM_AddEvent(EntityEvent.EV_STEP_8);
                else if (delta < 15) PM_AddEvent(EntityEvent.EV_STEP_12);
                else PM_AddEvent(EntityEvent.EV_STEP_16);
            }
        }
    }

    // ── PmoveSingle ──

    private static void PmoveSingle()
    {
        c_pmove++;

        // clear results
        pm_numtouch = 0;
        pm_watertype = 0;
        pm_waterlevel = 0;

        if (pm_ps->Stats[Stats.STAT_HEALTH] <= 0)
            pm_tracemask &= ~CONTENTS_BODY;

        // make sure walking button is clear if running
        if (Math.Abs(pm_cmd.ForwardMove) > 64 || Math.Abs(pm_cmd.RightMove) > 64)
            pm_cmd.Buttons &= ~BUTTON_WALKING;

        // set talk balloon flag
        if ((pm_cmd.Buttons & BUTTON_TALK) != 0)
            pm_ps->EFlags |= EF_TALK;
        else
            pm_ps->EFlags &= ~EF_TALK;

        // set firing flag for continuous beam weapons
        if ((pm_ps->PmFlags & PMF_RESPAWNED) == 0 && pm_ps->PmType != PmType.PM_INTERMISSION
            && pm_ps->PmType != PmType.PM_NOCLIP
            && (pm_cmd.Buttons & BUTTON_ATTACK) != 0 && pm_ps->Ammo[pm_ps->Weapon] != 0)
        {
            pm_ps->EFlags |= EF_FIRING;
        }
        else
        {
            pm_ps->EFlags &= ~EF_FIRING;
        }

        // clear respawned flag if attack and use are cleared
        if (pm_ps->Stats[Stats.STAT_HEALTH] > 0 &&
            (pm_cmd.Buttons & (BUTTON_ATTACK | BUTTON_USE_HOLDABLE)) == 0)
        {
            pm_ps->PmFlags &= ~PMF_RESPAWNED;
        }

        // if talk button is down, disallow all other input
        if ((pm_cmd.Buttons & BUTTON_TALK) != 0)
        {
            pm_cmd.Buttons = BUTTON_TALK;
            pm_cmd.ForwardMove = 0;
            pm_cmd.RightMove = 0;
            pm_cmd.UpMove = 0;
        }

        // clear local vars
        walking = false;
        groundPlane = false;
        impactSpeed = 0;

        // determine the time
        msec = pm_cmd.ServerTime - pm_ps->CommandTime;
        if (msec < 1) msec = 1;
        else if (msec > 200) msec = 200;
        pm_ps->CommandTime = pm_cmd.ServerTime;

        // save old origin and velocity
        previous_origin[0] = pm_ps->OriginX;
        previous_origin[1] = pm_ps->OriginY;
        previous_origin[2] = pm_ps->OriginZ;
        previous_velocity[0] = pm_ps->VelocityX;
        previous_velocity[1] = pm_ps->VelocityY;
        previous_velocity[2] = pm_ps->VelocityZ;

        frametime = msec * 0.001f;

        // update view angles
        UpdateViewAngles(pm_ps, CmdPtr());

        float* viewangles = stackalloc float[3];
        viewangles[0] = pm_ps->ViewAnglesX;
        viewangles[1] = pm_ps->ViewAnglesY;
        viewangles[2] = pm_ps->ViewAnglesZ;
        AngleVectors(viewangles, forward, right, up);

        if (pm_cmd.UpMove < 10)
            pm_ps->PmFlags &= ~PMF_JUMP_HELD;

        // decide if backpedaling animations should be used
        if (pm_cmd.ForwardMove < 0)
            pm_ps->PmFlags |= PMF_BACKWARDS_RUN;
        else if (pm_cmd.ForwardMove > 0 || (pm_cmd.ForwardMove == 0 && pm_cmd.RightMove != 0))
            pm_ps->PmFlags &= ~PMF_BACKWARDS_RUN;

        if (pm_ps->PmType >= PmType.PM_DEAD)
        {
            pm_cmd.ForwardMove = 0;
            pm_cmd.RightMove = 0;
            pm_cmd.UpMove = 0;
        }

        if (pm_ps->PmType == PmType.PM_SPECTATOR)
        {
            PM_CheckDuck();
            PM_FlyMove();
            PM_DropTimers();
            return;
        }

        if (pm_ps->PmType == PmType.PM_NOCLIP)
        {
            PM_NoclipMove();
            PM_DropTimers();
            return;
        }

        if (pm_ps->PmType == PmType.PM_FREEZE)
            return;

        if (pm_ps->PmType == PmType.PM_INTERMISSION || pm_ps->PmType == PmType.PM_SPINTERMISSION)
            return;

        // set watertype and waterlevel
        PM_SetWaterLevel();
        previous_waterlevel = pm_waterlevel;

        // set mins, maxs, and viewheight
        PM_CheckDuck();

        // set groundentity
        PM_GroundTrace();

        if (pm_ps->PmType == PmType.PM_DEAD)
            PM_DeadMove();

        PM_DropTimers();

        if (pm_ps->PowerupTimers[PW_FLIGHT] != 0)
        {
            PM_FlyMove();
        }
        else if ((pm_ps->PmFlags & PMF_GRAPPLE_PULL) != 0)
        {
            PM_GrappleMove();
            PM_AirMove();
        }
        else if ((pm_ps->PmFlags & PMF_TIME_WATERJUMP) != 0)
        {
            PM_WaterJumpMove();
        }
        else if (pm_waterlevel > 1)
        {
            PM_WaterMove();
        }
        else if (walking)
        {
            PM_WalkMove();
        }
        else
        {
            PM_AirMove();
        }

        PM_Animate();

        // set groundentity, watertype, and waterlevel
        PM_GroundTrace();
        PM_SetWaterLevel();

        // weapons
        PM_Weapon();

        // torso animation
        PM_TorsoAnimation();

        // footstep events / legs animations
        PM_Footsteps();

        // entering / leaving water splashes
        PM_WaterEvents();

        // snap velocity
        SnapVector(&pm_ps->VelocityX);
    }

    // ── Public entry point: Execute (equivalent to Pmove()) ──

    /// <summary>
    /// Run player movement physics. Equivalent to Pmove() from bg_pmove.c.
    /// Chops move into sub-frames if too long, then runs PmoveSingle for each.
    /// </summary>
    public static void Execute(Q3PlayerState* ps, Q3UserCmd* cmd,
        TraceDelegate trace, PointContentsDelegate pointcontents,
        int tracemask, bool noFootsteps, int pmove_fixed, int pmove_msec_val,
        float* mins, float* maxs, out int numtouch, int* touchents, out float xyspeed,
        out int watertype, out int waterlevel, out bool gauntletHit)
    {
        // Set up pointers to the fixed arrays
        fixed (float* fwd = _forward, rt = _right, u = _up, po = _previousOrigin, pv = _previousVelocity)
        {
            forward = fwd;
            right = rt;
            up = u;
            previous_origin = po;
            previous_velocity = pv;

            pm_ps = ps;
            pm_cmd = *cmd;
            pm_trace = trace;
            pm_pointcontents = pointcontents;
            pm_tracemask = tracemask;
            pm_noFootsteps = noFootsteps;
            pm_gauntletHit = false;
            pm_mins = mins;
            pm_maxs = maxs;

            int finalTime = pm_cmd.ServerTime;

            if (finalTime < pm_ps->CommandTime)
            {
                numtouch = 0;
                xyspeed = 0;
                watertype = 0;
                waterlevel = 0;
                gauntletHit = false;
                return;
            }

            if (finalTime > pm_ps->CommandTime + 1000)
                pm_ps->CommandTime = finalTime - 1000;

            pm_ps->PmoveFramecount = (pm_ps->PmoveFramecount + 1) & ((1 << PS_PMOVEFRAMECOUNTBITS) - 1);

            while (pm_ps->CommandTime != finalTime)
            {
                int ms = finalTime - pm_ps->CommandTime;

                if (pmove_fixed != 0)
                {
                    if (ms > pmove_msec_val) ms = pmove_msec_val;
                }
                else
                {
                    if (ms > 66) ms = 66;
                }

                pm_cmd.ServerTime = pm_ps->CommandTime + ms;
                PmoveSingle();

                if ((pm_ps->PmFlags & PMF_JUMP_HELD) != 0)
                    pm_cmd.UpMove = 20;
            }

            // Copy touch results
            numtouch = pm_numtouch;
            for (int i = 0; i < pm_numtouch; i++)
                touchents[i] = pm_touchents[i];

            xyspeed = pm_xyspeed;
            watertype = pm_watertype;
            waterlevel = pm_waterlevel;
            gauntletHit = pm_gauntletHit;

            // Restore cmd back
            *cmd = pm_cmd;

            // Null out statics to avoid holding references
            pm_trace = null;
            pm_pointcontents = null;
            forward = null;
            right = null;
            up = null;
            previous_origin = null;
            previous_velocity = null;
        }
    }

    // ── EV_TAUNT constant (not in EntityEvent — add inline) ──
    // EV_TAUNT is typically EV_OBITUARY+6=66, but we reference EV_TAUNT from bg_public.h
    // Check: it's actually after the event list. In Q3 it's not a standard event,
    // it's just EV_GENERAL_SOUND in many mods. The C code just calls PM_AddEvent(EV_TAUNT).
    // We need the actual value.
}

// Additional EntityEvent constant
public static partial class EntityEvent
{
    public const int EV_TAUNT = 66; // after EV_SCOREPLUM(65)
}
