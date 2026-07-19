using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Scripted introduction for blind players. Runs as an async sequence of levels that teach the game
/// bit by bit while feeling like continuous play - levels are NOT announced as such. Drives spoken
/// instructions (SpeechSystem), sound cues, the field size, and which pieces spawn, via hooks on
/// GameManager (PieceSource / AutoSpawn / ScoringEnabled / SpawnPiece) and GridManager (Resize /
/// AddLockedCells) plus the lower handle's on-demand trace (PieceHandle.TraceShape).
///
/// Seven levels: 1 explore the field, 2 place a square, 3 find a block and stack, 4 rotation,
/// 5 a bigger field with new shapes, 6 scoring, 7 full size with all seven shapes. Level 7 already
/// ends on the standard 8x16 grid, so the handoff to free play needs no further resize.
///
/// Levels 3-7 all end in the same shared PracticePhase: pieces keep falling until the player has
/// cleared a target number of lines by themselves. Mistakes there are COACHED, never a failure -
/// only level 2 and level 3's scripted first piece can actually restart their level.
///
/// "Which handle" is spoken, not shaken (no vibration haptic exists / is stable): "upper handle" =
/// the free handle the player moves (stack), "lower handle" = the device-driven piece handle.
/// </summary>
public class TutorialManager : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] PieceHandle pieceHandle;
    [SerializeField] StackHandle stackHandle;

    [Header("Standard (free-play) size the tutorial ends into")]
    [SerializeField] int standardWidth = 8;
    [SerializeField] int standardHeight = 16;

    [Header("Fail-safe timing")]
    [Tooltip("Seconds of no progress before an inactivity reminder is spoken.")]
    [SerializeField] float reminderSeconds = 6f;
    [Tooltip("Max retries for a scripted placement before moving on regardless.")]
    [SerializeField] int maxRetries = 4;

    // NOTE: the tutorial deliberately does NOT override the fall speed - it inherits the game's
    // level-0 speed set in GameManager.Start (FramesForLevel(0)). An earlier serialized
    // `tutorialFallFramesPerRow` override was removed: a serialized field keeps the value stored in
    // the scene, so changing its code default silently did nothing and the pieces kept falling fast.

    [Tooltip("How long the lower handle holds on a shape's first cell before it starts drawing the " +
        "shape - gives the player time to register where the drawing starts.")]
    [SerializeField] float preTraceHoldSeconds = 1.5f;

    [Tooltip("How often a \"press the right pedal to continue\" prompt is repeated while waiting, " +
        "so it can't be missed or forgotten.")]
    [SerializeField] float continuePromptRepeatSeconds = 20f;

    // Lines the player has to clear ON THEIR OWN in each level's practice phase before moving on;
    // lines from that level's scripted pieces do NOT count. Deliberately NOT [SerializeField]s: the
    // scene keeps a serialized value from when the component was added, so changing a code default
    // here would silently do nothing (same trap as the removed tutorialFallFramesPerRow).
    const int level3PracticeLines = 4;
    // Level 4 has no practice phase - it is a fully scripted two-bar lesson that restarts on a miss.
    const int level5PracticeLines = 4;
    const int level6PracticeLines = 4;

    // Round-robin order for the levels that use every shape, so each one is guaranteed to turn up
    // (and get its first-spawn description) rather than being left to chance.
    static readonly PieceType[] AllPieces = {
        PieceType.O, PieceType.I, PieceType.T, PieceType.J,
        PieceType.L, PieceType.S, PieceType.Z };

    // Gravity for level 4's demo drop. Level-0 speed is 300 frames/row (5 s) = ~30 s for the whole
    // fall, too long to watch; but it must stay SLOWER than one full piece trace, or each fall step
    // supersedes the previous one and the handle never finishes drawing the square. With PieceHandle's
    // defaults a square takes (glide 0.1 + firstPause 0.4) + 4 x (0.1 + 0.15) = 1.5 s, so anything
    // under 90 frames/row gets cut off - which is exactly what 60 did. 150 leaves ~1 s of margin
    // (~15 s for the whole demo). Re-check this if PieceHandle's glide/pause values change.
    const int demoDropFallFrames = 150; // 2.5 s per row

    [Header("Demo cue sound entry names (must match the SoundManager entries)")]
    [SerializeField] string stackStepEmptySound = "stackStepEmpty";
    [SerializeField] string stackStepOccupiedSound = "stackStepOccupied";

    [Header("Dev")]
    [Tooltip("Skips to the next level (hardware testing without replaying everything).")]
    [SerializeField] KeyCode skipLevelKey = KeyCode.N;

    bool running;

    // Pedal waiters + inactivity tracking, driven by Update.
    TaskCompletionSource<bool> rightPedalTcs;
    // Completed by the dev skip key; makes every skippable wait in the running level bail out.
    TaskCompletionSource<bool> levelSkip;
    // Set by a level that wants to react to a top-out itself (announce + reset + carry on).
    // Null = the generic quiet board reset below.
    Action topOutHandler;
    float lastProgressTime;
    string reminderText;
    bool reminderActive;

    // First-occurrence explainers (tutorial-wide, spoken once each on top of the fail sound).
    bool explainedEdge, explainedShiftFail, explainedRotateFail, explainedTraceBlock, explainedRotate;

    // Shapes already described on their first spawn. O and I start in here because levels 2 and 4
    // introduce them in their own scripted wording ("a square block", "a long bar lying flat") -
    // describing them again at spawn would queue a second line behind carefully sequenced speech.
    readonly HashSet<PieceType> describedPieces = new HashSet<PieceType> { PieceType.O, PieceType.I };

    // ---- entry point ----

    public void Begin()
    {
        if (running) return;
        if (gridManager == null || pieceHandle == null || stackHandle == null)
        {
            Debug.LogError("[TutorialManager] missing references - falling back to free play.");
            GameManager.Instance.EnterFreePlayFromTutorial();
            return;
        }
        running = true;

        // Tutorial-wide "explain the first time it happens" hooks.
        stackHandle.OnEdgePush += HandleEdgePush;
        gridManager.OnShiftFailed += HandleShiftFail;
        gridManager.OnRotationFailed += HandleRotateFail;
        gridManager.OnPieceRotated += HandleRotated;
        gridManager.OnPieceSpawned += HandlePieceSpawned;
        GameManager.Instance.OnMoveBlockedByTrace += HandleMoveBlockedByTrace;

        _ = Run();
    }

    async Task Run()
    {
        try
        {
            await RunLevel(Level1_ExploreField);
            await RunLevel(Level2_PlaceSquare);
            await RunLevel(Level3_FindAndStack);
            await RunLevel(Level4_Rotate);
            await RunLevel(Level5_BiggerField);
            await RunLevel(Level6_Scoring);

            await HandOffToFreePlay();
        }
        catch (Exception e)
        {
            Debug.LogError($"[TutorialManager] tutorial aborted: {e}");
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Runs one level under a skip signal. The dev skip key completes `levelSkip`, which makes every
    /// skippable wait inside the level throw LevelSkipped - so the level is abandoned wherever it
    /// happens to be, rather than just nudging past one wait (which did nothing whenever the level
    /// was mid-speech, mid-trace or mid-pause, i.e. most of the time).
    /// </summary>
    async Task RunLevel(Func<Task> level)
    {
        levelSkip = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await level();
        }
        catch (LevelSkippedException)
        {
            Debug.Log("[TutorialManager] level skipped (dev key)");
            // StopSpeaking, NOT Stop: Stop() cancels the speech source permanently and TTS would be
            // dead for the rest of the session.
            SpeechSystem.Instance?.StopSpeaking();
            RestoreAfterSkip();
        }
        finally
        {
            levelSkip = null;
        }
    }

    // A skipped level is abandoned mid-step, so shared state it had switched around (input off for a
    // demo drop, a sped-up fall, an active reminder, a top-out handler) has to be put back before the
    // next level starts. Per-level event subscriptions clean themselves up in their own finally
    // blocks; this covers the flags those don't own.
    void RestoreAfterSkip()
    {
        StopReminder();
        topOutHandler = null;
        // Clears any half-fallen piece and un-pauses gravity, so the next level doesn't inherit a
        // piece it never spawned. Levels that resize would wipe it anyway; this covers the ones
        // (like level 6) that deliberately continue on the previous board.
        gridManager.Reset();
        if (GameManager.Instance == null) return;
        GameManager.Instance.InputEnabled = true;
        GameManager.Instance.AutoSpawn = false;
        GameManager.Instance.AnnounceLineClears = true;
        GameManager.Instance.ApplyNormalFallSpeed();
    }

    class LevelSkippedException : Exception { }

    /// <summary>
    /// Awaits `work`, but abandons it if the level gets skipped first. Everything the tutorial waits
    /// on for any length of time (speech, pauses, pedal, piece lock, practice targets) goes through
    /// here, so the skip key lands promptly wherever the level currently is.
    /// </summary>
    async Task Skippable(Task work)
    {
        if (levelSkip == null) { await work; return; }
        if (await Task.WhenAny(work, levelSkip.Task) == levelSkip.Task) throw new LevelSkippedException();
        await work; // completed normally - observe any exception
    }

    void End()
    {
        stackHandle.OnEdgePush -= HandleEdgePush;
        gridManager.OnShiftFailed -= HandleShiftFail;
        gridManager.OnRotationFailed -= HandleRotateFail;
        gridManager.OnPieceRotated -= HandleRotated;
        gridManager.OnPieceSpawned -= HandlePieceSpawned;
        if (GameManager.Instance != null) GameManager.Instance.OnMoveBlockedByTrace -= HandleMoveBlockedByTrace;
        reminderActive = false;
        topOutHandler = null;
        running = false;

        // Safety net: level 4 switches input off for its demo drop. If the tutorial aborts mid-demo
        // the player would otherwise be left with dead pedals forever.
        if (GameManager.Instance != null)
        {
            GameManager.Instance.InputEnabled = true;
            GameManager.Instance.ApplyNormalFallSpeed();
        }
    }

    /// <summary>
    /// Called by GameManager when a piece tops out during the tutorial (instead of the game-over
    /// screen). A level that has installed a topOutHandler deals with it itself (level 3 announces
    /// it and hands the player a fresh board); otherwise the board is just cleared quietly so the
    /// tutorial can't get stuck.
    /// </summary>
    public void HandleTopOut()
    {
        if (topOutHandler != null) { topOutHandler(); return; }
        _ = QuietBoardReset();
    }

    async Task QuietBoardReset()
    {
        await Task.Yield(); // escape the lock/spawn call stack before touching the board
        if (!running) return;
        gridManager.Reset();
        if (GameManager.Instance.AutoSpawn) GameManager.Instance.SpawnNextPiece();
    }

    // ---- levels ----

    async Task Level1_ExploreField()
    {
        await ResizeTo(4, 8);
        await Say("Explore the outline of this 4 by 8 playing field with the upper handle. " +
                  "Move it around to feel the walls.");

        // Inactivity reminder keyed on cell changes; complete on right pedal.
        StartReminder("Move the upper handle around to explore the playing field.");
        stackHandle.OnCellChanged += ReminderProgressCell;
        try
        {
            const string continuePrompt = "Press the right pedal when you are ready to continue.";
            await Say(continuePrompt);
            Task pedal = WaitForRightPedal();
            // Re-offer the prompt periodically until the pedal is actually pressed. The prompt
            // interval is a plain delay, NOT a skippable wait - `pedal` is the skippable one, so a
            // skip lands immediately instead of waiting out the current interval.
            while (true)
            {
                Task timeout = Task.Delay(TimeSpan.FromSeconds(continuePromptRepeatSeconds));
                if (await Task.WhenAny(pedal, timeout) == pedal) break;
                await Say(continuePrompt);
            }
            await pedal; // observe it: throws LevelSkipped if that is why it finished
        }
        finally
        {
            stackHandle.OnCellChanged -= ReminderProgressCell;
            StopReminder();
        }

        await Say("Good.");
    }

    async Task Level2_PlaceSquare()
    {
        // Bottom-right square block, pre-placed, then drawn by the lower handle.
        List<Vector2Int> block = new List<Vector2Int> {
            new Vector2Int(2, 0), new Vector2Int(3, 0), new Vector2Int(3, 1), new Vector2Int(2, 1) };

        // The upper handle plays no role in this level and sitting in the field is just confusing -
        // park it back outside while the intro line plays.
        Task park = stackHandle.MoveOutOfField();

        int attempt = 0;
        while (true)
        {
            gridManager.AddLockedCells(block);
            await Say("There is a square block in the bottom-right corner. The lower handle draws it for you.");
            await park;
            await TraceWithLeadIn(block);
            await WaitSeconds(0.4f);

            // This clear gets its own celebratory line, so mute the generic "Cleared N lines."
            GameManager.Instance.AnnounceLineClears = false;
            int clears = await IntroducePieceThenFall(PieceType.O,
                "Now a square piece is falling, traced by the lower handle. " +
                "The left pedal moves it to the left, the right pedal moves it to the right. " +
                "Move it into the bottom-left corner.",
                progressDirection: Vector2Int.left);
            GameManager.Instance.AnnounceLineClears = true;

            if (clears >= 2)
            {
                await Say("Congratulations, you cleared your first two lines.");
                return;
            }

            attempt++;
            if (attempt >= maxRetries) { await Say("Let's move on."); return; }
            await Say("Not quite. Let's try again.");
            gridManager.Reset();
        }
    }

    /// <summary>
    /// Level 3, in two phases with deliberately different failure handling:
    /// (A) one scripted piece that must be landed next to the pre-placed block - misplacing it means
    ///     the player didn't understand the goal, so the WHOLE LEVEL restarts (block, cues, intro);
    /// (B) free O-play until the player has cleared `level3PracticeLines` lines ON THEIR OWN (the two
    ///     from phase A do not count). Here mistakes are coached, not restarted: a piece dropped in
    ///     the middle is taken back with an explanation, and a top-out just clears the board.
    /// </summary>
    async Task Level3_FindAndStack()
    {
        // Get the lower handle off level 2's drop spot FIRST (it's a quick relocate) - otherwise it
        // sits there motionless through the whole stack-handle move and the intro, which reads as
        // "nothing is happening". Sequential rather than parallel with the move below: driving the
        // upper handle streams positions AND drains the wall-removal queue, and mixing that with the
        // lower handle's position stream is exactly the serial contention that has bitten us before.
        await pieceHandle.MoveToSpawnArea();

        // The upper handle is needed again from here on - bring it back into the field (this also
        // puts the walls back up, since level 2 parked it outside with them down).
        await stackHandle.MoveToStartCorner();

        List<Vector2Int> block = new List<Vector2Int> {
            new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(0, 1) };

        // ---- phase A: land one piece next to the block, restart the level if it's misplaced ----
        int attempt = 0;
        while (true)
        {
            gridManager.Reset();
            gridManager.AddLockedCells(block);

            // Play the two cues instead of describing them.
            await Say("An empty cell sounds like this.");
            PlayCue(stackStepEmptySound);
            await WaitSeconds(0.25f); // just long enough for the cue to land before the next line
            await Say("A filled cell sounds like this.");
            PlayCue(stackStepOccupiedSound);
            await WaitSeconds(0.25f);

            int clears = await IntroducePieceThenFall(PieceType.O,
                "A block is on the floor. Find it with the upper handle, " +
                "then land this piece right next to it to fill the rows.",
                nudge: "Find the block with the upper handle, then move the falling piece next to it.");

            if (clears >= 2) break;

            attempt++;
            if (attempt >= maxRetries) { await Say("Let's move on."); break; }
            await Say("That didn't fill a row. Let's try that again.");
        }

        // ---- phase B: practise until they've cleared enough lines by themselves ----
        await Say("Try clearing a few more lines.");
        await PracticePhase(level3PracticeLines, new[] { PieceType.O },
            "Stack the pieces against the sides to fill and clear a line.",
            coachMiddleDrops: true);
    }

    /// <summary>
    /// Shared free-play phase used by every level from 3 onward: pieces round-robin through `pieces`
    /// until BOTH the player has cleared `targetLines` lines AND every shape in `pieces` has been met
    /// and described. Mistakes are COACHED, never a level failure - topping out just wipes the board
    /// (the cleared count is kept) and play resumes.
    /// </summary>
    /// <param name="coachMiddleDrops">
    /// Only sensible on the narrow 4-wide field (level 3): there a piece touching neither side wall
    /// leaves an unfillable gap on both sides, so it is taken back with an explanation. On 6- or
    /// 8-wide boards a centre placement is perfectly legitimate, so this stays off.
    /// </param>
    async Task PracticePhase(int targetLines, PieceType[] pieces, string reminder,
                             bool coachMiddleDrops = false)
    {
        int next = 0;
        GameManager.Instance.PieceSource = () => pieces[next++ % pieces.Length];
        GameManager.Instance.AutoSpawn = true;

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cleared = 0;
        int clearEvents = 0;

        // The phase ends on TWO conditions, not just the line target: every shape it can spawn must
        // also have been met and described. With a 4-line target and a 7-shape round-robin the target
        // can easily be hit before the last shapes have appeared - one lucky tetris and the player
        // would never meet the S or the Z. `describedPieces` is tutorial-wide, so shapes introduced
        // in an earlier level already count and this costs nothing in the single-shape phases.
        bool AllPiecesMet()
        {
            foreach (PieceType t in pieces)
                if (!describedPieces.Contains(t)) return false;
            return true;
        }
        void TryFinish()
        {
            if (cleared >= targetLines && AllPiecesMet()) doneTcs.TrySetResult(true);
        }

        void OnCleared(List<int> rows)
        {
            clearEvents++;
            cleared += rows.Count;
            TryFinish();
        }
        // Checked on LOCK rather than on spawn: a shape is described when it spawns, so testing at
        // lock time means the player actually gets to place that last new shape instead of it
        // vanishing mid-fall the moment it completes the set.
        void OnLockedCheck(List<Vector2Int> cells) => TryFinish();

        // Snapshot the locked cells and check them a frame later, once ClearFullLines (which runs
        // synchronously right after the lock) has had its say.
        void OnLocked(List<Vector2Int> cells) => _ = CheckMiddleDrop(new List<Vector2Int>(cells), clearEvents);

        async Task CheckMiddleDrop(List<Vector2Int> cells, int clearEventsAtLock)
        {
            await Task.Yield(); // escape the lock/spawn call stack
            if (!running) return;
            if (clearEvents != clearEventsAtLock) return; // it completed a row - nothing to coach

            bool touchesWall = cells.Exists(c => c.x == 0 || c.x == gridManager.Width - 1);
            if (touchesWall) return;

            gridManager.RemoveLockedCells(cells);
            await Say("That piece landed in the middle, leaving a gap on each side. " +
                      "A row like that can never be filled, so I removed it. Keep the pieces against the sides.");
        }

        gridManager.OnLinesCleared += OnCleared;
        gridManager.OnPieceLocked += OnLockedCheck;
        if (coachMiddleDrops) gridManager.OnPieceLocked += OnLocked;
        topOutHandler = () => _ = HandlePracticeTopOut();

        StartReminder(reminder);
        gridManager.OnPieceMoved += ReminderProgressPiece;
        gridManager.OnPieceShifted += ReminderProgressShift;

        GameManager.Instance.SpawnNextPiece();
        try
        {
            await Skippable(doneTcs.Task);

            // The target is reached from inside ClearFullLines, i.e. while the lower handle is still
            // sweeping the cleared rows. Returning here would let the next level's first handle move
            // supersede that sweep (++traceVersion) and cut the animation off half-way, so wait it
            // out first. This also lets PieceHandle's own ResumeFall run instead of being skipped.
            await WaitForTraceIdle();
        }
        finally
        {
            // Also runs when the level is skipped mid-practice - otherwise these handlers would stay
            // attached and keep firing into the next level.
            gridManager.OnPieceMoved -= ReminderProgressPiece;
            gridManager.OnPieceShifted -= ReminderProgressShift;
            StopReminder();
            gridManager.OnLinesCleared -= OnCleared;
            gridManager.OnPieceLocked -= OnLockedCheck;
            if (coachMiddleDrops) gridManager.OnPieceLocked -= OnLocked;
            topOutHandler = null;
            GameManager.Instance.AutoSpawn = false;
        }
    }

    // Topping out during practice is not a level failure - the board is wiped, the player keeps the
    // lines they already cleared, and play continues with a fresh piece.
    async Task HandlePracticeTopOut()
    {
        await Task.Yield(); // escape the lock/spawn call stack before touching the board
        if (!running) return;
        gridManager.Reset();
        await Say("You topped out. Resetting the board. Try clearing more lines.");
        if (!running) return;
        GameManager.Instance.SpawnNextPiece();
    }

    /// <summary>
    /// Level 4 (4x8) - rotation. The lower handle first drops a square into the CENTRE by itself
    /// (input off, so the player watches a mistake being made rather than making it). That leaves a
    /// one-cell gap on each side, which a two-wide square can never fill - so the only way out is to
    /// stand the long bar upright, which is exactly the lesson.
    /// </summary>
    async Task Level4_Rotate()
    {
        gridManager.Reset();
        await Say("Resetting the board. The playing field is empty again.");
        await pieceHandle.MoveToSpawnArea();

        // ---- the "oopsie": a demo drop the player cannot influence ----
        GameManager.Instance.AutoSpawn = false;
        GameManager.Instance.ScoringEnabled = false;
        GameManager.Instance.InputEnabled = false;
        await Say("Watch this one. I'll let it fall by itself.");

        // An O spawns horizontally centred, which on a 4-wide field is dead centre - no positioning
        // needed, the mistake places itself.
        gridManager.SetFallFramesPerRow(demoDropFallFrames);
        GameManager.Instance.SpawnPiece(PieceType.O);
        await WaitForLock();
        await WaitSeconds(0.05f); // let the post-lock bookkeeping run
        GameManager.Instance.ApplyNormalFallSpeed();
        GameManager.Instance.InputEnabled = true;

        await Say("That landed in the middle and left a one-cell gap on each side. " +
                  "A square piece is two cells wide, so it can never fill them.");

        // Where the demo square ended up - re-placed on every retry below to rebuild the setup.
        List<Vector2Int> centreBlock = new List<Vector2Int>(gridManager.LastLockedPieceCells);

        // ---- the lesson: two upright bars, one into each side gap ----
        // Both must land correctly. Misplacing either restarts the level (board + square + both
        // bars), because a half-finished attempt leaves a board the lesson can no longer be taught
        // on. The passive demo drop above is NOT replayed - only the part the player acts on.
        int attempt = 0;
        while (true)
        {
            int clears = 0;
            bool misplaced = false;

            for (int bar = 0; bar < 2 && !misplaced; bar++)
            {
                clears += await IntroducePieceThenFall(PieceType.I,
                    bar == 0
                        ? "The middle pedal turns the falling piece. Every press turns it by a " +
                          "quarter turn, clockwise, and four presses bring it back to where it " +
                          "started. This piece is a long bar lying flat, so one press turns it " +
                          "upright. Turn it, then move it into one of the gaps."
                        : "Here is another bar. Turn it upright as well, and drop it into the gap " +
                          "on the other side.",
                    nudge: "Press the middle pedal to turn the bar upright, then move it to a gap.");

                misplaced = !IsUprightInSideColumn(gridManager.LastLockedPieceCells);
            }

            if (!misplaced && clears >= 2)
            {
                await Say("Well done. Every piece turns the same way, a quarter turn clockwise per " +
                          "press, and turning them is how you make a piece fit where it otherwise " +
                          "would not.");
                return;
            }

            attempt++;
            if (attempt >= maxRetries) { await Say("Let's move on."); return; }
            await Say("That bar didn't end up standing in a gap at the side. " +
                      "Remember, each press of the middle pedal turns it a quarter turn. " +
                      "Let's try again.");
            gridManager.Reset();
            gridManager.AddLockedCells(centreBlock);
        }
    }

    // A bar counts as correctly placed only if it is standing UPRIGHT (one column), in one of the two
    // SIDE columns, and reaching the FLOOR - the single placement that fills the gaps the demo square
    // left. A bar dropped flat also clears a row here (it spans the whole 4-wide field and rests on
    // the square), but it teaches nothing about rotation and leaves the gaps, so it counts as a miss.
    bool IsUprightInSideColumn(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) return false;

        int x = cells[0].x;
        int minY = int.MaxValue;
        foreach (Vector2Int c in cells)
        {
            if (c.x != x) return false; // lying flat, not upright
            minY = Mathf.Min(minY, c.y);
        }
        return (x == 0 || x == gridManager.Width - 1) && minY == 0;
    }

    /// <summary>
    /// Level 5 (6x12) - a bigger field and the first pieces that are neither square nor bar.
    /// </summary>
    async Task Level5_BiggerField()
    {
        await ResizeTo(6, 12);
        await pieceHandle.MoveToSpawnArea();
        await Say("The playing field is bigger now, 6 by 12, and from here on every shape can fall. " +
                  "Each new one is described the first time you meet it, while the lower handle draws " +
                  "it. Most of them are not symmetrical, so turning them with the middle pedal changes " +
                  "them much more than it changed the bar.");

        await PracticePhase(level5PracticeLines, AllPieces,
            "Turn the piece with the middle pedal until it fits, then move it into place.");
    }

    /// <summary>
    /// Level 6 - scoring. Same field and pieces as level 5, so the score is the only new thing.
    /// Turning ScoringEnabled on also makes GameAudio start appending "Score X." to its line-clear
    /// announcements, which is the audible payoff.
    /// </summary>
    async Task Level6_Scoring()
    {
        GameManager.Instance.ResetScore();
        GameManager.Instance.ScoringEnabled = true;
        await Say("From now on, cleared lines score points, and your score is announced with them. " +
                  "Clearing several rows at once is worth much more than clearing them one at a time.");

        await PracticePhase(level6PracticeLines, AllPieces,
            "Keep clearing lines to build up your score.");
    }

    /// <summary>
    /// Closing step, not a level: grow to the standard field one last time and hand over to free
    /// play. There is no practice phase here - by now everything has been taught, and the player
    /// simply keeps playing on the full board.
    /// </summary>
    async Task HandOffToFreePlay()
    {
        await ResizeTo(standardWidth, standardHeight);
        await pieceHandle.MoveToSpawnArea();
        await Say("The playing field has changed size one last time. " +
                  "This is the full field, 8 by 16.");
        await Say("You now know all there is to know. " +
                  "Continue clearing lines and try getting the highest score possible.");
        GameManager.Instance.EnterFreePlayFromTutorial();
    }

    // Introduces a scripted piece with gravity HELD throughout: the lower handle travels up to the
    // new piece, the instruction is spoken, and only THEN is the shape drawn - after which the
    // piece is released to fall. Waits for the lock and returns how many lines it cleared (for
    // placement checks). AutoSpawn stays off so the lock doesn't chain a new piece.
    async Task<int> IntroducePieceThenFall(PieceType type, string announcement,
                                           Vector2Int? progressDirection = null, string nudge = null)
    {
        GameManager.Instance.AutoSpawn = false;
        GameManager.Instance.PieceSource = () => type;
        GameManager.Instance.ScoringEnabled = false;

        int cleared = 0;
        void OnCleared(List<int> rows) => cleared = rows.Count;
        void StopNaggingOnProgress(Vector2Int dir)
        {
            if (progressDirection == null || dir == progressDirection.Value) StopReminder();
        }
        // A rotation counts as progress too - level 4's nudge asks for exactly that, and it would
        // otherwise keep nagging after the player has already done the right thing.
        void StopNaggingOnRotate(List<Vector2Int> cells) => StopReminder();

        gridManager.OnLinesCleared += OnCleared;
        try
        {
            // Gravity stays held through the whole introduction - the piece must not fall while the
            // handle travels, the instruction is spoken, or the shape is drawn. Order (per user):
            // travel up to the piece ONLY -> announcement -> THEN draw the shape -> then let it fall.
            gridManager.PauseFall();
            GameManager.Instance.SpawnPiece(type);

            List<Vector2Int> shape = gridManager.GetPieceTraceWaypoints();
            if (shape.Count > 0)
            {
                // Move to the piece's first cell only. This supersedes the automatic spawn trace, so
                // the shape is NOT drawn yet - just the travel up.
                await pieceHandle.TraceShape(new List<Vector2Int> { shape[0] });
                gridManager.PauseFall(); // re-arm the pause failsafe before the (slow) announcement
                await Say(announcement);
                gridManager.PauseFall(); // re-arm again before drawing
                await pieceHandle.TraceShape(shape); // now draw it, after the announcement
            }
            else
            {
                await Say(announcement);
            }

            gridManager.ResumeFall(); // now it starts falling, at the game's level-0 speed

            // Nudge until the player has moved the piece the RIGHT way - then stop reminding
            // (otherwise it keeps nagging while they simply leave the piece where they want it). A
            // move in the wrong direction does NOT count as progress, so the nudge keeps going.
            StartReminder(nudge ?? "Press the left pedal to move the falling piece.");
            gridManager.OnPieceShifted += StopNaggingOnProgress;
            gridManager.OnPieceRotated += StopNaggingOnRotate;

            await WaitForLock();
            await WaitSeconds(0.05f); // let ClearFullLines (synchronous after the lock) run

            // If that lock cleared rows, the lower handle is now sweeping them. Let it finish before
            // handing control back, or the caller's next handle move supersedes the sweep and cuts
            // the animation short - which is what happens between levels (level 4's last bar into
            // level 5's resize, level 2's clear into level 3's opening move).
            if (cleared > 0) await WaitForTraceIdle();
        }
        finally
        {
            // Also runs when the level is skipped part-way through the introduction, so gravity is
            // never left paused and no handler survives into the next level.
            gridManager.ResumeFall();
            gridManager.OnPieceShifted -= StopNaggingOnProgress;
            gridManager.OnPieceRotated -= StopNaggingOnRotate;
            StopReminder();
            gridManager.OnLinesCleared -= OnCleared;
        }
        return cleared;
    }

    // Moves the lower handle to the shape's FIRST cell, holds there (preTraceHoldSeconds) so the
    // player can register where the drawing starts, and only then draws the shape - at normal
    // trace speed (drawing itself may be quick; the lead-in hold is what makes it followable).
    async Task TraceWithLeadIn(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) return;
        await pieceHandle.TraceShape(new List<Vector2Int> { cells[0] });
        await WaitSeconds(preTraceHoldSeconds);

        // Close the loop: end back on the first cell, so a pre-placed block is drawn the same way a
        // falling one is (GridManager.GetPieceTraceWaypoints does exactly this for the O piece) -
        // once all the way around rather than stopping at the last corner. The duplicate cell is
        // trace-only; the caller's list (which also feeds AddLockedCells) is left untouched.
        List<Vector2Int> loop = new List<Vector2Int>(cells) { cells[0] };
        await pieceHandle.TraceShape(loop);
    }


    // ---- helpers: resize / speech / cues ----

    async Task ResizeTo(int w, int h)
    {
        gridManager.Resize(w, h);
        stackHandle.RebuildGates();
        await stackHandle.MoveToStartCorner();
    }

    Task Say(string text)
    {
        if (SpeechSystem.Instance == null) return Task.CompletedTask;
        return Skippable(SpeechSystem.Instance.Say(text));
    }

    void PlayCue(string soundName)
    {
        if (!string.IsNullOrEmpty(soundName) && SoundManager.Instance != null)
            SoundManager.Instance.Play(soundName);
    }

    Task WaitSeconds(float s) => Skippable(Task.Delay(TimeSpan.FromSeconds(s)));

    // Waits until the lower handle has finished whatever it is drawing (a cleared-line sweep, a piece
    // retrace). Call before anything that would start a new handle move, since that supersedes the
    // running trace and visibly cuts it short. Capped so a stuck trace can never hang the tutorial.
    async Task WaitForTraceIdle(float timeoutSeconds = 8f)
    {
        await WaitSeconds(0.1f); // let a trace that is only just starting raise IsTracing first
        float deadline = Time.time + timeoutSeconds;
        while (pieceHandle.IsTracing && Time.time < deadline) await WaitSeconds(0.05f);
    }

    // ---- helpers: waiters (fulfilled from Update / grid events) ----

    Task WaitForRightPedal()
    {
        rightPedalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Skippable(rightPedalTcs.Task);
    }

    async Task WaitForLock()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(List<Vector2Int> cells) => tcs.TrySetResult(true);
        gridManager.OnPieceLocked += Handler;
        try { await Skippable(tcs.Task); }
        finally { gridManager.OnPieceLocked -= Handler; } // also unsubscribes when the level is skipped
    }

    // ---- inactivity reminder ----

    void StartReminder(string text)
    {
        reminderText = text;
        reminderActive = true;
        lastProgressTime = Time.time;
    }

    void StopReminder() => reminderActive = false;

    void ReminderProgressCell(Vector2Int cell, bool occupied) => lastProgressTime = Time.time;
    void ReminderProgressPiece(List<Vector2Int> cells) => lastProgressTime = Time.time;
    void ReminderProgressShift(Vector2Int dir) => lastProgressTime = Time.time;

    // ---- first-occurrence explainers ----

    void HandleEdgePush()
    {
        if (explainedEdge) return;
        explainedEdge = true;
        _ = Say("That sound means you reached the edge of the playing field.");
    }

    void HandleShiftFail()
    {
        if (explainedShiftFail) return;
        explainedShiftFail = true;
        _ = Say("That piece can't move further in that direction.");
    }

    void HandleRotateFail()
    {
        if (explainedRotateFail) return;
        explainedRotateFail = true;
        _ = Say("That piece can't rotate here.");
    }

    // A SUCCESSFUL rotation. The lower handle deliberately does not redraw the piece on rotate (it
    // only retraces on spawn/fall/shift), so the player hears the rotate sound but feels nothing
    // change until the next fall step. Said once, so the delay reads as expected rather than broken.
    void HandleRotated(List<Vector2Int> cells)
    {
        if (explainedRotate) return;
        explainedRotate = true;
        _ = Say("The piece turned a quarter turn clockwise. The lower handle moves onto its new " +
                "shape, and draws the whole shape again as soon as the piece falls one row.");
    }

    // The first time each shape falls, say what it looks like - the lower handle draws it at the same
    // moment, so the words and the felt outline arrive together.
    void HandlePieceSpawned(List<Vector2Int> cells)
    {
        PieceType type = gridManager.CurrentPieceType;
        if (!describedPieces.Add(type)) return;
        _ = Say(PieceDescription(type));
    }

    // Descriptions of each shape AS IT SPAWNS (rotation state 0), read off the actual shape tables:
    //   I ####      O ##   T .#.   S .##   Z ##.   J #..   L ..#
    //                 ##     ###     ##.     .##     ###     ###
    static string PieceDescription(PieceType type) => type switch
    {
        PieceType.I => "This is the bar: four cells in a straight line.",
        PieceType.O => "This is the square: two cells by two cells.",
        PieceType.T => "A new shape. This is the T: three cells in a row, with one more on top of the middle.",
        PieceType.J => "A new shape. This is the J: three cells in a row, with one more on top of the left end.",
        PieceType.L => "A new shape. This is the L: three cells in a row, with one more on top of the right end.",
        PieceType.S => "A new shape. This is the S: two cells side by side, with two more on top, shifted one step to the right.",
        PieceType.Z => "A new shape. This is the Z: two cells side by side, with two more below, shifted one step to the right.",
        _ => string.Empty,
    };

    // Unlike the three above there is no fail sound behind this one - the press is simply swallowed,
    // so without this the player just gets silence and assumes the pedal is broken.
    void HandleMoveBlockedByTrace()
    {
        if (explainedTraceBlock) return;
        explainedTraceBlock = true;
        _ = Say("You can't move or turn a piece while the lower handle is drawing it. " +
                "Wait until it has finished, then press the pedal.");
    }

    // ---- Update: pedal polling, reminder ticking, dev skip ----

    void Update()
    {
        if (!running) return;

        if (rightPedalTcs != null && (Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.RightArrow)))
        {
            var tcs = rightPedalTcs;
            rightPedalTcs = null;
            tcs.TrySetResult(true);
        }

        // Dev skip: abandon the whole current level, wherever it is.
        if (Input.GetKeyDown(skipLevelKey)) levelSkip?.TrySetResult(true);

        if (reminderActive && Time.time - lastProgressTime > reminderSeconds)
        {
            lastProgressTime = Time.time; // don't repeat every frame
            _ = Say(reminderText);
        }
    }
}
