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
    [SerializeField] int gridWidth = 10;
    [SerializeField] int gridHeight = 20;
    [Tooltip("Seconds to wait after load before the game can be started, so the device/handles are " +
        "fully functional first (they take ~10s; starting earlier makes the first piece fall during " +
        "loading). Starting is gated on this AND a right-pedal press.")]
    [SerializeField] float startupDelaySeconds = 10f;
    [Tooltip("Seconds after game over before the restart pedal is accepted, so a still-in-progress " +
        "move press doesn't instantly restart (with GetKeyDown you then have to deliberately press " +
        "again). Set higher to make it wait roughly until the game-over announcement is done.")]
    [SerializeField] float restartInputDelaySeconds = 1.5f;
    [SerializeField] bool debugLogging = true;

    enum GameState { WaitingToStart, Playing, GameOver }
    GameState state = GameState.WaitingToStart;
    float readyTime;
    float restartReadyTime;
    bool startPromptGiven;

    public int Score { get; private set; }
    public int LinesCleared { get; private set; }
    public int Level { get; private set; }
    public bool IsGameOver { get; private set; }
    public GridManager Grid => gridManager;

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

    void Start()
    {
        gridManager.Initialize(gridWidth, gridHeight);
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceRotated += HandlePieceMoved;
        gridManager.OnPieceLocked += HandlePieceLocked;
        gridManager.OnLinesCleared += HandleLinesCleared;
        gridManager.OnGameOver += HandleGameOver;

        // PantoSystem.CreateBoxObstacle defers registration itself if the device/sync isn't
        // ready yet, so no delay needed here (see toolkit README troubleshooting for why that
        // matters - registering too early can silently fail).
        PantoSystem.Instance.CreateBoxObstacle(gridManager.Frame.gameObject, onUpper: true, onLower: false);
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));

        // Do NOT spawn yet - wait for the startup delay (device ready) + a right-pedal press. Until
        // then there's no piece, so nothing falls. See Update / StartGame.
        readyTime = Time.time + startupDelaySeconds;
        state = GameState.WaitingToStart;
    }

    // The lock that ends a piece spawns the next one; the spawn that can't fit fires OnGameOver.
    // Guarded on Playing so a stray lock while not playing can't spawn.
    void HandlePieceLocked(List<Vector2Int> cells)
    {
        if (state == GameState.Playing) SpawnRandomPiece();
    }

    void SpawnRandomPiece()
    {
        PieceType type = (PieceType)Random.Range(0, 7);
        gridManager.SpawnPiece(type);
        Log($"Spawned {type}");
    }

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        Log($"Piece moved: {string.Join(", ", cells)}");
    }

    void HandleLinesCleared(List<int> rows)
    {
        int previousLevel = Level;
        Score += LineClearScore(rows.Count) * (Level + 1);
        LinesCleared += rows.Count;
        Level = LinesCleared / 10;
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));
        Log($"Cleared {rows.Count} line(s) -> Score: {Score}, Lines: {LinesCleared}, Level: {Level}");
        OnLinesScored?.Invoke(rows.Count, Score);
        if (Level > previousLevel) OnLevelUp?.Invoke();
    }

    void HandleGameOver()
    {
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
                        Say("Push right pedal to start game.");
                        startPromptGiven = true;
                    }
                    if (StartOrRestartPressed()) StartGame();
                }
                break;

            case GameState.Playing:
                HandleGameplayInput();
                break;

            case GameState.GameOver:
                if (Time.time >= restartReadyTime && StartOrRestartPressed()) RestartGame();
                break;
        }
    }

    // Right pedal (V). RightArrow kept as a keyboard fallback for testing without pedals.
    static bool StartOrRestartPressed() =>
        Input.GetKeyDown(KeyCode.V) || Input.GetKeyDown(KeyCode.RightArrow);

    void HandleGameplayInput()
    {
        // Left/right is driven by two foot pedals, each wired to emit a keycode: U = left, V =
        // right (edge-triggered, one move per press). Rotation comes from the keyboard up-arrow for
        // now (handle-turn rotation is parked). Arrow keys are temporary test fallbacks.
        if (Input.GetKeyDown(KeyCode.U)) gridManager.TryMove(Vector2Int.left);
        if (Input.GetKeyDown(KeyCode.V)) gridManager.TryMove(Vector2Int.right);
        if (Input.GetKeyDown(KeyCode.LeftArrow)) gridManager.TryMove(Vector2Int.left);
        if (Input.GetKeyDown(KeyCode.RightArrow)) gridManager.TryMove(Vector2Int.right);
        if (Input.GetKeyDown(KeyCode.UpArrow)) gridManager.TryRotate(1);
        if (Input.GetKey(KeyCode.DownArrow) && gridManager.SoftDrop()) Score += 1;
    }

    void StartGame()
    {
        state = GameState.Playing;
        OnGameStarted?.Invoke();
        if (stackHandle != null) _ = stackHandle.MoveToStartCorner();
        SpawnRandomPiece();
    }

    void RestartGame()
    {
        gridManager.Reset();
        Score = 0;
        LinesCleared = 0;
        Level = 0;
        IsGameOver = false;
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));
        state = GameState.Playing;
        OnGameStarted?.Invoke();
        if (stackHandle != null) _ = stackHandle.MoveToStartCorner();
        SpawnRandomPiece();
    }

    void Say(string text)
    {
        if (SpeechSystem.Instance != null) _ = SpeechSystem.Instance.Say(text, interrupt: true);
    }

    // Classic NES scoring: base points per simultaneous line count, multiplied by (level + 1).
    static int LineClearScore(int lineCount) => lineCount switch
    {
        1 => 40,
        2 => 100,
        3 => 300,
        4 => 1200,
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
