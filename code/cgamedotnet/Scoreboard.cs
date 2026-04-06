namespace CGameDotNet;

/// <summary>
/// Scoreboard display — shows player scores, pings, and times.
/// Equivalent to cg_scoreboard.c from the C cgame.
/// </summary>
public static unsafe class Scoreboard
{
    // ── Score entry ──
    private struct ScoreEntry
    {
        public int Client;
        public int Score;
        public int Ping;
        public int Time;
        public int PowerUps;
        public int Accuracy;
        public int Team;
        public bool Perfect;
    }

    private const int MAX_CLIENTS = 64;
    private static readonly ScoreEntry[] _scores = new ScoreEntry[MAX_CLIENTS];
    private static int _numScores;
    private static int _teamScoreRed;
    private static int _teamScoreBlue;
    private static int _lastScoreTime;

    // Display state
    private static bool _showScores;
    private static int _scoreFadeTime;

    // Layout constants (640x480 virtual)
    private const int SCOREBOARD_X = 0;
    private const int SB_HEADER = 86;
    private const int SB_TOP = 118;
    private const int SB_NORMAL_HEIGHT = 25;
    private const int SB_INTER_HEIGHT = 16;
    private const int BIGCHAR_WIDTH = 16;
    private const int BIGCHAR_HEIGHT = 16;
    private const int FADE_TIME = 200;

    // Column positions
    private const int SB_SCORELINE_X = 112;
    private const int SB_SCORE_X = SB_SCORELINE_X + BIGCHAR_WIDTH;
    private const int SB_PING_X = SB_SCORELINE_X + 7 * BIGCHAR_WIDTH;
    private const int SB_TIME_X = SB_SCORELINE_X + 13 * BIGCHAR_WIDTH;
    private const int SB_NAME_X = SB_SCORELINE_X + 18 * BIGCHAR_WIDTH;

    // Media
    private static int _scoreboardScore;
    private static int _scoreboardPing;
    private static int _scoreboardTime;
    private static int _scoreboardName;

    public static void Init()
    {
        _numScores = 0;
        _showScores = false;
        _scoreboardScore = Syscalls.R_RegisterShaderNoMip("menu/tab/score.tga");
        _scoreboardPing = Syscalls.R_RegisterShaderNoMip("menu/tab/ping.tga");
        _scoreboardTime = Syscalls.R_RegisterShaderNoMip("menu/tab/time.tga");
        _scoreboardName = Syscalls.R_RegisterShaderNoMip("menu/tab/name.tga");
    }

    public static void ScoresDown()
    {
        int time = Syscalls.Milliseconds();
        if (time - _lastScoreTime > 2000)
            Syscalls.SendConsoleCommand("score\n");
        _showScores = true;
    }

    public static void ScoresUp()
    {
        _showScores = false;
        _scoreFadeTime = Syscalls.Milliseconds();
    }

    /// <summary>Parse "scores" server command.</summary>
    public static void ParseScores()
    {
        _numScores = int.TryParse(Syscalls.Argv(1), out int n) ? n : 0;
        if (_numScores > MAX_CLIENTS) _numScores = MAX_CLIENTS;

        _teamScoreRed = int.TryParse(Syscalls.Argv(2), out int rs) ? rs : 0;
        _teamScoreBlue = int.TryParse(Syscalls.Argv(3), out int bs) ? bs : 0;

        for (int i = 0; i < _numScores; i++)
        {
            int off = i * 14 + 4;
            ref var s = ref _scores[i];
            s.Client = ParseArg(off);
            s.Score = ParseArg(off + 1);
            s.Ping = ParseArg(off + 2);
            s.Time = ParseArg(off + 3);
            s.PowerUps = ParseArg(off + 5);
            s.Accuracy = ParseArg(off + 6);
            s.Perfect = ParseArg(off + 12) != 0;
            s.Team = ParseArg(off + 13);
        }

        _lastScoreTime = Syscalls.Milliseconds();
    }

    private static int ParseArg(int idx)
    {
        string val = Syscalls.Argv(idx);
        return int.TryParse(val, out int v) ? v : 0;
    }

    /// <summary>Draw the scoreboard. Returns true if drawn.</summary>
    public static bool Draw(int time, int clientNum, int gametype)
    {
        if (!_showScores && _numScores == 0)
            return false;

        if (!_showScores)
        {
            int elapsed = time - _scoreFadeTime;
            if (elapsed > FADE_TIME) return false;
        }

        // Dim background
        float* color = stackalloc float[4];
        color[0] = 0; color[1] = 0; color[2] = 0; color[3] = 0.6f;
        Syscalls.R_SetColor(color);
        CGame.FillRect(0, 0, 640, 480, color);
        Syscalls.R_SetColor(null);

        // Header row
        int y = SB_HEADER;

        // Team scores for team games
        if (gametype >= 3) // GT_TEAM or GT_CTF
        {
            string teamText;
            if (_teamScoreRed == _teamScoreBlue)
                teamText = $"Teams are tied at {_teamScoreRed}";
            else if (_teamScoreRed > _teamScoreBlue)
                teamText = $"Red leads {_teamScoreRed} to {_teamScoreBlue}";
            else
                teamText = $"Blue leads {_teamScoreBlue} to {_teamScoreRed}";

            CGame.DrawString(320 - teamText.Length * BIGCHAR_WIDTH / 2,
                y - 24, teamText, 1, 1, 1, 1);
        }

        // Column headers using shaders
        if (_scoreboardScore != 0)
            Syscalls.R_DrawStretchPic(SB_SCORE_X, y, 64, 16, 0, 0, 1, 1, _scoreboardScore);
        if (_scoreboardPing != 0)
            Syscalls.R_DrawStretchPic(SB_PING_X, y, 48, 16, 0, 0, 1, 1, _scoreboardPing);
        if (_scoreboardTime != 0)
            Syscalls.R_DrawStretchPic(SB_TIME_X, y, 48, 16, 0, 0, 1, 1, _scoreboardTime);
        if (_scoreboardName != 0)
            Syscalls.R_DrawStretchPic(SB_NAME_X, y, 64, 16, 0, 0, 1, 1, _scoreboardName);

        y = SB_TOP;

        // Determine row height based on player count
        int rowHeight = _numScores > 12 ? SB_INTER_HEIGHT : SB_NORMAL_HEIGHT;
        int maxRows = (420 - y) / rowHeight;

        float* rowColor = stackalloc float[4];

        for (int i = 0; i < _numScores && i < maxRows; i++)
        {
            ref var score = ref _scores[i];
            if (score.Client < 0 || score.Client >= MAX_CLIENTS) continue;

            bool isLocal = score.Client == clientNum;

            // Highlight local player row
            if (isLocal)
            {
                rowColor[0] = 1; rowColor[1] = 1; rowColor[2] = 0; rowColor[3] = 0.15f;
                CGame.FillRect(SB_SCORELINE_X - 4, y, 640 - SB_SCORELINE_X, rowHeight, rowColor);
            }

            // Team-colored background for team games
            if (gametype >= 3)
            {
                if (score.Team == 1) // TEAM_RED
                { rowColor[0] = 1; rowColor[1] = 0; rowColor[2] = 0; rowColor[3] = 0.1f; }
                else if (score.Team == 2) // TEAM_BLUE
                { rowColor[0] = 0; rowColor[1] = 0; rowColor[2] = 1; rowColor[3] = 0.1f; }
                else
                { rowColor[0] = 0.5f; rowColor[1] = 0.5f; rowColor[2] = 0.5f; rowColor[3] = 0.1f; }
                CGame.FillRect(SB_SCORELINE_X - 4, y, 640 - SB_SCORELINE_X, rowHeight, rowColor);
            }

            // Score
            string scoreStr = score.Score.ToString();
            CGame.DrawString(SB_SCORE_X, y, scoreStr, 1, 1, 1, 1);

            // Ping
            string pingStr = score.Ping == -1 ? "BOT" : score.Ping.ToString();
            CGame.DrawString(SB_PING_X, y, pingStr, 1, 1, 1, 1);

            // Time
            CGame.DrawString(SB_TIME_X, y, score.Time.ToString(), 1, 1, 1, 1);

            // Name from config string
            string name = GetPlayerName(score.Client);
            float nr = 1, ng = 1, nb = 1;
            if (isLocal) { nr = 1; ng = 1; nb = 0; } // Yellow for local player
            CGame.DrawString(SB_NAME_X, y, name, nr, ng, nb, 1);

            y += rowHeight;
        }

        return true;
    }

    private static string GetPlayerName(int clientNum)
    {
        const int CS_PLAYERS = 544;
        byte* gs = CGame.GetGameStateRaw();
        if (gs == null) return $"Player {clientNum}";

        string info = Q3GameState.GetConfigString(gs, CS_PLAYERS + clientNum);
        if (string.IsNullOrEmpty(info)) return $"Player {clientNum}";

        // Parse "n" key from info string
        return ParseInfoValue(info, "n");
    }

    private static string ParseInfoValue(string info, string key)
    {
        int idx = 0;
        while (idx < info.Length)
        {
            if (info[idx] == '\\') idx++;

            int keyStart = idx;
            while (idx < info.Length && info[idx] != '\\') idx++;
            string k = info[keyStart..idx];

            if (idx < info.Length && info[idx] == '\\') idx++;

            int valStart = idx;
            while (idx < info.Length && info[idx] != '\\') idx++;
            string v = info[valStart..idx];

            if (k == key) return v;
        }
        return $"Player";
    }

    public static bool IsShowing => _showScores;
}
