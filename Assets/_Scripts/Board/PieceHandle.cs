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

    // True once the current retrace has finished and the handle is just resting at the last
    // waypoint - false while a trace is actively moving the target around. PieceNudgeInput only
    // reads real-vs-commanded handle displacement as a left/right input while this is true, since
    // during an active trace the handle normally lags behind its (constantly moving) target for
    // reasons that have nothing to do with the player pushing it.
    public bool IsSettled { get; private set; }

    // Since the handle no longer snaps/retraces on a shift or rotation (see HandlePieceShifted/
    // HandlePieceRotated below), a sustained push's real-vs-target displacement never shrinks on
    // its own - without these flags PieceNudgeInput would fire TryMove/TryRotate every single
    // frame the push stays past threshold. Only true again once the piece actually falls (a real
    // OnPieceMoved), so a push yields exactly one shift/rotation per fall step no matter how long
    // it's held.
    public bool CanShiftAgain { get; private set; } = true;
    public bool CanRotateAgain { get; private set; } = true;

    void OnEnable()
    {
        gridManager.OnPieceSpawned += HandlePieceMoved;
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceShifted += HandlePieceShifted;
        gridManager.OnPieceRotated += HandlePieceRotated;
    }

    void OnDisable()
    {
        gridManager.OnPieceSpawned -= HandlePieceMoved;
        gridManager.OnPieceMoved -= HandlePieceMoved;
        gridManager.OnPieceShifted -= HandlePieceShifted;
        gridManager.OnPieceRotated -= HandlePieceRotated;
    }

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        int myVersion = ++traceVersion;
        IsSettled = false;
        CanShiftAgain = true;
        CanRotateAgain = true;
        _ = RetraceShape(gridManager.GetPieceTraceWaypoints(), myVersion);
    }

    // The handle itself stays put wherever it currently is on a shift - it only picks up the
    // piece's new (possibly shifted) position on the next fall step's full retrace. No snap/
    // visual feedback for shifts for now; a duration-based move-distance + sound feedback design
    // is planned for later (sound design pass), not this one.
    void HandlePieceShifted(Vector2Int direction)
    {
        CanShiftAgain = false;
    }

    // Same reasoning as HandlePieceShifted: rotating the piece's shape is only felt once the
    // piece actually falls and the next full retrace picks up the new shape - no immediate
    // retrace on rotation itself.
    void HandlePieceRotated(List<Vector2Int> cells)
    {
        CanRotateAgain = false;
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
                // PantoHandle.FixedUpdate continuously re-sends once following (no arrival check
                // needed or possible there). If this first distance is large (e.g. the handle is
                // resting far from the very first piece's spawn cell), awaiting it properly keeps
                // IsSettled false for that whole real travel time - without this await, the fixed
                // per-waypoint delays below would finish (and IsSettled/PieceNudgeInput would
                // unblock) long before the handle physically catches up, and the resulting
                // real-vs-target gap gets misread as a deliberate push (random extra shifts/
                // rotations while the first piece is still catching up).
                await PantoSystem.Instance.FollowTarget(isUpper: false, gameObject, handleSpeed);
                if (version != traceVersion) return;
            }

            await Task.Delay(TimeSpan.FromSeconds(isFirst ? firstWaypointPauseSeconds : cellPauseSeconds));
            isFirst = false;
        }

        if (version == traceVersion) IsSettled = true;
    }
}
