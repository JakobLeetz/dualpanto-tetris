using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Continuous Tetris game loop: spawns random pieces, tracks score/level, drives fall speed.
/// Temporary keyboard controls stand in for pedal/Panto movement input until that's wired up.
/// The field boundary is a hand-placed GameObject with a Panto Box Collider, registered as an
/// obstacle here once at Start - deliberately not via the toolkit's Obstacle Manager, since that
/// scans the whole scene for any PantoCollider and would also pick up (and permanently freeze in
/// place) the falling piece's visual-only blocks. Locked-block obstacles and piece-handle
/// feedback live in Board/LockedBlocksView and Board/PieceHandle.
/// </summary>
public class GameManager : Singleton<GameManager>
{
    [SerializeField] GridManager gridManager;
    [SerializeField] StackHandle stackHandle;
    [SerializeField] PieceHandle pieceHandle;
    [SerializeField] TutorialManager tutorialManager;
    [SerializeField] int gridWidth = 8;
    [SerializeField] int gridHeight = 16;
    [Tooltip("Seconds to wait after load before the game can be started, so the device/handles are " +
        "fully functional first (they take ~10s; starting earlier makes the first piece fall during " +
        "loading). Starting is gated on this AND a right-pedal press.")]
    [SerializeField] float startupDelaySeconds = 10f;
    [Tooltip("Seconds after game over before the restart pedal is accepted, so a still-in-progress " +
        "move press doesn't instantly restart (with GetKeyDown you then have to deliberately press " +
        "again). Set higher to make it wait roughly until the game-over announcement is done.")]
    [SerializeField] float restartInputDelaySeconds = 1.5f;
    [SerializeField] bool debugLogging = true;

    enum GameState { WaitingToStart, Playing, GameOver, Tutorial }
    GameState state = GameState.WaitingToStart;
    float readyTime;
    float restartReadyTime;
    bool startPromptGiven;

    public int Score { get; private set; }
    public int LinesCleared { get; private set; }
    public int Level { get; private set; }
    public bool IsGameOver { get; private set; }
    public GridManager Grid => gridManager;

    // Tutorial hooks (also usable by free-play; defaults preserve normal behaviour):
    // Provides the next piece type - null = uniform random over all 7 (free-play). The tutorial
    // sets this to restrict/script which pieces spawn.
    public System.Func<PieceType> PieceSource;
    // When false, locking a piece does NOT auto-spawn the next one - the tutorial spawns manually
    // during scripted steps.
    public bool AutoSpawn = true;
    // When false, line clears don't award score / advance level - the tutorial enables scoring only
    // from its scoring level onward. GameAudio also omits the score from its spoken line-clear
    // announcement while this is false.
    public bool ScoringEnabled = true;
    // When false, GameAudio plays the line-clear SFX but does NOT announce it - lets the tutorial
    // replace a scripted clear's announcement with its own line (e.g. the first two lines).
    public bool AnnounceLineClears = true;
    // When false, gameplay input is ignored entirely - for tutorial steps that DEMONSTRATE a drop
    // the player must not be able to influence.
    public bool InputEnabled = true;

    // Fires when clearing lines pushes the level up (GridManager is level-agnostic, so this lives
    // here). GameAudio listens for a level-up jingle.
    public event System.Action OnLevelUp;

    // Fires after a line clear once score/lines are updated: (linesClearedThisTime, totalScore).
    // GameAudio uses it to announce the clear with the CURRENT score (order-independent, unlike
    // reading Score from the raw GridManager.OnLinesCleared event).
    public event System.Action<int, int> OnLinesScored;

    // Fires when the right pedal is used to select "start" (from WaitingToStart) or "restart"
    // (from GameOver). GameAudio plays a menu-select sound off this.
    public event System.Action OnGameStarted;

    // Fires when a left/right press was swallowed because the it-handle is mid-trace (see
    // HandleGameplayInput). The tutorial explains that rule the first time it happens.
    public event System.Action OnMoveBlockedByTrace;

    void Start()
    {
        gridManager.Initialize(gridWidth, gridHeight);
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceRotated += HandlePieceMoved;
        gridManager.OnPieceLocked += HandlePieceLocked;
        gridManager.OnLinesCleared += HandleLinesCleared;
        gridManager.OnGameOver += HandleGameOver;

        // NOTE: the field frame is an upper-handle-only wall and is registered by StackHandle, not
        // here - it must stay DOWN until the stack handle has driven to its start position (it
        // starts outside the field, so with the frame up it can never get in). See
        // StackHandle.BuildGates / TearDownGates.
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));

        // Do NOT spawn yet - wait for the startup delay (device ready) + a right-pedal press. Until
        // then there's no piece, so nothing falls. See Update / StartGame.
        readyTime = Time.time + startupDelaySeconds;
        state = GameState.WaitingToStart;
    }

    // The lock that ends a piece spawns the next one; the spawn that can't fit fires OnGameOver.
    // Guarded on Playing/Tutorial + AutoSpawn so a stray lock (or a scripted tutorial step that
    // spawns manually) doesn't double-spawn.
    void HandlePieceLocked(List<Vector2Int> cells)
    {
        if (AutoSpawn && (state == GameState.Playing || state == GameState.Tutorial)) SpawnNextPiece();
    }

    // Spawns the next piece using PieceSource (tutorial-controlled) or uniform random (free-play).
    public bool SpawnNextPiece()
    {
        PieceType type = PieceSource != null ? PieceSource() : (PieceType)Random.Range(0, 7);
        bool ok = gridManager.SpawnPiece(type);
        Log($"Spawned {type}");
        return ok;
    }

    // Spawns a specific piece type immediately (tutorial scripted steps).
    public bool SpawnPiece(PieceType type) => gridManager.SpawnPiece(type);

    /// <summary>
    /// Zeroes score and line count. Used by the tutorial when it introduces scoring, so "cleared
    /// lines score points" genuinely starts from zero rather than from whatever the practice levels
    /// happened to accumulate.
    /// </summary>
    public void ResetScore()
    {
        Score = 0;
        LinesCleared = 0;
    }

    /// <summary>
    /// Restores the normal fall speed for the current level. The tutorial speeds gravity up for its
    /// demo drop and calls this to put it back, instead of duplicating the level-0 constant.
    /// </summary>
    public void ApplyNormalFallSpeed() => gridManager.SetFallFramesPerRow(FramesForLevel(Level));

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        Log($"Piece moved: {string.Join(", ", cells)}");
    }

    void HandleLinesCleared(List<int> rows)
    {
        // Scoring/level progression is gated so the tutorial can introduce it only from its
        // scoring level onward; the line-clear SFX/announcement still fire either way via OnLinesScored.
        if (ScoringEnabled)
        {
            Score += LineClearScore(rows.Count) * (Level + 1);
            LinesCleared += rows.Count;
            Log($"Cleared {rows.Count} line(s) -> Score: {Score}, Lines: {LinesCleared}");

            // Level progression and its fall-speed increase are DISABLED for now (user request):
            // free play stays at level 0 speed, so Level never leaves 0 and OnLevelUp never fires
            // (GameAudio's level-up sound/"Reached level X" stay silent). To restore, put back:
            //   int previousLevel = Level;
            //   Level = LinesCleared / 10;
            //   gridManager.SetFallFramesPerRow(FramesForLevel(Level));
            //   if (Level > previousLevel) OnLevelUp?.Invoke();
        }
        else
        {
            Log($"Cleared {rows.Count} line(s) (scoring disabled)");
        }
        OnLinesScored?.Invoke(rows.Count, Score);
    }

    void HandleGameOver()
    {
        // During the tutorial a top-out must not trigger the real game-over screen - the tutorial
        // quietly clears the board and continues.
        if (state == GameState.Tutorial)
        {
            if (tutorialManager != null) tutorialManager.HandleTopOut();
            return;
        }

        IsGameOver = true;
        state = GameState.GameOver;
        restartReadyTime = Time.time + restartInputDelaySeconds;
        Log($"Game Over. Score: {Score}, Lines: {LinesCleared}, Level: {Level}");
        Say($"Game over. Final score {Score}. Total lines {LinesCleared}. Push right pedal to restart.");
    }

    void Log(string message)
    {
        if (debugLogging) Debug.Log($"[GameManager] {message}");
    }

    void Update()
    {
        switch (state)
        {
            case GameState.WaitingToStart:
                if (Time.time >= readyTime)
                {
                    if (!startPromptGiven)
                    {
                        Say("Welcome to Pantris. Press the left pedal for the introduction, or the right pedal for free play.");
                        startPromptGiven = true;
                    }
                    if (LeftPedalPressed()) StartTutorial();
                    else if (StartOrRestartPressed()) StartGame();
                }
                break;

            case GameState.Playing:
                HandleGameplayInput();
                break;

            case GameState.Tutorial:
                HandleGameplayInput(); // the tutorial controls spawning/scoring; input stays live
                break;

            case GameState.GameOver:
                if (Time.time >= restartReadyTime && StartOrRestartPressed()) RestartGame();
                break;
        }
    }

    // Right pedal (C). RightArrow kept as a keyboard fallback for testing without pedals.
    static bool StartOrRestartPressed() =>
        Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.RightArrow);

    // Left pedal (A). LeftArrow kept as a keyboard fallback.
    static bool LeftPedalPressed() =>
        Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow);

    void HandleGameplayInput()
    {
        // The tutorial switches this off while it demonstrates a drop the player is NOT meant to
        // influence (see TutorialManager's level 4 "oopsie").
        if (!InputEnabled) return;

        // Left/right/rotation are driven by three foot pedals, each wired to emit a keycode:
        // A = left, C = right, B = rotate a quarter turn clockwise (edge-triggered, one action per
        // press). Arrow keys are temporary test fallbacks.
        // ALL THREE are blocked while the it-handle is mid-retrace (e.g. tracing a fall step or the
        // cleared-line sweep): acting then would fight the in-progress waypoint stepping, and for a
        // rotation it would also move the handle onto a shape it is still in the middle of drawing.
        bool left = Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow);
        bool right = Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.RightArrow);
        bool rotate = Input.GetKeyDown(KeyCode.B) || Input.GetKeyDown(KeyCode.UpArrow);
        if (pieceHandle != null && pieceHandle.IsTracing)
        {
            // The press is swallowed - announce it so it isn't just silence (the tutorial explains
            // the rule the first time this happens).
            if (left || right || rotate) OnMoveBlockedByTrace?.Invoke();
        }
        else
        {
            if (left) gridManager.TryMove(Vector2Int.left);
            if (right) gridManager.TryMove(Vector2Int.right);
            if (rotate) gridManager.TryRotate(1);
        }
        // Gated like the line-clear score, so the tutorial can't quietly accumulate points before it
        // has introduced scoring. (No pedal is bound to soft drop today, so this only bites once one
        // is - which is exactly when it would be hard to track down.)
        if (Input.GetKey(KeyCode.DownArrow) && gridManager.SoftDrop() && ScoringEnabled) Score += 1;
    }

    void StartGame()
    {
        state = GameState.Playing;
        OnGameStarted?.Invoke();
        if (stackHandle != null) _ = stackHandle.MoveToStartCorner();
        if (pieceHandle != null) pieceHandle.RequestStartHold();
        SpawnNextPiece();
    }

    void RestartGame()
    {
        gridManager.Reset();
        Score = 0;
        LinesCleared = 0;
        Level = 0;
        IsGameOver = false;
        PieceSource = null;
        AutoSpawn = true;
        ScoringEnabled = true;
        AnnounceLineClears = true;
        InputEnabled = true;
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));
        state = GameState.Playing;
        OnGameStarted?.Invoke();
        if (stackHandle != null) _ = stackHandle.MoveToStartCorner();
        if (pieceHandle != null) pieceHandle.RequestStartHold();
        SpawnNextPiece();
    }

    // Left pedal from the start menu: hand off to the tutorial (falls back to free play if no
    // TutorialManager is wired). The tutorial owns its own resize/positioning/spawn flow.
    void StartTutorial()
    {
        if (tutorialManager == null) { StartGame(); return; }
        state = GameState.Tutorial;
        OnGameStarted?.Invoke(); // menu-select sound
        tutorialManager.Begin();
    }

    /// <summary>
    /// Called by the TutorialManager when the introduction finishes: switches to normal free play on
    /// the CURRENT grid (the tutorial has already resized+positioned it). Resets the tutorial hooks.
    /// </summary>
    public void EnterFreePlayFromTutorial()
    {
        PieceSource = null;
        AutoSpawn = true;
        ScoringEnabled = true;
        AnnounceLineClears = true;
        InputEnabled = true;
        gridManager.SetFallFramesPerRow(FramesForLevel(Level)); // level 0 speed (Level never advances)
        state = GameState.Playing;
        if (pieceHandle != null) pieceHandle.RequestStartHold();
        SpawnNextPiece();
    }

    void Say(string text)
    {
        if (SpeechSystem.Instance != null) _ = SpeechSystem.Instance.Say(text, interrupt: true);
    }

    // Modern-guideline scoring, multiplied by (level + 1). Chosen over the classic NES values
    // (40/100/300/1200) for two reasons specific to this game: the score is SPOKEN, so round hundreds
    // read far better than sums like 1340; and NES's 30x tetris bonus assumes a 10-wide field, while
    // clearing four rows here only takes 32 cells, so 8x rewards it without making every other way of
    // playing pointless.
    static int LineClearScore(int lineCount) => lineCount switch
    {
        1 => 100,
        2 => 300,
        3 => 500,
        4 => 800,
        _ => 0,
    };

    // Classic NES frames-per-row speed curve (60 fps).
    static int FramesForLevel(int level) => level switch
    {
        0 => 300,
        1 => 43,
        2 => 38,
        3 => 33,
        4 => 28,
        5 => 23,
        6 => 18,
        7 => 13,
        8 => 8,
        9 => 6,
        >= 10 and <= 12 => 5,
        >= 13 and <= 15 => 4,
        >= 16 and <= 18 => 3,
        >= 19 and <= 28 => 2,
        _ => 1,
    };
}
