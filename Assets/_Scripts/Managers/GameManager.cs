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
    [SerializeField] int gridWidth = 10;
    [SerializeField] int gridHeight = 20;
    [SerializeField] bool debugLogging = true;

    public int Score { get; private set; }
    public int LinesCleared { get; private set; }
    public int Level { get; private set; }
    public bool IsGameOver { get; private set; }

    void Start()
    {
        gridManager.Initialize(gridWidth, gridHeight);
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceRotated += HandlePieceMoved;
        gridManager.OnPieceLocked += _ => SpawnRandomPiece();
        gridManager.OnLinesCleared += HandleLinesCleared;
        gridManager.OnGameOver += HandleGameOver;

        // PantoSystem.CreateBoxObstacle defers registration itself if the device/sync isn't
        // ready yet, so no delay needed here (see toolkit README troubleshooting for why that
        // matters - registering too early can silently fail).
        PantoSystem.Instance.CreateBoxObstacle(gridManager.Frame.gameObject, onUpper: true, onLower: false);
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));
        SpawnRandomPiece();
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
        Score += LineClearScore(rows.Count) * (Level + 1);
        LinesCleared += rows.Count;
        Level = LinesCleared / 10;
        gridManager.SetFallFramesPerRow(FramesForLevel(Level));
        Log($"Cleared {rows.Count} line(s) -> Score: {Score}, Lines: {LinesCleared}, Level: {Level}");
    }

    void HandleGameOver()
    {
        IsGameOver = true;
        Log($"Game Over. Score: {Score}, Lines: {LinesCleared}, Level: {Level}");
        _ = SpeechSystem.Instance.Say($"Game Over. Score {Score}.");
    }

    void Log(string message)
    {
        if (debugLogging) Debug.Log($"[GameManager] {message}");
    }

    void Update()
    {
        if (IsGameOver) return;

        // Left/right is driven by two foot pedals, each wired to emit a keycode: U = left, V =
        // right. One move per press (GetKeyDown is edge-triggered), so no rate-limiting needed.
        // Rotation still comes from nudging the it-handle (see PieceNudgeInput).
        if (Input.GetKeyDown(KeyCode.U)) gridManager.TryMove(Vector2Int.left);
        if (Input.GetKeyDown(KeyCode.V)) gridManager.TryMove(Vector2Int.right);

        // Temporary arrow-key fallbacks for testing without the pedals/device.
        if (Input.GetKeyDown(KeyCode.LeftArrow)) gridManager.TryMove(Vector2Int.left);
        if (Input.GetKeyDown(KeyCode.RightArrow)) gridManager.TryMove(Vector2Int.right);
        if (Input.GetKeyDown(KeyCode.UpArrow)) gridManager.TryRotate(1);
        if (Input.GetKey(KeyCode.DownArrow) && gridManager.SoftDrop()) Score += 1;
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
