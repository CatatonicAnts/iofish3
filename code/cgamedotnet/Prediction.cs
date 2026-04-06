using System.Runtime.CompilerServices;

namespace CGameDotNet;

/// <summary>
/// Port of cg_predict.c — client-side prediction system.
/// Generates predictedPlayerState by running pmove on unacknowledged usercmds.
/// </summary>
public static unsafe class Prediction
{
    // ── Constants ──
    private const int CMD_BACKUP = 64;
    private const int SOLID_BMODEL = 0xffffff;
    private const int MAX_ENTITIES_IN_SNAPSHOT = 256;
    private const int MAX_GENTITIES = 1024;

    // Content flags
    private const int CONTENTS_SOLID = 1;
    private const int CONTENTS_BODY = 0x2000000;
    private const int CONTENTS_PLAYERCLIP = 0x10000;
    private const int MASK_PLAYERSOLID = CONTENTS_SOLID | CONTENTS_PLAYERCLIP | CONTENTS_BODY;
    private const int MAX_PS_EVENTS = 2;

    // ── Solid entity tracking ──
    // Stores data about solid entities needed for clipping
    public struct SolidEntity
    {
        public int Number;
        public int Solid;
        public int ModelIndex;
        public float OriginX, OriginY, OriginZ;
        public float AnglesX, AnglesY, AnglesZ;
    }

    // Lists populated by BuildSolidList
    private static SolidEntity[] _solidEntities = new SolidEntity[MAX_ENTITIES_IN_SNAPSHOT];
    private static int _numSolidEntities;
    private static int[] _triggerEntityNums = new int[MAX_ENTITIES_IN_SNAPSHOT];
    private static int _numTriggerEntities;

    // Predicted player state
    public static Q3PlayerState PredictedPlayerState;
    private static bool _validPPS;
    private static int _physicsTime;

    // Prediction error smoothing
    private static float _predictedErrorX, _predictedErrorY, _predictedErrorZ;
    private static int _predictedErrorTime;
    public static bool Hyperspace;

    // Pmove bounds (set during prediction, used for trigger checking)
    private static float[] _pmoveMins = new float[3];
    private static float[] _pmoveMaxs = new float[3];

    // ── Delegate for getting solid entity data from CGame ──
    public delegate int GetSolidEntitiesDelegate(SolidEntity* buffer, int maxEntities);
    public delegate int GetTriggerEntitiesDelegate(int* buffer, int maxEntities);

    // ── Vector helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorCopy(float* src, float* dst)
    {
        dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorSubtract(float* a, float* b, float* o)
    {
        o[0] = a[0] - b[0]; o[1] = a[1] - b[1]; o[2] = a[2] - b[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorAdd(float* a, float* b, float* o)
    {
        o[0] = a[0] + b[0]; o[1] = a[1] + b[1]; o[2] = a[2] + b[2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorScale(float* v, float s, float* o)
    {
        o[0] = v[0] * s; o[1] = v[1] * s; o[2] = v[2] * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorClear(float* v)
    {
        v[0] = 0; v[1] = 0; v[2] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VectorLength(float* v) =>
        MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VectorCompare(float* a, float* b) =>
        a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

    private static float LerpAngle(float from, float to, float frac)
    {
        float a = to - from;
        if (a > 180) a -= 360;
        if (a < -180) a += 360;
        return from + frac * a;
    }

    // ── BuildSolidList ──

    /// <summary>
    /// Build the list of solid entities from the current snapshot.
    /// Called by CGame when a new snap is set, passing entity data via the delegate.
    /// </summary>
    public static void BuildSolidList(GetSolidEntitiesDelegate getSolids, GetTriggerEntitiesDelegate getTriggers)
    {
        fixed (SolidEntity* buf = _solidEntities)
            _numSolidEntities = getSolids(buf, MAX_ENTITIES_IN_SNAPSHOT);
        fixed (int* buf = _triggerEntityNums)
            _numTriggerEntities = getTriggers(buf, MAX_ENTITIES_IN_SNAPSHOT);
    }

    /// <summary>
    /// Overload for direct population (used when CGame fills arrays directly).
    /// </summary>
    public static void SetSolidEntities(SolidEntity[] entities, int count)
    {
        _numSolidEntities = Math.Min(count, MAX_ENTITIES_IN_SNAPSHOT);
        Array.Copy(entities, _solidEntities, _numSolidEntities);
    }

    public static void SetTriggerEntities(int[] entityNums, int count)
    {
        _numTriggerEntities = Math.Min(count, MAX_ENTITIES_IN_SNAPSHOT);
        Array.Copy(entityNums, _triggerEntityNums, _numTriggerEntities);
    }

    // ── CG_ClipMoveToEntities ──

    private static void ClipMoveToEntities(float* start, float* mins, float* maxs, float* end,
        int skipNumber, int mask, Q3Trace* tr)
    {
        float* origin = stackalloc float[3];
        float* angles = stackalloc float[3];
        float* bmins = stackalloc float[3];
        float* bmaxs = stackalloc float[3];

        for (int i = 0; i < _numSolidEntities; i++)
        {
            ref SolidEntity se = ref _solidEntities[i];

            if (se.Number == skipNumber) continue;

            Q3Trace trace;

            if (se.Solid == SOLID_BMODEL)
            {
                int cmodel = Syscalls.CM_InlineModel(se.ModelIndex);
                angles[0] = se.AnglesX; angles[1] = se.AnglesY; angles[2] = se.AnglesZ;
                origin[0] = se.OriginX; origin[1] = se.OriginY; origin[2] = se.OriginZ;
                Syscalls.CM_TransformedBoxTrace(&trace, start, end, mins, maxs, cmodel, mask, origin, angles);
            }
            else
            {
                // Encoded bbox
                int x = se.Solid & 255;
                int zd = (se.Solid >> 8) & 255;
                int zu = ((se.Solid >> 16) & 255) - 32;

                bmins[0] = bmins[1] = -x;
                bmaxs[0] = bmaxs[1] = x;
                bmins[2] = -zd;
                bmaxs[2] = zu;

                int cmodel = Syscalls.CM_TempBoxModel(bmins, bmaxs);
                angles[0] = 0; angles[1] = 0; angles[2] = 0;
                origin[0] = se.OriginX; origin[1] = se.OriginY; origin[2] = se.OriginZ;
                Syscalls.CM_TransformedBoxTrace(&trace, start, end, mins, maxs, cmodel, mask, origin, angles);
            }

            if (trace.AllSolid != 0 || trace.Fraction < tr->Fraction)
            {
                trace.EntityNum = se.Number;
                *tr = trace;
            }
            else if (trace.StartSolid != 0)
            {
                tr->StartSolid = 1;
            }

            if (tr->AllSolid != 0) return;
        }
    }

    // ── CG_Trace ──

    /// <summary>
    /// Trace against world + solid entities. Matches CG_Trace from cg_predict.c.
    /// </summary>
    public static void Trace(Q3Trace* result, float* start, float* mins, float* maxs, float* end,
        int skipNumber, int mask)
    {
        Q3Trace t;
        Syscalls.CM_BoxTrace(&t, start, end, mins, maxs, 0, mask);
        t.EntityNum = t.Fraction != 1.0f ? EntityNum.ENTITYNUM_WORLD : EntityNum.ENTITYNUM_NONE;

        ClipMoveToEntities(start, mins, maxs, end, skipNumber, mask, &t);

        *result = t;
    }

    // ── CG_PointContents ──

    /// <summary>
    /// Point contents against world + solid bmodel entities.
    /// </summary>
    public static int PointContents(float* point, int passEntityNum)
    {
        int contents = Syscalls.CM_PointContents(point, 0);

        float* origin = stackalloc float[3];
        float* angles = stackalloc float[3];

        for (int i = 0; i < _numSolidEntities; i++)
        {
            ref SolidEntity se = ref _solidEntities[i];
            if (se.Number == passEntityNum) continue;
            if (se.Solid != SOLID_BMODEL) continue;

            int cmodel = Syscalls.CM_InlineModel(se.ModelIndex);
            if (cmodel == 0) continue;

            origin[0] = se.OriginX; origin[1] = se.OriginY; origin[2] = se.OriginZ;
            angles[0] = se.AnglesX; angles[1] = se.AnglesY; angles[2] = se.AnglesZ;

            contents |= Syscalls.CM_TransformedPointContents(point, cmodel, origin, angles);
        }

        return contents;
    }

    // ── Trace/PointContents callbacks for PMove ──

    private static void TraceCallback(Q3Trace* results, float* start, float* mins, float* maxs,
        float* end, int passEntityNum, int contentMask)
    {
        Trace(results, start, mins, maxs, end, passEntityNum, contentMask);
    }

    private static int PointContentsCallback(float* point, int passEntityNum)
    {
        return PointContents(point, passEntityNum);
    }

    // Keep delegate instances alive to prevent GC
    private static readonly PMove.TraceDelegate _traceDelegate = TraceCallback;
    private static readonly PMove.PointContentsDelegate _pointContentsDelegate = PointContentsCallback;

    // ── InterpolatePlayerState ──

    /// <summary>
    /// Non-prediction fallback: interpolate between snap and nextSnap.
    /// </summary>
    public static void InterpolatePlayerState(bool grabAngles,
        Q3PlayerState* snapPs, Q3PlayerState* nextSnapPs, bool hasNextSnap,
        int snapServerTime, int nextSnapServerTime, int cgTime,
        bool nextFrameTeleport)
    {
        PredictedPlayerState = *snapPs;

        if (grabAngles)
        {
            int cmdNum = Syscalls.GetCurrentCmdNumber();
            Q3UserCmd cmd;
            Syscalls.GetUserCmd(cmdNum, &cmd);
            fixed (Q3PlayerState* pps = &PredictedPlayerState)
                PMove.UpdateViewAngles(pps, &cmd);
        }

        if (nextFrameTeleport) return;
        if (!hasNextSnap || nextSnapServerTime <= snapServerTime) return;

        float f = (float)(cgTime - snapServerTime) / (nextSnapServerTime - snapServerTime);

        int bob = nextSnapPs->BobCycle;
        if (bob < snapPs->BobCycle) bob += 256;
        PredictedPlayerState.BobCycle = (int)(snapPs->BobCycle + f * (bob - snapPs->BobCycle));

        // Interpolate origin, velocity, viewangles
        PredictedPlayerState.OriginX = snapPs->OriginX + f * (nextSnapPs->OriginX - snapPs->OriginX);
        PredictedPlayerState.OriginY = snapPs->OriginY + f * (nextSnapPs->OriginY - snapPs->OriginY);
        PredictedPlayerState.OriginZ = snapPs->OriginZ + f * (nextSnapPs->OriginZ - snapPs->OriginZ);

        PredictedPlayerState.VelocityX = snapPs->VelocityX + f * (nextSnapPs->VelocityX - snapPs->VelocityX);
        PredictedPlayerState.VelocityY = snapPs->VelocityY + f * (nextSnapPs->VelocityY - snapPs->VelocityY);
        PredictedPlayerState.VelocityZ = snapPs->VelocityZ + f * (nextSnapPs->VelocityZ - snapPs->VelocityZ);

        if (!grabAngles)
        {
            PredictedPlayerState.ViewAnglesX = LerpAngle(snapPs->ViewAnglesX, nextSnapPs->ViewAnglesX, f);
            PredictedPlayerState.ViewAnglesY = LerpAngle(snapPs->ViewAnglesY, nextSnapPs->ViewAnglesY, f);
            PredictedPlayerState.ViewAnglesZ = LerpAngle(snapPs->ViewAnglesZ, nextSnapPs->ViewAnglesZ, f);
        }
    }

    // ── AdjustPositionForMover ──

    /// <summary>
    /// Adjust player origin/angles for mover (platform) movement between two times.
    /// In the C code this evaluates the mover's trajectory at both old and new times
    /// and applies the delta to the player. For now we provide a simplified version
    /// that can be expanded when mover entity data is accessible.
    /// </summary>
    public static void AdjustPositionForMover(float* oldOrigin, int groundEntityNum,
        int fromTime, int toTime, float* outOrigin, float* oldAngles, float* outAngles)
    {
        // If not on a mover, just copy through
        VectorCopy(oldOrigin, outOrigin);
        outAngles[0] = oldAngles[0];
        outAngles[1] = oldAngles[1];
        outAngles[2] = oldAngles[2];

        if (groundEntityNum == EntityNum.ENTITYNUM_NONE || groundEntityNum == EntityNum.ENTITYNUM_WORLD)
            return;

        // Full mover adjustment requires access to entity trajectory data.
        // This will be populated when CGame exposes mover entity lookup.
        // For now, non-mover entities pass through unchanged — this is correct
        // for most gameplay scenarios (only platforms/elevators need adjustment).
    }

    // ── PredictPlayerState ──

    /// <summary>
    /// Main prediction entry point. Called each frame by CGame.
    /// Generates PredictedPlayerState from snapshot + predicted usercmds.
    /// </summary>
    /// <param name="snapPs">Current snapshot player state</param>
    /// <param name="nextSnapPs">Next snapshot player state (null if unavailable)</param>
    /// <param name="hasNextSnap">Whether nextSnapPs is valid</param>
    /// <param name="snapServerTime">Current snap server time</param>
    /// <param name="nextSnapServerTime">Next snap server time</param>
    /// <param name="cgTime">Current client time</param>
    /// <param name="cgOldTime">Previous frame client time</param>
    /// <param name="demoPlayback">True if playing a demo</param>
    /// <param name="pmFlags">PMF_FOLLOW check (from snap playerstate)</param>
    /// <param name="nextFrameTeleport">True if next frame is teleport</param>
    /// <param name="thisFrameTeleport">True if this frame is teleport</param>
    /// <param name="noPredict">cg_nopredict value</param>
    /// <param name="synchronousClients">cg_synchronousClients value</param>
    /// <param name="dmflags">Server dmflags</param>
    /// <param name="pmoveFixed">pmove_fixed cvar value</param>
    /// <param name="pmoveMsec">pmove_msec cvar value</param>
    /// <param name="team">Player team</param>
    public static void PredictPlayerState(
        Q3PlayerState* snapPs, Q3PlayerState* nextSnapPs, bool hasNextSnap,
        int snapServerTime, int nextSnapServerTime,
        int cgTime, int cgOldTime,
        bool demoPlayback, int pmFlags,
        bool nextFrameTeleport, bool thisFrameTeleport,
        bool noPredict, bool synchronousClients,
        int dmflags, int pmoveFixed, int pmoveMsec, int team)
    {
        Hyperspace = false;

        // First frame guarantee
        if (!_validPPS)
        {
            _validPPS = true;
            PredictedPlayerState = *snapPs;
        }

        // Demo playback or spectating (PMF_FOLLOW)
        if (demoPlayback || (pmFlags & PMF_FOLLOW) != 0)
        {
            InterpolatePlayerState(false,
                snapPs, nextSnapPs, hasNextSnap,
                snapServerTime, nextSnapServerTime, cgTime,
                nextFrameTeleport);
            return;
        }

        // Non-predicting
        if (noPredict || synchronousClients)
        {
            InterpolatePlayerState(true,
                snapPs, nextSnapPs, hasNextSnap,
                snapServerTime, nextSnapServerTime, cgTime,
                nextFrameTeleport);
            return;
        }

        // Prepare for pmove
        int tracemask;
        if (PredictedPlayerState.PmType == PmType.PM_DEAD)
            tracemask = MASK_PLAYERSOLID & ~CONTENTS_BODY;
        else
            tracemask = MASK_PLAYERSOLID;

        if (team == Teams.TEAM_SPECTATOR)
            tracemask &= ~CONTENTS_BODY;

        bool noFootsteps = (dmflags & 32) != 0; // DF_NO_FOOTSTEPS

        // Save state before pmove for transition detection
        Q3PlayerState oldPlayerState = PredictedPlayerState;

        int current = Syscalls.GetCurrentCmdNumber();

        // Check we have commands after the snapshot
        int cmdNum = current - CMD_BACKUP + 1;
        Q3UserCmd oldestCmd;
        Syscalls.GetUserCmd(cmdNum, &oldestCmd);
        if (oldestCmd.ServerTime > snapPs->CommandTime && oldestCmd.ServerTime < cgTime)
            return;

        // Get latest command
        Q3UserCmd latestCmd;
        Syscalls.GetUserCmd(current, &latestCmd);

        // Get most recent state
        if (hasNextSnap && !nextFrameTeleport && !thisFrameTeleport)
        {
            PredictedPlayerState = *nextSnapPs;
            _physicsTime = nextSnapServerTime;
        }
        else
        {
            PredictedPlayerState = *snapPs;
            _physicsTime = snapServerTime;
        }

        // Clamp pmove_msec
        if (pmoveMsec < 8) pmoveMsec = 8;
        else if (pmoveMsec > 33) pmoveMsec = 33;

        // Pmove bounds
        fixed (float* mins = _pmoveMins, maxs = _pmoveMaxs)
        {
            // Pre-allocate scratch buffers outside the loop
            float* adjusted = stackalloc float[3];
            float* oldAngles = stackalloc float[3];
            float* newAngles = stackalloc float[3];
            float* ppsOrigin = stackalloc float[3];
            float* oldOriginBuf = stackalloc float[3];
            float* delta = stackalloc float[3];
            float* predictedError = stackalloc float[3];
            int* touchents = stackalloc int[32];

            // Run cmds
            bool moved = false;
            for (cmdNum = current - CMD_BACKUP + 1; cmdNum <= current; cmdNum++)
            {
                Q3UserCmd cmd;
                Syscalls.GetUserCmd(cmdNum, &cmd);

                if (pmoveFixed != 0)
                {
                    fixed (Q3PlayerState* pps = &PredictedPlayerState)
                        PMove.UpdateViewAngles(pps, &cmd);
                }

                // Skip if before snapshot playerstate time
                if (cmd.ServerTime <= PredictedPlayerState.CommandTime)
                    continue;

                // Skip if from previous map_restart
                if (cmd.ServerTime > latestCmd.ServerTime)
                    continue;

                // Check for prediction error from last frame
                if (PredictedPlayerState.CommandTime == oldPlayerState.CommandTime)
                {
                    if (thisFrameTeleport)
                    {
                        _predictedErrorX = 0;
                        _predictedErrorY = 0;
                        _predictedErrorZ = 0;
                        thisFrameTeleport = false;
                    }
                    else
                    {
                        oldAngles[0] = PredictedPlayerState.ViewAnglesX;
                        oldAngles[1] = PredictedPlayerState.ViewAnglesY;
                        oldAngles[2] = PredictedPlayerState.ViewAnglesZ;

                        ppsOrigin[0] = PredictedPlayerState.OriginX;
                        ppsOrigin[1] = PredictedPlayerState.OriginY;
                        ppsOrigin[2] = PredictedPlayerState.OriginZ;

                        AdjustPositionForMover(ppsOrigin,
                            PredictedPlayerState.GroundEntityNum,
                            _physicsTime, cgOldTime, adjusted,
                            oldAngles, newAngles);

                        oldOriginBuf[0] = oldPlayerState.OriginX;
                        oldOriginBuf[1] = oldPlayerState.OriginY;
                        oldOriginBuf[2] = oldPlayerState.OriginZ;

                        VectorSubtract(oldOriginBuf, adjusted, delta);
                        float len = VectorLength(delta);

                        if (len > 0.1f)
                        {
                            // Apply error decay
                            predictedError[0] = _predictedErrorX;
                            predictedError[1] = _predictedErrorY;
                            predictedError[2] = _predictedErrorZ;

                            int t = cgTime - _predictedErrorTime;
                            float f = (200.0f - t) / 200.0f; // cg_errorDecay default ~200
                            if (f < 0) f = 0;
                            VectorScale(predictedError, f, predictedError);
                            VectorAdd(delta, predictedError, predictedError);

                            _predictedErrorX = predictedError[0];
                            _predictedErrorY = predictedError[1];
                            _predictedErrorZ = predictedError[2];
                            _predictedErrorTime = cgOldTime;
                        }
                    }
                }

                // Don't predict gauntlet firing
                // (gauntletHit is always false client-side)

                if (pmoveFixed != 0)
                {
                    cmd.ServerTime = ((cmd.ServerTime + pmoveMsec - 1) / pmoveMsec) * pmoveMsec;
                }

                int numtouch;
                float xyspeed;
                int watertype, waterlevel;
                bool gauntletHit;

                fixed (Q3PlayerState* pps = &PredictedPlayerState)
                {
                    PMove.Execute(pps, &cmd,
                        _traceDelegate, _pointContentsDelegate,
                        tracemask, noFootsteps, pmoveFixed, pmoveMsec,
                        mins, maxs, out numtouch, touchents, out xyspeed,
                        out watertype, out waterlevel, out gauntletHit);
                }

                moved = true;
            }

            if (!moved) return;

            // Adjust for mover movement
            {
                float* origin = stackalloc float[3];
                float* angles = stackalloc float[3];
                float* outOrigin = stackalloc float[3];
                float* outAngles = stackalloc float[3];
                origin[0] = PredictedPlayerState.OriginX;
                origin[1] = PredictedPlayerState.OriginY;
                origin[2] = PredictedPlayerState.OriginZ;
                angles[0] = PredictedPlayerState.ViewAnglesX;
                angles[1] = PredictedPlayerState.ViewAnglesY;
                angles[2] = PredictedPlayerState.ViewAnglesZ;

                AdjustPositionForMover(origin,
                    PredictedPlayerState.GroundEntityNum,
                    _physicsTime, cgTime, outOrigin,
                    angles, outAngles);

                PredictedPlayerState.OriginX = outOrigin[0];
                PredictedPlayerState.OriginY = outOrigin[1];
                PredictedPlayerState.OriginZ = outOrigin[2];
                PredictedPlayerState.ViewAnglesX = outAngles[0];
                PredictedPlayerState.ViewAnglesY = outAngles[1];
                PredictedPlayerState.ViewAnglesZ = outAngles[2];
            }
        }
    }

    /// <summary>
    /// Get the prediction error for view smoothing.
    /// </summary>
    public static void GetPredictionError(out float x, out float y, out float z, int cgTime)
    {
        int t = cgTime - _predictedErrorTime;
        float f = (200.0f - t) / 200.0f;
        if (f <= 0)
        {
            x = y = z = 0;
            return;
        }
        x = _predictedErrorX * f;
        y = _predictedErrorY * f;
        z = _predictedErrorZ * f;
    }

    /// <summary>
    /// Reset prediction state (called on CG_Init).
    /// </summary>
    public static void Reset()
    {
        _validPPS = false;
        _numSolidEntities = 0;
        _numTriggerEntities = 0;
        _predictedErrorX = _predictedErrorY = _predictedErrorZ = 0;
        _predictedErrorTime = 0;
        _physicsTime = 0;
        Hyperspace = false;
        PredictedPlayerState = default;
    }

    // PMF_FOLLOW constant
    private const int PMF_FOLLOW = 4096;
}
