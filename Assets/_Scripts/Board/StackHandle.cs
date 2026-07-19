using System;
using System.Threading.Tasks;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Stack handle (Me/Upper): the "magnetic grid" feel, driven by a Unity-side FORCE FIELD. This is a
/// deliberate RETURN to the implementation as it stood BEFORE the first big stack-handle rework
/// (commit 3d1deb7), chosen by the user over both the firmware boundary-rail version that replaced
/// it (stable, but the cells could not really be felt) and the simpler original spring (d1c4388).
///
/// Three parts, all tuned independently:
///  - a flat centre DEADZONE with no force, so a let-go handle coasts to rest on the mechanism's own
///    friction instead of being sprung around the centre. Sized (>= 0.5 - cellHysteresis) so that
///    right after a cell switch the handle is already inside the new cell's deadzone - otherwise the
///    switch drops it half a cell from the new centre where the pull is strongest and yanks it
///    across, overshooting;
///  - an edge DETENT: the pull ramps from 0 at the deadzone edge up to pullStrength at the cell
///    boundary, so it resists leaving a cell rather than acting as a strong central spring;
///  - a CORNER PULL: an extra, independently tuned force that grows as the handle nears a corner, to
///    fight diagonal crossings.
/// On top, the target cell uses hysteresis and steps CARDINALLY (one cell along whichever axis moved
/// furthest, never diagonally to a corner cell in one go), so the force never flips while the handle
/// rests on a boundary.
///
/// Deliberately NO Unity-side velocity term anywhere: every attempt at one amplified sensor noise
/// and made the behaviour worse.
///
/// Two consequences of force mode that are easy to forget:
///  - it OVERRIDES firmware wall rendering on this handle, so while force is being sent the outer
///    frame is not felt as a hard wall. The frame obstacle is still registered (it is this
///    component's, GameManager does not create one) and is what the emulator raycast clamps
///    against, but on hardware the edge is communicated by OnEdgePush -> fail sound instead;
///  - it is hardware-only. PantoSystem.ApplyForce is a no-op in debug/emulator mode, where the
///    handle is mouse-driven and this component only tracks position.
///
/// Occupancy is NOT expressed as force (the old locked-cell buzz was removed on request): cell
/// changes fire OnCellChanged(cell, occupied) and GameAudio plays the per-step click off it (dull =
/// empty, higher = locked stack OR the falling piece). Reaching the field border fires OnEdgePush.
/// The force field is purely about grid navigation.
///
/// The frame obstacle is taken DOWN before any positioning move and put back up on arrival - with
/// it up the handle (which starts outside the field) collides with it and never gets in.
/// </summary>
public class StackHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;

    [Header("Magnetic grid force")]
    [Tooltip("Max pull, as a [0,1] force (clamped to unit length). Only acts in the ring between " +
        "centerDeadzone and the cell boundary, so keep it modest.")]
    [SerializeField] float pullStrength = 0.2f;
    [Tooltip("Fraction of a cell (from the centre) that is force-free. MUST be >= 0.5 - " +
        "cellHysteresis so a cell switch doesn't yank the handle across the cell (overshoot).")]
    [SerializeField] float centerDeadzone = 0.4f;
    [Tooltip("Extra fraction of a cell past the midpoint the handle must be pushed before the " +
        "target cell switches, so the force doesn't flip on a boundary. Also gates the step sound.")]
    [SerializeField] float cellHysteresis = 0.1f;
    [Tooltip("Extra pull toward the centre (independent of pullStrength) that ramps up as the " +
        "handle nears a CORNER, to fight diagonal crossings. 0 disables it.")]
    [SerializeField] float cornerPullStrength = 0.4f;
    [Tooltip("Corner-ness (the smaller of the two axis offsets, as a fraction of a cell) at which " +
        "the corner pull starts, ramping to full at 0.5.")]
    [SerializeField] float cornerThreshold = 0.25f;
    [Tooltip("Falloff sharpness of the corner pull. 1 = linear; higher concentrates it right at " +
        "the corner (very strong at the corner, dropping off quickly as you move away).")]
    [SerializeField] float cornerSharpness = 3f;

    [Header("Feedback")]
    [Tooltip("How far past the field border (fraction of a cell) the handle must be before the fail " +
        "sound fires. 0 = right AT the border. Negative would fire just before reaching it.")]
    [SerializeField] float edgePushTriggerCells = 0f;
    [Tooltip("How far back inside the handle must come before the edge sound can fire again, so " +
        "resting against the border doesn't chatter. Fraction of a cell.")]
    [SerializeField] float edgePushRearmCells = 0.1f;

    [Header("Positioning")]
    [Tooltip("Speed used to move the handle to the bottom-right cell at game start (brief position " +
        "control, handle freed again afterwards).")]
    [SerializeField] float moveToStartSpeed = 15f;
    [Tooltip("Give up the start-corner move after this long even if the handle hasn't reported " +
        "arrival, so it never hangs positioning forever.")]
    [SerializeField] float moveToStartTimeoutSeconds = 4f;
    [Tooltip("Settle time after freeing the handle at the start corner before the follow target is " +
        "destroyed - without it the handle ended up slightly off the corner (a race on Free()).")]
    [SerializeField] float settleAfterMoveSeconds = 0.3f;
    [SerializeField] bool debugLogging = false;

    // Fired when the handle crosses into a different cell: (cell, cell is occupied).
    // GameAudio plays the per-step click off this.
    public event Action<Vector2Int, bool> OnCellChanged;

    // Fired once when the handle reaches the border of the playing field. GameAudio plays the fail
    // sound off this. Re-arms only after the handle has come back inside (see DetectEdgePush).
    public event Action OnEdgePush;

    Vector2Int currentCell;
    bool hasCurrent;
    bool fieldReady;
    bool positioning;
    Vector3 fieldMin;
    Vector3 fieldMax;
    bool edgePushArmed = true;
    // The frame wall is only put up once the handle has been driven to its start position; it stays
    // down during any positioning move so the handle can drive in unobstructed.
    bool wallsReleased;
    bool forceActive;
    // Where the handle sat before it was first driven into the field - it gets parked back here for
    // tutorial steps that don't use the upper handle (MoveOutOfField). `parked` then suppresses cell
    // tracking / edge feedback / force, since it's sitting outside the playfield.
    Vector3 homePosition;
    bool hasHome;
    bool parked;
    // The outer field wall (upper-handle only) - created on the first BuildField, then just
    // enabled/disabled so the handle can drive in unobstructed.
    PantoBoxCollider frameObstacle;

    void FixedUpdate()
    {
        // GridManager.Initialize runs in GameManager.Start - execution order relative to this
        // component isn't guaranteed, so set up lazily once the grid knows its size AND the handle
        // has reached its start position, and isn't mid-move.
        if (gridManager == null || gridManager.CellSize <= 0f) return;
        if (!fieldReady && wallsReleased && !positioning) BuildField();

        Vector3 real = PantoSystem.Instance.GetHandlePosition(true, transform.position);
        transform.position = real;

        // The force field runs ONLY once the handle has actually been driven to its start corner and
        // the field has been set up. Three separate cases are excluded, and all three matter:
        //  - `positioning`: a move to the start corner (or back out) is in progress. Force must be
        //    off for the WHOLE move - it is a motor command like the position control it would
        //    otherwise fight, and it would rattle off a step click per cell swept across;
        //  - `parked`: the handle is sitting outside the playfield, where there is nothing to feel;
        //  - `!fieldReady`: the window between a resize (RebuildGates tears the field down) and the
        //    next arrival. Without this the field kept running on a stale currentCell and stale
        //    fieldMin/fieldMax after a resize - pulling toward a cell centre from the OLD grid and
        //    testing edge pushes against the OLD bounds.
        // Because fieldReady is only set by BuildField, which itself only runs once wallsReleased is
        // set on arrival, "force is off while moving to the start position" holds by construction
        // rather than by remembering to switch it off at each call site.
        if (positioning || parked || !fieldReady)
        {
            ReleaseForce();
            return;
        }

        float cellSize = gridManager.CellSize;
        Vector2Int nearest = gridManager.WorldToGrid(real);

        // Hysteresis + cardinal-only stepping: stick with the current target cell until the handle
        // is pushed clearly past a boundary, then step it by ONE cell along whichever axis it moved
        // furthest on - never diagonally to a corner cell in one go. This is also what gates the
        // step sound, so the click lands on a real, committed cell change.
        if (!hasCurrent)
        {
            currentCell = nearest;
            hasCurrent = true;
        }
        else
        {
            Vector3 fromCurrent = real - gridManager.GridToWorld(currentCell);
            float ax = Mathf.Abs(fromCurrent.x) / cellSize;
            float az = Mathf.Abs(fromCurrent.z) / cellSize;
            if (Mathf.Max(ax, az) > 0.5f + cellHysteresis)
            {
                Vector2Int step = ax >= az
                    ? new Vector2Int((int)Mathf.Sign(fromCurrent.x), 0)
                    : new Vector2Int(0, (int)Mathf.Sign(fromCurrent.z));
                Vector2Int stepped = new Vector2Int(
                    Mathf.Clamp(currentCell.x + step.x, 0, gridManager.Width - 1),
                    Mathf.Clamp(currentCell.y + step.y, 0, gridManager.Height - 1));

                if (stepped != currentCell)
                {
                    currentCell = stepped;
                    // Occupied = locked stack OR the current falling piece, so the higher click also
                    // fires when moving over the falling piece.
                    bool moved = gridManager.IsOccupied(currentCell);
                    if (debugLogging) Debug.Log($"[StackHandle] cell={currentCell} occupied={moved}");
                    OnCellChanged?.Invoke(currentCell, moved);
                }
            }
        }

        DetectEdgePush(real, cellSize);
        ApplyMagneticForce(real, nearest, cellSize);
    }

    // The magnetic grid: a force-free deadzone around the cell centre, an edge detent ramping up to
    // pullStrength at the boundary, and a separate corner pull. Occupancy is NOT expressed as force
    // any more - the per-step click (OnCellChanged -> GameAudio) is the only "this cell is occupied"
    // channel, which keeps the force field purely about grid navigation.
    void ApplyMagneticForce(Vector3 real, Vector2Int nearest, float cellSize)
    {
        Vector3 toCenter = gridManager.GridToWorld(currentCell) - real;
        toCenter.y = 0f;
        float distance = toCenter.magnitude;

        Vector3 force = Vector3.zero;
        if (distance > cellSize * centerDeadzone && distance > 1e-4f)
        {
            // Edge detent: nothing inside the deadzone, ramping to pullStrength at the boundary.
            float t = Mathf.Clamp01(Mathf.InverseLerp(cellSize * centerDeadzone, cellSize * 0.5f, distance));
            force += (toCenter / distance) * (t * pullStrength);
        }

        // Corner pull: an extra pull that grows as the handle nears a corner, to fight diagonal
        // crossings. It targets the PHYSICALLY NEAREST cell centre, NOT the hysteretic currentCell:
        // at a shared corner currentCell often lags to the other cell, whose centre is behind the
        // handle, so pulling toward it dragged the handle further INTO the corner instead of out.
        // Toward the nearest centre it always points out of the corner the handle is actually in.
        // "Corner-ness" is the SMALLER of the two axis offsets, so it only fires near corners (both
        // axes far from centre), not near edge midpoints.
        Vector3 toNearest = gridManager.GridToWorld(nearest) - real;
        toNearest.y = 0f;
        float nearDist = toNearest.magnitude;
        float cornerness = Mathf.Min(Mathf.Abs(toNearest.x), Mathf.Abs(toNearest.z)) / cellSize;
        if (nearDist > 1e-4f && cornerness > cornerThreshold)
        {
            // Sharpen the ramp so the pull is concentrated right at the corner and falls off
            // quickly as you move away (cornerSharpness > 1).
            float c = Mathf.Pow(Mathf.Clamp01(Mathf.InverseLerp(cornerThreshold, 0.5f, cornerness)), cornerSharpness);
            force += (toNearest / nearDist) * (c * cornerPullStrength);
        }

        PantoSystem.Instance.ApplyForce(true, force, force.magnitude);
        forceActive = true;
    }


    // Hands the handle back to the firmware. Called whenever force must not be running (positioning,
    // parked, disabled) - leaving force mode on would keep overriding wall rendering and keep the
    // last force vector applied.
    void ReleaseForce()
    {
        if (!forceActive) return;
        forceActive = false;
        if (PantoSystem.Instance != null) PantoSystem.Instance.StopApplyingForce(true);
    }

    // Fail feedback for reaching the edge of the playing field. `push` is the SIGNED distance past
    // the field boundary along whichever outer edge the handle is against - negative while inside,
    // zero exactly ON the border. With edgePushTriggerCells at 0 the sound therefore fires the
    // moment the border is reached, rather than after being pushed a margin past it.
    // Edge-triggered: it fires once and only re-arms after the handle has come back inside by
    // edgePushRearmCells, so resting against the border doesn't chatter.
    void DetectEdgePush(Vector3 real, float cellSize)
    {
        // Interior cell: nowhere near an outer edge, so nothing to test - just re-arm.
        float push = float.NegativeInfinity;
        if (currentCell.x == 0) push = Mathf.Max(push, fieldMin.x - real.x);
        else if (currentCell.x == gridManager.Width - 1) push = Mathf.Max(push, real.x - fieldMax.x);
        if (currentCell.y == 0) push = Mathf.Max(push, fieldMin.z - real.z);
        else if (currentCell.y == gridManager.Height - 1) push = Mathf.Max(push, real.z - fieldMax.z);

        if (float.IsNegativeInfinity(push))
        {
            edgePushArmed = true;
            return;
        }

        float trigger = edgePushTriggerCells * cellSize;
        if (push >= trigger)
        {
            if (edgePushArmed)
            {
                edgePushArmed = false;
                if (debugLogging) Debug.Log($"[StackHandle] edge reached ({push / cellSize:F2} cells past border)");
                OnEdgePush?.Invoke();
            }
        }
        else if (push < trigger - edgePushRearmCells * cellSize)
        {
            edgePushArmed = true;
        }
    }

    // Puts the outer frame wall up and caches the field bounds the edge-push test needs. Only runs
    // once the handle is in position (see wallsReleased).
    void BuildField()
    {
        fieldReady = true;

        // The frame is an upper-handle-only wall, so it belongs to this component's lifecycle:
        // created once, then just re-enabled, so it is DOWN while the handle drives to the corner
        // (it starts outside the field - with the frame up it can never get in).
        if (frameObstacle == null)
            frameObstacle = PantoSystem.Instance.CreateBoxObstacle(gridManager.Frame.gameObject, onUpper: true, onLower: false);
        else
            frameObstacle.Enable();

        float cellSize = gridManager.CellSize;
        Vector3 min = gridManager.GridToWorld(new Vector2Int(0, 0)) - new Vector3(cellSize, 0f, cellSize) / 2f;
        fieldMin = min;
        fieldMax = min + new Vector3(gridManager.Width * cellSize, 0f, gridManager.Height * cellSize);

        if (debugLogging) Debug.Log($"[StackHandle] field ready ({gridManager.Width}x{gridManager.Height})");
    }

    /// <summary>
    /// Drops the field setup so it can be rebuilt for a new grid size (tutorial resize). It stays
    /// down until the next MoveToStartCorner has put the handle in position. Cell tracking resets so
    /// the first cell after the resize doesn't fire a spurious step.
    /// </summary>
    public void RebuildGates()
    {
        TearDownField();
        hasCurrent = false;
    }

    // Takes the frame wall back down and suspends rebuilding until the handle is back in position.
    // The frame is only disabled (single packet) since its geometry never changes. Clearing
    // fieldReady also switches the force field off (see FixedUpdate); ReleaseForce is called here
    // too so the handle is handed back to the firmware IMMEDIATELY rather than a frame later.
    void TearDownField()
    {
        ReleaseForce();
        if (frameObstacle != null) frameObstacle.Disable();
        fieldReady = false;
        wallsReleased = false;
    }

    /// <summary>
    /// Drives the handle to the bottom-right cell via position control, then frees it again (so the
    /// force field can take over). Called by GameManager on game start/restart. Cell tracking and
    /// force are muted during the sweep.
    ///
    /// Uses a persistent follow target re-commanded every frame (the toolkit's continuous-follow
    /// via MarkFollowReady) rather than PantoHandle.MoveToPosition, which sends the position ONCE
    /// and frees the handle after a fixed ~3s SwitchTo timeout - on this device that single packet
    /// can be dropped racing firmware readiness, or the handle can be too far to arrive in 3s, so
    /// it never reaches the corner. Re-sending every frame guarantees arrival; we free only once
    /// the handle is actually there (or after moveToStartTimeoutSeconds as a failsafe).
    /// </summary>
    public async Task MoveToStartCorner()
    {
        if (gridManager == null || gridManager.CellSize <= 0f) return;

        // Remember where the handle sits before we ever drive it in - that's the position we park
        // it back at when a tutorial step doesn't use the upper handle (see MoveOutOfField).
        if (!hasHome)
        {
            homePosition = PantoSystem.Instance.GetHandlePosition(true, transform.position);
            hasHome = true;
        }

        Vector2Int corner = new Vector2Int(gridManager.Width - 1, 0);
        await DriveHandleTo(gridManager.GridToWorld(corner));

        currentCell = corner;
        hasCurrent = true;
        parked = false;
        wallsReleased = true; // in position - the next FixedUpdate puts the frame up
    }

    /// <summary>
    /// Parks the handle back OUTSIDE the playfield, where it sat before it was first driven in -
    /// for tutorial steps where the upper handle plays no role and leaving it sitting in the field
    /// is just confusing. The frame stays DOWN (there is nothing to feel out there); the next
    /// MoveToStartCorner brings it back in and puts it up again.
    /// </summary>
    public async Task MoveOutOfField()
    {
        if (gridManager == null || gridManager.CellSize <= 0f) return;
        if (!hasHome) return; // never captured a home position - nothing to return to

        await DriveHandleTo(homePosition);
        parked = true;    // suppress cell tracking / edge feedback / force while it sits outside
        hasCurrent = false;
    }

    // Drives the handle to a world position, taking the frame down first (it would block it) and
    // leaving it down - the caller decides whether to put it back up (wallsReleased). Force mode is
    // released first: position control and force are both motor commands and must not overlap.
    async Task DriveHandleTo(Vector3 target)
    {
        positioning = true;
        ReleaseForce();

        // Wall out of the way FIRST: with it up the handle collides on its way and never arrives.
        TearDownField();
        float drainDeadline = Time.time + 3f;
        while (!PantoSystem.Instance.ObstacleQueueEmpty && Time.time < drainDeadline)
            await Task.Yield(); // let the change actually reach the device before driving through

        GameObject targetObj = new GameObject("StackHandleTarget");
        targetObj.transform.position = target;
        _ = PantoSystem.Instance.FollowTarget(isUpper: true, targetObj, moveToStartSpeed);
        PantoSystem.Instance.MarkFollowReady(isUpper: true); // engage continuous follow immediately
        PantoSystem.Instance.SetHandleSpeed(isUpper: true, moveToStartSpeed);

        float arriveTolerance = 0.1f * gridManager.CellSize;
        float deadline = Time.time + moveToStartTimeoutSeconds;
        while (Time.time < deadline)
        {
            Vector3 real = PantoSystem.Instance.GetHandlePosition(true, target);
            Vector3 d = real - target;
            d.y = 0f;
            if (d.magnitude <= arriveTolerance) break;
            await Task.Yield();
        }

        PantoSystem.Instance.FreeHandle(isUpper: true); // free again before force mode resumes

        // Let the handle settle on the target BEFORE destroying the follow target. Destroying it
        // immediately after Free() left the handle slightly off (a race: the target vanished while
        // the firmware was still finishing the transition) - this settle window fixed it on
        // hardware. Keep it.
        await Task.Delay(TimeSpan.FromSeconds(settleAfterMoveSeconds));
        Destroy(targetObj);

        positioning = false;
    }

    void OnDisable() => ReleaseForce();
}
