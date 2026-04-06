using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// Impact marks/decals that render on world surfaces.
/// Equivalent to cg_marks.c from the C cgame.
/// </summary>
public static unsafe class Marks
{
    // ── Mark polygon ──

    private struct MarkPoly
    {
        public bool Active;
        public int Time;
        public int Shader;
        public bool AlphaFade;
        public float R, G, B, A;
        public int NumVerts;
        public fixed float Verts[MAX_VERTS_ON_POLY * 5]; // x,y,z,s,t per vert
    }

    private const int MAX_MARK_POLYS = 256;
    private const int MAX_VERTS_ON_POLY = 10;
    private const int MAX_MARK_FRAGMENTS = 128;
    private const int MAX_MARK_POINTS = 384;
    private const int MARK_TOTAL_TIME = 10000;
    private const int MARK_FADE_TIME = 1000;

    private static readonly MarkPoly[] _marks = new MarkPoly[MAX_MARK_POLYS];

    // ── Shaders ──
    private static int _bulletMarkShader;
    private static int _burnMarkShader;
    private static int _bloodMarkShader;
    private static int _energyMarkShader;

    public static void Init()
    {
        Array.Clear(_marks);
        _bulletMarkShader = Syscalls.R_RegisterShader("gfx/damage/bullet_mrk");
        _burnMarkShader = Syscalls.R_RegisterShader("gfx/damage/burn_med_mrk");
        _bloodMarkShader = Syscalls.R_RegisterShader("bloodMark");
        _energyMarkShader = Syscalls.R_RegisterShader("gfx/damage/plasma_mrk");
        Syscalls.Print("[.NET cgame] Mark system initialized\n");
    }

    /// <summary>
    /// Projects a mark onto world surfaces.
    /// Equivalent to CG_ImpactMark in the original cgame.
    /// </summary>
    public static void ImpactMark(int shader,
        float ox, float oy, float oz,
        float dx, float dy, float dz,
        float radius,
        float r, float g, float b, float a,
        bool alphaFade, bool temporary)
    {
        if (radius <= 0) return;

        // Build projection axes from direction
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 0.001f) return;
        float invLen = 1.0f / len;
        dx *= invLen; dy *= invLen; dz *= invLen;

        // Build perpendicular axes
        float ax1x, ax1y, ax1z;
        if (MathF.Abs(dz) > 0.9f)
        {
            // direction nearly vertical — use X axis for perp
            ax1x = 1; ax1y = 0; ax1z = 0;
        }
        else
        {
            ax1x = 0; ax1y = 0; ax1z = 1;
        }

        // axis[1] = cross(dir, temp) normalized
        float crossX = dy * ax1z - dz * ax1y;
        float crossY = dz * ax1x - dx * ax1z;
        float crossZ = dx * ax1y - dy * ax1x;
        len = MathF.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ);
        if (len < 0.001f) return;
        invLen = 1.0f / len;
        ax1x = crossX * invLen; ax1y = crossY * invLen; ax1z = crossZ * invLen;

        // axis[2] = cross(dir, axis[1])
        float ax2x = dy * ax1z - dz * ax1y;
        float ax2y = dz * ax1x - dx * ax1z;
        float ax2z = dx * ax1y - dy * ax1x;

        // Build 4-vertex quad for projection
        float invRad = 1.0f / radius;
        Span<float> points = stackalloc float[4 * 3]; // 4 vertices, 3 components
        Span<float> texCoords = stackalloc float[4 * 2]; // 4 vertices, 2 UVs

        for (int i = 0; i < 4; i++)
        {
            float su = (i == 0 || i == 3) ? -radius : radius;
            float sv = (i < 2) ? -radius : radius;

            points[i * 3 + 0] = ox + ax1x * su + ax2x * sv;
            points[i * 3 + 1] = oy + ax1y * su + ax2y * sv;
            points[i * 3 + 2] = oz + ax1z * su + ax2z * sv;

            texCoords[i * 2 + 0] = 0.5f + (su * invRad) * 0.5f;
            texCoords[i * 2 + 1] = 0.5f + (sv * invRad) * 0.5f;
        }

        // Call engine CM_MarkFragments to clip against BSP
        Q3MarkFragment* fragments = stackalloc Q3MarkFragment[MAX_MARK_FRAGMENTS];
        float* markPoints = stackalloc float[MAX_MARK_POINTS * 3];

        fixed (float* pPoints = points)
        {
            float* proj = stackalloc float[3];
            proj[0] = dx; proj[1] = dy; proj[2] = dz;

            int numFragments = Syscalls.CM_MarkFragments(4, pPoints, proj,
                MAX_MARK_POINTS, markPoints,
                MAX_MARK_FRAGMENTS, (int*)fragments);

            if (numFragments <= 0) return;

            for (int f = 0; f < numFragments; f++)
            {
                int firstPt = fragments[f].FirstPoint;
                int numPts = fragments[f].NumPoints;
                if (numPts <= 2 || numPts > MAX_VERTS_ON_POLY) continue;

                // Allocate a mark polygon
                int idx = AllocMark();
                ref var mark = ref _marks[idx];
                mark.Active = true;
                mark.Time = Syscalls.Milliseconds();
                mark.Shader = shader;
                mark.AlphaFade = alphaFade;
                mark.R = r; mark.G = g; mark.B = b; mark.A = a;
                mark.NumVerts = numPts;

                for (int v = 0; v < numPts; v++)
                {
                    float vx = markPoints[(firstPt + v) * 3 + 0];
                    float vy = markPoints[(firstPt + v) * 3 + 1];
                    float vz = markPoints[(firstPt + v) * 3 + 2];

                    // Project onto our axes to get tex coords
                    float ddx = vx - ox;
                    float ddy = vy - oy;
                    float ddz = vz - oz;
                    float ts = 0.5f + (ddx * ax1x + ddy * ax1y + ddz * ax1z) * invRad * 0.5f;
                    float tt = 0.5f + (ddx * ax2x + ddy * ax2y + ddz * ax2z) * invRad * 0.5f;

                    mark.Verts[v * 5 + 0] = vx;
                    mark.Verts[v * 5 + 1] = vy;
                    mark.Verts[v * 5 + 2] = vz;
                    mark.Verts[v * 5 + 3] = ts;
                    mark.Verts[v * 5 + 4] = tt;
                }
            }
        }
    }

    /// <summary>Render all active marks each frame.</summary>
    public static void AddToScene(int time)
    {
        Q3PolyVert* verts = stackalloc Q3PolyVert[MAX_VERTS_ON_POLY];

        for (int i = 0; i < MAX_MARK_POLYS; i++)
        {
            ref var mark = ref _marks[i];
            if (!mark.Active) continue;

            int elapsed = time - mark.Time;
            if (elapsed > MARK_TOTAL_TIME)
            {
                mark.Active = false;
                continue;
            }

            // Calculate fade alpha
            float alpha = mark.A;
            if (elapsed > MARK_TOTAL_TIME - MARK_FADE_TIME)
            {
                float fadeT = 1.0f - (float)(elapsed - (MARK_TOTAL_TIME - MARK_FADE_TIME)) / MARK_FADE_TIME;
                if (mark.AlphaFade)
                    alpha *= fadeT;
            }

            // Build polyVerts and submit
            int numVerts = mark.NumVerts;
            byte cr = (byte)(mark.R * 255);
            byte cg = (byte)(mark.G * 255);
            byte cb = (byte)(mark.B * 255);
            byte ca = (byte)(alpha * 255);

            for (int v = 0; v < numVerts; v++)
            {
                verts[v].X = mark.Verts[v * 5 + 0];
                verts[v].Y = mark.Verts[v * 5 + 1];
                verts[v].Z = mark.Verts[v * 5 + 2];
                verts[v].S = mark.Verts[v * 5 + 3];
                verts[v].T = mark.Verts[v * 5 + 4];
                verts[v].R = cr;
                verts[v].G = cg;
                verts[v].B = cb;
                verts[v].A = ca;
            }

            Syscalls.R_AddPolyToScene(mark.Shader, numVerts, verts);
        }
    }

    /// <summary>Quick access to common mark shaders.</summary>
    public static int BulletMarkShader => _bulletMarkShader;
    public static int BurnMarkShader => _burnMarkShader;
    public static int BloodMarkShader => _bloodMarkShader;
    public static int EnergyMarkShader => _energyMarkShader;

    private static int AllocMark()
    {
        // Find free slot or recycle oldest
        int best = -1;
        int oldest = int.MaxValue;
        for (int i = 0; i < MAX_MARK_POLYS; i++)
        {
            if (!_marks[i].Active) return i;
            if (_marks[i].Time < oldest)
            {
                oldest = _marks[i].Time;
                best = i;
            }
        }
        return best >= 0 ? best : 0;
    }
}
