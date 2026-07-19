using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Persistent target the it-handle continuously follows (toolkit's "SwitchTo a persistent
/// object" pattern - see the toolkit's own SwitchTo example scene). SwitchTo is called exactly
/// once, ever (on the first move/spawn event) - after that, the toolkit's own FixedUpdate
/// continuously re-reads this object's transform.position every frame and drives the handle
/// toward wherever it currently is, with no further SwitchTo calls needed. On every grid
/// move/spawn, walks this object's position through gridManager.GetPieceTraceWaypoints() (the
/// current piece's cells, in a jump-free order - see GridManager), so the player can feel the
/// piece's full shape, not just its lowest cell.
/// Earlier versions called SwitchTo again for every cell, which held correctly during the trace
/// but stopped holding firmly once a piece finished tracing (repeated SwitchTo calls apparently
/// leave the device in a different state than a single call + pure position updates) - moving
/// only the transform and never re-calling SwitchTo avoids that entirely.
/// Movement between waypoints is a smooth glide (see GlideTo), not an instant teleport: the
/// toolkit's continuous follow just re-sends whatever transform.position currently is every
/// frame, so an instant jump fed the firmware a single large step to chase, which is what caused
/// overshoot on close cells and forced handleSpeed to be kept low. Feeding it a steadily advancing
/// position instead lets it track smoothly and tolerates a higher handleSpeed.
/// </summary>
public class PieceHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] float handleSpeed = 15f;

    // Every hop belongs to one of three categories, each with its own glide + pause pair below:
    //  - PIECE:     tracing a piece's actual shape (glideSeconds / cellPauseSeconds).
    //  - LINE CLEAR: the deliberate sweep across a cleared row, meant to be felt
    //                (lineClearGlideSeconds / lineClearPauseSeconds).
    //  - RELOCATE:  fast repositioning that is NOT meant to be felt as a shape - jumping to a
    //               cleared row's start, and returning to the newly-spawned piece afterwards
    //               (relocateGlideSeconds / relocatePauseSeconds).

    // PIECE: how long to smoothly glide from one waypoint to the next during a normal piece
    // retrace (replaces the old instant teleport). A starting guess to tune on hardware - shorter
    // feels snappier but risks reintroducing overshoot; longer is gentler but slower to trace.
    [SerializeField] float glideSeconds = 0.1f;
    // PIECE: how long to hold still at each waypoint AFTER the glide arrives, so the player can
    // feel that cell distinctly before moving on (unrelated to the glide itself).
    [SerializeField] float cellPauseSeconds = 0.15f;

    // LINE CLEAR: glide duration for the sweep itself (start-of-row to end-of-row) - covers a much
    // longer distance in a single glide than a piece hop, so it wants its own, longer/slower value.
    [SerializeField] float lineClearGlideSeconds = 0.5f;
    // LINE CLEAR: hold at the row's start/end after the sweep glide arrives.
    [SerializeField] float lineClearPauseSeconds = 0.15f;

    // RELOCATE: glide duration for hops that are just repositioning, not meant to be felt: jumping
    // to the start of a cleared line before the (slower) line-clear sweep, and returning to the
    // newly spawned piece afterwards. Short/fast by design.
    [SerializeField] float relocateGlideSeconds = 0.05f;
    // RELOCATE: hold after a relocation hop arrives. Independent of lineClearPauseSeconds/
    // cellPauseSeconds - a relocation is neither the felt line-clear sweep nor a piece-shape cell.
    [SerializeField] float relocatePauseSeconds = 0.15f;

    // The first waypoint of a new retrace is often a long jump back (e.g. from wherever the
    // previous trace ended, back to the new piece's first cell) - much farther than the small
    // one-cell hops between the rest of the waypoints. The fixed per-category pause above isn't
    // enough time for the handle to physically arrive there, so the trace moves on to the second
    // waypoint while still travelling to the first - it looks like the first cell got skipped.
    // Applies to the first hop of EVERY retrace, overriding whatever pause its category would
    // otherwise use.
    [SerializeField] float firstWaypointPauseSeconds = 0.4f;
    // Delay after the very first FollowTarget of the session before re-asserting handle speed and
    // handing off rotation (see FinishFirstFollowSetup). Just long enough that a re-sent speed
    // packet doesn't race firmware readiness; the trace itself no longer waits on this.
    [SerializeField] float firstFollowSetupDelaySeconds = 0.3f;
    // On a fresh game (start/restart), how long the handle rests at the first piece's start
    // position before it begins tracing the rest of the shape - gravity is paused for this hold,
    // so the piece doesn't fall during it, giving the player time to locate the handle first.
    // Requested via RequestStartHold() by GameManager on start/restart.
    [SerializeField] float startHoldSeconds = 1f;

    // One step of a retrace: which cell, how long to glide there, whether that glide should take
    // the Manhattan (X-then-Z) route or go straight there directly, and how long to hold once
    // arrived - see the category comment above and callers for which pair of values to pass.
    readonly struct Hop
    {
        public readonly Vector2Int Cell;
        public readonly float Duration;
        public readonly bool Manhattan;
        public readonly float PauseSeconds;

        public Hop(Vector2Int cell, float duration, bool manhattan, float pauseSeconds)
        {
            Cell = cell;
            Duration = duration;
            Manhattan = manhattan;
            PauseSeconds = pauseSeconds;
        }
    }

    int traceVersion;
    bool following;
    // Set by RequestStartHold (GameManager, on game start/restart), consumed by the next retrace's
    // first hop: makes the handle rest at the start position for startHoldSeconds (gravity paused)
    // before tracing the rest of the shape.
    bool startHoldPending;

    // True from the moment a retrace is kicked off until it finishes (or is itself superseded by a
    // newer one, in which case the newer trace keeps it true). GameManager gates left/right pedal
    // input on this - shifting mid-trace would fight the in-progress waypoint stepping.
    public bool IsTracing { get; private set; }

    // Called by GameManager when a fresh game starts (start or restart), before the first piece
    // spawns, so the upcoming first retrace holds at the start position (see startHoldSeconds).
    public void RequestStartHold() => startHoldPending = true;

    void OnEnable()
    {
        gridManager.OnPieceSpawned += HandlePieceMoved;
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceShifted += HandleShifted;
        gridManager.OnPieceRotated += HandleRotated;
        gridManager.OnLinesCleared += HandleLinesCleared;
    }

    void OnDisable()
    {
        gridManager.OnPieceSpawned -= HandlePieceMoved;
        gridManager.OnPieceMoved -= HandlePieceMoved;
        gridManager.OnPieceShifted -= HandleShifted;
        gridManager.OnPieceRotated -= HandleRotated;
        gridManager.OnLinesCleared -= HandleLinesCleared;
    }

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        int myVersion = ++traceVersion;
        List<Hop> hops = new List<Hop>();
        foreach (Vector2Int cell in gridManager.GetPieceTraceWaypoints())
            hops.Add(new Hop(cell, glideSeconds, manhattan: true, pauseSeconds: cellPauseSeconds));
        _ = RetraceShape(hops, myVersion);
    }

    /// <summary>
    /// Traces an arbitrary list of grid cells on demand (awaitable) - used by the tutorial to draw
    /// a pre-placed block / a spawned piece with the lower handle. Supersedes any in-flight trace
    /// (++traceVersion), same as a normal move. Cells are traced in the given order. Pass explicit
    /// glide/pause (>= 0) to trace deliberately slowly so a player can follow; -1 uses the normal
    /// piece speed (glideSeconds / cellPauseSeconds).
    /// </summary>
    public Task TraceShape(List<Vector2Int> cells, float glide = -1f, float pause = -1f)
    {
        float g = glide >= 0f ? glide : glideSeconds;
        float p = pause >= 0f ? pause : cellPauseSeconds;
        int myVersion = ++traceVersion;
        List<Hop> hops = new List<Hop>();
        foreach (Vector2Int cell in cells)
            hops.Add(new Hop(cell, g, manhattan: true, pauseSeconds: p));
        return RetraceShape(hops, myVersion);
    }

    /// <summary>
    /// Sends the handle up to the piece spawn area (top centre) in one fast, direct RELOCATE hop -
    /// no piece is involved, so this is travel, not something meant to be felt. Used at the start of
    /// a tutorial level so the handle doesn't sit parked wherever the PREVIOUS level's piece happened
    /// to land while the new level's intro plays.
    /// </summary>
    public Task MoveToSpawnArea()
    {
        if (gridManager == null || gridManager.CellSize <= 0f) return Task.CompletedTask;
        int myVersion = ++traceVersion;
        Vector2Int cell = new Vector2Int((gridManager.Width - 1) / 2, gridManager.Height - 1);
        List<Hop> hops = new List<Hop> {
            new Hop(cell, relocateGlideSeconds, manhattan: false, pauseSeconds: relocatePauseSeconds) };
        return RetraceShape(hops, myVersion);
    }

    /// <summary>
    /// A rotation deliberately does NOT redraw the shape - the player is told they will feel the new
    /// orientation on the next fall step. But the handle must still END UP ON the rotated piece: it
    /// rests wherever the last trace finished, and that cell is often not part of the new shape at
    /// all (a flat bar rests on its rightmost cell; stood upright, that cell is three columns away).
    /// Leaving it there also breaks the NEXT shift, because HandleShifted slides the current position
    /// by one cell relatively - from a stale spot that walks the handle out of the playing field.
    /// So: one fast RELOCATE hop onto the new shape's last waypoint, which is exactly where a normal
    /// trace would have left it. Travel, not drawing.
    /// </summary>
    void HandleRotated(List<Vector2Int> cells)
    {
        List<Vector2Int> waypoints = gridManager.GetPieceTraceWaypoints();
        if (waypoints.Count == 0) return;
        int myVersion = ++traceVersion;
        List<Hop> hops = new List<Hop> {
            new Hop(waypoints[waypoints.Count - 1], relocateGlideSeconds,
                    manhattan: false, pauseSeconds: relocatePauseSeconds) };
        _ = RetraceShape(hops, myVersion);
    }

    // A pure left/right shift (GameManager blocks this input entirely while IsTracing, so this is a
    // defensive no-op then, not the normal path) - just slide the held target sideways by one cell
    // instead of a full retrace. following is already true by the time a shift can happen (a piece
    // must have spawned/fallen first), so no FollowTarget call is needed - the toolkit keeps
    // re-reading this transform every frame regardless.
    void HandleShifted(Vector2Int direction)
    {
        if (IsTracing) return;
        transform.position += new Vector3(direction.x * gridManager.CellSize, 0f, direction.y * gridManager.CellSize);
    }

    // After a line clear, sweep the it-handle across each cleared row so the player feels which
    // line(s) went away, then return to tracing the (already-spawned) new piece. This fires right
    // after the new piece's spawn retrace has started (see GridManager.LockPiece ordering), so the
    // ++traceVersion cleanly supersedes it - the version guard in RetraceShape abandons the older
    // one. Gravity is paused by GridManager for the duration (set in ClearFullLines) and released
    // here once the trace finishes, so the new piece doesn't fall until the animation is done.
    // (Skyline/stack-outline tracing is a separate future item, not included here.)
    async void HandleLinesCleared(List<int> rows)
    {
        int myVersion = ++traceVersion;
        List<Hop> hops = new List<Hop>();
        foreach (int row in rows)
        {
            // RELOCATE to the start of the line - that approach isn't the part meant to be felt -
            // then LINE CLEAR sweep across to the end more slowly, which is. Just the two
            // endpoints, not every cell - RetraceShape glides smoothly between waypoints (GlideTo)
            // instead of teleporting, so this alone sweeps the whole row in one continuous motion
            // rather than stopping at each cell in between.
            hops.Add(new Hop(new Vector2Int(0, row), relocateGlideSeconds, manhattan: true, pauseSeconds: relocatePauseSeconds));
            if (gridManager.Width > 1)
                hops.Add(new Hop(new Vector2Int(gridManager.Width - 1, row), lineClearGlideSeconds, manhattan: true, pauseSeconds: lineClearPauseSeconds));
        }

        // RELOCATE back to the current (already-spawned) piece afterwards, via the most direct
        // path - relocating to the piece isn't itself part of the cleared-line feedback, so it
        // skips the Manhattan detour. Only the FIRST hop back is a relocation; once there, the
        // rest of the piece's own waypoints (if any) trace normally as PIECE hops (glideSeconds/
        // Manhattan/cellPauseSeconds), same as any other piece retrace.
        List<Vector2Int> pieceWaypoints = gridManager.GetPieceTraceWaypoints();
        for (int i = 0; i < pieceWaypoints.Count; i++)
        {
            bool isReturnHop = i == 0;
            hops.Add(new Hop(
                pieceWaypoints[i],
                isReturnHop ? relocateGlideSeconds : glideSeconds,
                manhattan: !isReturnHop,
                pauseSeconds: isReturnHop ? relocatePauseSeconds : cellPauseSeconds));
        }

        await RetraceShape(hops, myVersion);

        // Only resume if this trace ran to completion and wasn't superseded by a newer one (a newer
        // trace will resume in its own time / isn't a clear trace). GridManager also has a failsafe
        // timeout so it can never stay paused forever.
        if (myVersion == traceVersion) gridManager.ResumeFall();
    }

    // Abandons itself as soon as a newer move/spawn event arrives, so overlapping fall steps
    // don't fight each other over the same target position - accepted speed cap, see plan.
    // Each hop carries its own glide duration/path style - see the Hop struct and callers.
    async Task RetraceShape(List<Hop> hops, int version)
    {
        IsTracing = true;
        bool isFirst = true;
        foreach (Hop hop in hops)
        {
            if (version != traceVersion) return;
            Vector3 target = gridManager.GridToWorld(hop.Cell);

            if (!following)
            {
                // The very first waypoint ever: no previous position to glide from (and the real
                // starting distance is unknown/possibly large), so jump straight there.
                transform.position = target;
                following = true;

                // Start following - but do NOT await SwitchTo's arrival. SwitchTo marks the handle
                // "inTransition" and, on hardware, only clears it on a confirmed arrival report or
                // a ~3s "couldn't be reached" timeout; while inTransition the toolkit's continuous
                // position-follow is SUPPRESSED, so the handle just sits after its one initial
                // command. Arrival is never reported for this first target, so awaiting it stalled
                // the whole trace ~5s every start (handle looked dead). Instead we end the
                // transition ourselves right away (MarkFollowReady) so continuous-follow engages
                // next frame and the handle tracks our glides immediately, and we do the one-time
                // speed re-assert + rotation hand-off shortly after, off the hot path (see
                // FinishFirstFollowSetup).
                _ = PantoSystem.Instance.FollowTarget(isUpper: false, gameObject, handleSpeed);
                PantoSystem.Instance.MarkFollowReady(isUpper: false);
                _ = FinishFirstFollowSetup();
            }
            else
            {
                // Every later waypoint: glide smoothly instead of teleporting (see class doc).
                if (!await GlideTo(target, hop.Duration, hop.Manhattan, version)) return;
            }

            // On a fresh game (RequestStartHold set the flag), once the handle has reached the very
            // first waypoint (the start position), rest there for startHoldSeconds with gravity
            // paused so the piece doesn't fall - lets the player locate the handle before play.
            if (isFirst && startHoldPending)
            {
                startHoldPending = false;
                gridManager.PauseFall();
                await Task.Delay(TimeSpan.FromSeconds(startHoldSeconds));
                if (version != traceVersion) { gridManager.ResumeFall(); return; }
                gridManager.ResumeFall();
            }

            await Task.Delay(TimeSpan.FromSeconds(isFirst ? firstWaypointPauseSeconds : hop.PauseSeconds));
            isFirst = false;
        }

        // Reached the end without being superseded - genuinely done tracing. (The early returns
        // above never reach here, so this is only ever the still-current trace; the version check
        // is defensive/self-documenting rather than load-bearing.)
        if (version == traceVersion) IsTracing = false;
    }

    // One-time setup after the first FollowTarget of the session, run off the trace's hot path so
    // it never stalls tracing. A short delay first so the re-sent speed packet doesn't race
    // firmware readiness.
    async Task FinishFirstFollowSetup()
    {
        await Task.Delay(TimeSpan.FromSeconds(firstFollowSetupDelaySeconds));

        // Re-assert speed once. SwitchTo already sent it at its very start, but that packet can
        // race the firmware's motor-task boot and get dropped, leaving the handle on a slow default
        // all session (felt as low power / high resistance). Re-sending it a moment later corrects
        // that. Sent once, NOT per fall step - a per-step re-send floods the position stream with
        // SendSpeed packets and drags the handle to the bottom edge (observed on hardware). No-op
        // in debug mode.
        PantoSystem.Instance.SetHandleSpeed(isUpper: false, handleSpeed);

        // Decouple the it-handle's ROTATION from the position-follow. Otherwise the toolkit's
        // per-frame re-send keeps commanding this target's eulerAngles.y as the handle's rotation
        // (userControlledRotation stays false), and the it-handle's unstable rotation motor makes
        // it intermittently spin wildly. We don't use the it-handle's rotation for anything, so
        // free it - position stays held (relies on the toolkit fix that sends null rotation when
        // userControlledRotation is true).
        PantoSystem.Instance.FreeRotation(isUpper: false);
    }

    // Smoothly moves transform.position from wherever it currently is to target over `duration`
    // seconds. When manhattan is true, takes a MANHATTAN path (full X move, then full Z move)
    // rather than a diagonal straight line - most hops are already single-axis (within a piece,
    // waypoints are ordered to be grid-adjacent - see GridManager's Shapes comment), so this is a
    // no-op change for them; it matters for the jump between the last waypoint of one retrace and
    // the first of the next (e.g. a plain fall step), which can land in a different column - that
    // jump goes sideways then down instead of cutting diagonally across cells. When manhattan is
    // false, goes straight there directly (both axes at once) - used for hops that are explicitly
    // just relocating, not meant to be felt as a shape (see callers, e.g. returning to the piece
    // after a cleared-line sweep). Duration for the Manhattan case is split between the two legs
    // proportional to each axis's distance, so a single-axis hop still uses the full duration on
    // that one axis (identical to a direct glide there). Checks the trace version every frame, same
    // cancellation contract as the rest of RetraceShape. Always lands exactly on target when it
    // returns true, so callers can rely on transform.position being grid-aligned afterwards (e.g.
    // HandleShifted's sideways slide assumes this).
    async Task<bool> GlideTo(Vector3 target, float duration, bool manhattan, int version)
    {
        Vector3 start = transform.position;
        if (!manhattan) return await GlideAxis(start, target, duration, version);

        float dx = Mathf.Abs(target.x - start.x);
        float dz = Mathf.Abs(target.z - start.z);
        float totalDistance = dx + dz;
        float xDuration = totalDistance > 0f ? duration * (dx / totalDistance) : 0f;
        float zDuration = duration - xDuration;

        Vector3 corner = new Vector3(target.x, start.y, start.z);
        if (!await GlideAxis(start, corner, xDuration, version)) return false;
        if (!await GlideAxis(corner, target, zDuration, version)) return false;
        return true;
    }

    // A plain Lerp from `from` to `to` over `duration` seconds - either one leg of a Manhattan
    // glide, or (if `from`/`to` differ on both axes) a direct diagonal glide (see GlideTo).
    async Task<bool> GlideAxis(Vector3 from, Vector3 to, float duration, int version)
    {
        if (duration <= 0f)
        {
            if (version != traceVersion) return false;
            transform.position = to;
            return true;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (version != traceVersion) return false;
            elapsed += Time.deltaTime;
            transform.position = Vector3.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            await Task.Yield();
        }

        if (version != traceVersion) return false;
        transform.position = to;
        return true;
    }
}
