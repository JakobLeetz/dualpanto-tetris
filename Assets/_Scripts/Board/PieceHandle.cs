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
/// current piece's cells, in a jump-free order - see GridManager), with a pause at each, so the
/// player can feel the piece's full shape, not just its lowest cell.
/// Earlier versions called SwitchTo again for every cell, which held correctly during the trace
/// but stopped holding firmly once a piece finished tracing (repeated SwitchTo calls apparently
/// leave the device in a different state than a single call + pure position updates) - moving
/// only the transform and never re-calling SwitchTo avoids that entirely.
/// </summary>
public class PieceHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] float handleSpeed = 15f;
    [SerializeField] float cellPauseSeconds = 0.15f;
    // The first waypoint of a new retrace is often a long jump back (e.g. from wherever the
    // previous trace ended, back to the new piece's first cell) - much farther than the small
    // one-cell hops between the rest of the waypoints. The fixed cellPauseSeconds isn't enough
    // time for the handle to physically arrive there, so the trace moves on to the second
    // waypoint while still travelling to the first - it looks like the first cell got skipped.
    [SerializeField] float firstWaypointPauseSeconds = 0.4f;

    int traceVersion;
    bool following;

    void OnEnable()
    {
        gridManager.OnPieceSpawned += HandlePieceMoved;
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnLinesCleared += HandleLinesCleared;
    }

    void OnDisable()
    {
        gridManager.OnPieceSpawned -= HandlePieceMoved;
        gridManager.OnPieceMoved -= HandlePieceMoved;
        gridManager.OnLinesCleared -= HandleLinesCleared;
    }

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        int myVersion = ++traceVersion;
        _ = RetraceShape(gridManager.GetPieceTraceWaypoints(), myVersion);
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
        List<Vector2Int> waypoints = new List<Vector2Int>();
        foreach (int row in rows)
            for (int x = 0; x < gridManager.Width; x++)
                waypoints.Add(new Vector2Int(x, row));
        // Return to the current piece afterwards (empty if the spawn topped out into game over).
        waypoints.AddRange(gridManager.GetPieceTraceWaypoints());

        await RetraceShape(waypoints, myVersion);

        // Only resume if this trace ran to completion and wasn't superseded by a newer one (a newer
        // trace will resume in its own time / isn't a clear trace). GridManager also has a failsafe
        // timeout so it can never stay paused forever.
        if (myVersion == traceVersion) gridManager.ResumeFall();
    }

    // Abandons itself as soon as a newer move/spawn event arrives, so overlapping fall steps
    // don't fight each other over the same target position - accepted speed cap, see plan.
    async Task RetraceShape(List<Vector2Int> waypoints, int version)
    {
        bool isFirst = true;
        foreach (Vector2Int cell in waypoints)
        {
            if (version != traceVersion) return;
            transform.position = gridManager.GridToWorld(cell);

            if (!following)
            {
                following = true;
                // FollowTarget's Task (PantoHandle.SwitchTo) only completes once the firmware
                // confirms the handle has physically arrived here (or a 3s toolkit-internal
                // timeout) - this is the ONE time in the whole session this actually matters,
                // since every later waypoint/piece is just a transform.position write that
                // PantoHandle.FixedUpdate continuously re-sends once following. Awaiting the first
                // arrival lets us do the speed re-assert and rotation hand-off below only once the
                // firmware is genuinely ready (see those comments) instead of mid-transition.
                await PantoSystem.Instance.FollowTarget(isUpper: false, gameObject, handleSpeed);
                if (version != traceVersion) return;

                // Re-assert speed exactly once, right here after the first arrival is confirmed.
                // SwitchTo already sent the speed once at the very start, but that packet can race
                // the firmware's motor-task boot and get dropped, leaving the handle on a slow
                // default all session (~50% of runs, felt as low power / high resistance). By the
                // time this await returns the firmware has definitely processed a full transition,
                // so it's guaranteed ready to accept the speed now. Sent once (not per fall step) -
                // a per-step re-send floods the position stream with SendSpeed packets and drags
                // the handle to the bottom edge (observed on hardware). No-op in debug mode.
                PantoSystem.Instance.SetHandleSpeed(isUpper: false, handleSpeed);

                // Decouple the it-handle's ROTATION from the position-follow. Otherwise the
                // toolkit's per-frame re-send keeps commanding this target's eulerAngles.y as the
                // handle's rotation (userControlledRotation stays false), and the it-handle's
                // unstable rotation motor makes it intermittently spin wildly. We don't use the
                // it-handle's rotation for anything, so free it - position stays held (relies on the
                // toolkit fix that sends null rotation when userControlledRotation is true).
                PantoSystem.Instance.FreeRotation(isUpper: false);
            }

            await Task.Delay(TimeSpan.FromSeconds(isFirst ? firstWaypointPauseSeconds : cellPauseSeconds));
            isFirst = false;
        }
    }
}
