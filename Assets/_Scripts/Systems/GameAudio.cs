using System.Collections.Generic;
using System.Threading.Tasks;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Glue between game events and the audio/speech systems: subscribes to GridManager / GameManager
/// events and plays SFX (via the toolkit's DualPantoToolkit.SoundManager, by entry name) and the
/// occasional spoken announcement (via SpeechSystem). SFX: move (left/right shift), rotate, line
/// clear / tetris, level up, game over, menu-select (start/restart), failed rotate, piece landed.
/// Speech: after every line clear it announces the number of lines and the current score (via
/// GameManager.OnLinesScored, so it reads the already-updated score, order-independent). The
/// game-over/start/restart spoken prompts live in GameManager (which owns game state). Fall/soft-drop
/// deliberately has no sound.
/// All Instance access is null-guarded so a missing SoundManager/SpeechSystem never crashes the game.
/// </summary>
public class GameAudio : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] GameManager gameManager;

    [Header("SoundManager entry names (configure name -> clip on the SoundManager component;")]
    [Header(" leave a name blank to disable that sound)")]
    [SerializeField] string moveSound = "move";       // left/right shift
    [SerializeField] string rotateSound = "rotate";
    [SerializeField] string lineClearSound = "lineClear";
    [SerializeField] string tetrisSound = "tetris";   // 4-line clear
    [SerializeField] string levelUpSound = "levelUp";
    [SerializeField] string gameOverSound = "gameOver";
    [SerializeField] string menuSound = "menu";               // start/restart selected via pedal
    [SerializeField] string failedRotateSound = "failedRotate";
    [SerializeField] string pieceLandedSound = "pieceLanded";

    [Header("Speech")]
    [SerializeField] bool announceLineClear = true;
    [SerializeField] bool announceLevelUp = true;

    // Tracks the in-flight line-clear announcement so the level-up sound/speech (which fires from a
    // separate event, GameManager.OnLevelUp, right after OnLinesScored in the same call) can wait
    // for it instead of racing/overlapping it. See HandleLinesScored / HandleLevelUp.
    Task lineClearAnnouncement = Task.CompletedTask;

    void OnEnable()
    {
        if (gridManager != null)
        {
            gridManager.OnPieceShifted += HandleShifted;
            gridManager.OnPieceRotated += HandleRotated;
            gridManager.OnRotationFailed += HandleRotationFailed;
            gridManager.OnPieceLocked += HandlePieceLocked;
            gridManager.OnGameOver += HandleGameOver;
        }
        if (gameManager != null)
        {
            gameManager.OnLinesScored += HandleLinesScored;
            gameManager.OnLevelUp += HandleLevelUp;
            gameManager.OnGameStarted += HandleGameStarted;
        }
    }

    void OnDisable()
    {
        if (gridManager != null)
        {
            gridManager.OnPieceShifted -= HandleShifted;
            gridManager.OnPieceRotated -= HandleRotated;
            gridManager.OnRotationFailed -= HandleRotationFailed;
            gridManager.OnPieceLocked -= HandlePieceLocked;
            gridManager.OnGameOver -= HandleGameOver;
        }
        if (gameManager != null)
        {
            gameManager.OnLinesScored -= HandleLinesScored;
            gameManager.OnLevelUp -= HandleLevelUp;
            gameManager.OnGameStarted -= HandleGameStarted;
        }
    }

    void HandleShifted(Vector2Int direction) => Sfx(moveSound);

    void HandleRotated(List<Vector2Int> cells) => Sfx(rotateSound);

    void HandleRotationFailed() => Sfx(failedRotateSound);

    void HandlePieceLocked(List<Vector2Int> cells) => Sfx(pieceLandedSound);

    void HandleGameStarted() => Sfx(menuSound);

    // Fired by GameManager after a clear with the up-to-date score.
    void HandleLinesScored(int lines, int score)
    {
        Sfx(lines >= 4 ? tetrisSound : lineClearSound);
        lineClearAnnouncement = announceLineClear
            ? Say($"Cleared {lines} {(lines == 1 ? "line" : "lines")}. Score {score}.", interrupt: true)
            : Task.CompletedTask;
    }

    // Fired right after OnLinesScored (same GameManager call) when the clear pushed the level up.
    // Waits for the cleared-lines/score announcement above to finish first, so the level-up sound
    // and "Reached level X" speech play in sequence after it instead of racing/overlapping it.
    async void HandleLevelUp()
    {
        int level = gameManager != null ? gameManager.Level : 0;
        await lineClearAnnouncement;
        Sfx(levelUpSound);
        if (announceLevelUp) await Say($"Reached level {level}.");
    }

    // SFX only - the spoken "Game over ... push right pedal to restart" is done by GameManager
    // (it owns the state + restart prompt).
    void HandleGameOver() => Sfx(gameOverSound);

    static void Sfx(string soundName)
    {
        if (string.IsNullOrEmpty(soundName)) return;
        if (SoundManager.Instance == null)
        {
            Debug.LogWarning($"[GameAudio] SoundManager.Instance is null - is the toolkit SoundManager component in the scene? (wanted '{soundName}')");
            return;
        }
        SoundManager.Instance.Play(soundName);
    }

    static Task Say(string text, bool interrupt = false)
    {
        if (SpeechSystem.Instance == null)
        {
            Debug.LogWarning($"[GameAudio] SpeechSystem.Instance is null - is a SpeechSystem component in the scene? (wanted to say '{text}')");
            return Task.CompletedTask;
        }
        return SpeechSystem.Instance.Say(text, interrupt);
    }
}
