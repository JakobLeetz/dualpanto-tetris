using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Stack handle (Me/Upper): cell navigation via a FIRMWARE-rendered grid of RAIL lines on the
/// inner cell BOUNDARIES (GridLineCollider, upper handle only). A firmware rail acts as a soft
/// BARRIER (hardware-verified): the handle moves FREELY within a cell and feels resistance when
/// crossing a boundary into the next cell. The handle stays completely FREE - no Unity-side force
/// or servo (every such attempt ran away or oscillated on this device; see project memory for the
/// full history). This is the most stable variant found and the one chosen to ship.
///
/// Free movement within a cell is accepted (not a hard per-cell lock): the boundary rails give a
/// felt bump per crossing, and cell changes fire OnCellChanged(cell, locked) so GameAudio plays a
/// per-step click (dull = empty cell, higher = occupied cell = locked stack OR the falling
/// piece). Cell changes use hysteresis
/// (cellSwitchHysteresisCells) so the click fires once the handle has really popped through into
/// the next cell, not the instant it touches the boundary line. No buzz on stacked cells (would
/// need force mode, which overrides the rail rendering).
///
/// Pushing outward against the frame from an edge cell fires OnEdgePush (-> GameAudio fail sound),
/// edge-triggered past a margin so a light touch doesn't retrigger.
///
/// The rails are only put up AFTER the handle has been driven to its start position, and are taken
/// down again before any such move (see MoveToStartCorner / TearDownGates) - with walls already in
/// place the handle collides with them on the way and never reaches the corner.
///
/// All geometry is uploaded once through PantoSystem's staggered queue (one obstacle per frame),
/// so the serial channel is quiet during play (dynamic streaming disturbed the it-handle's
/// position stream). The rail visuals also carry no colliders, so the emulator raycast is
/// unaffected (rail FORCES are hardware-only).
/// </summary>
public class StackHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;

    [Tooltip("Rail barrier band width as a fraction of a cell (crossing resistance).")]
    [SerializeField] float railDisplacementCells = 0.15f;
    [Tooltip("Create every rail twice, once per direction (a single firmware rail acts on one " +
        "side of its line only).")]
    [SerializeField] bool railBothSides = true;
    [Tooltip("Extra distance PAST a cell boundary (fraction of a cell) the handle must move " +
        "before the cell change registers - so the step sound fires once you've really popped " +
        "through the rail into the next cell, not the instant you touch the boundary line.")]
    [SerializeField] float cellSwitchHysteresisCells = 0.2f;
    [Tooltip("In an edge cell, how far past the field boundary (fraction of a cell) the handle " +
        "must be pushed against the frame wall before the fail sound fires - so a light touch " +
        "doesn't trigger it. Fires once per push (re-arms when eased back inside). Hardware-only " +
        "(needs pushing physically past the wall - tune to the device's wall compliance).")]
    [SerializeField] float edgePushMarginCells = 0.2f;
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

    // Fired when the handle crosses into a different cell: (cell, cell is locked/stacked).
    // GameAudio plays the per-step click off this.
    public event Action<Vector2Int, bool> OnCellChanged;

    // Fired once when the handle is pushed against the field frame from an edge cell (trying to
    // move outside the playfield). GameAudio plays the fail sound off this.
    public event Action OnEdgePush;

    Vector2Int currentCell;
    bool hasCurrent;
    bool built;
    bool positioning;
    Vector3 fieldMin;
    Vector3 fieldMax;
    bool edgePushArmed = true;
    // The rails are only built once the handle has been driven to its start position (see
    // MoveToStartCorner). Building them earlier means the handle has to cross walls on its way
    // there and gets stuck on them - so they stay down until we're in position.
    bool gatesReleased;
    // Where the handle sat before it was first driven into the field - it gets parked back here for
    // tutorial steps that don't use the upper handle (MoveOutOfField). `parked` then suppresses cell
    // tracking / edge feedback, since it's sitting outside the playfield.
    Vector3 homePosition;
    bool hasHome;
    bool parked;
    // The outer field wall (upper-handle only) - created on the first BuildGates, then just
    // enabled/disabled along with the rails so the handle can drive in unobstructed.
    PantoBoxCollider frameObstacle;
    readonly List<GridLineCollider> gates = new List<GridLineCollider>();

    void FixedUpdate()
    {
        // GridManager.Initialize runs in GameManager.Start - execution order relative to this
        // component isn't guaranteed, so build lazily once the grid knows its size AND the handle
        // has reached its start position (gatesReleased) and isn't mid-move.
        if (gridManager == null || gridManager.CellSize <= 0f) return;
        if (!built && gatesReleased && !positioning) BuildGates();

        Vector3 real = PantoSystem.Instance.GetHandlePosition(true, transform.position);
        transform.position = real;

        // No tracking while a positioning move sweeps the handle across the field (it would rattle
        // off a click per crossed cell), nor while it's parked outside the field.
        if (positioning || parked) return;

        float cellSize = gridManager.CellSize;
        Vector2Int nearest = gridManager.WorldToGrid(real);

        if (!hasCurrent)
        {
            currentCell = nearest;
            hasCurrent = true;
        }
        else if (nearest != currentCell)
        {
            // Hysteresis: only register the change once the handle has moved clearly PAST the
            // boundary into the new cell (popped through the rail), not the instant it crosses the
            // midpoint boundary line - otherwise the step sound fires while still pushing the rail.
            Vector3 fromCenter = real - gridManager.GridToWorld(currentCell);
            float threshold = (0.5f + cellSwitchHysteresisCells) * cellSize;
            if (Mathf.Abs(fromCenter.x) > threshold || Mathf.Abs(fromCenter.z) > threshold)
            {
                currentCell = nearest;
                // Occupied = locked stack OR the current falling piece, so the higher click also
                // fires when moving over the falling piece.
                bool occupied = gridManager.IsOccupied(nearest);
                if (debugLogging) Debug.Log($"[StackHandle] cell={nearest} occupied={occupied}");
                OnCellChanged?.Invoke(nearest, occupied);
            }
        }

        DetectEdgePush(real, cellSize);
    }

    // Fail feedback for pushing outward against the frame from an edge cell. Measures how far the
    // handle is pushed PAST the field boundary (only relevant in an outermost cell), edge-triggered
    // with hysteresis so a sustained push fires once and a light touch never fires. Hardware-only:
    // in the emulator the frame collider raycast clamps the handle at the wall so it never reads
    // past.
    void DetectEdgePush(Vector3 real, float cellSize)
    {
        float push = 0f;
        if (currentCell.x == 0) push = Mathf.Max(push, fieldMin.x - real.x);
        else if (currentCell.x == gridManager.Width - 1) push = Mathf.Max(push, real.x - fieldMax.x);
        if (currentCell.y == 0) push = Mathf.Max(push, fieldMin.z - real.z);
        else if (currentCell.y == gridManager.Height - 1) push = Mathf.Max(push, real.z - fieldMax.z);

        float margin = edgePushMarginCells * cellSize;
        if (push > margin)
        {
            if (edgePushArmed)
            {
                edgePushArmed = false;
                if (debugLogging) Debug.Log($"[StackHandle] edge push ({push / cellSize:F2} cells past edge)");
                OnEdgePush?.Invoke();
            }
        }
        else if (push < margin * 0.5f)
        {
            edgePushArmed = true;
        }
    }

    // Puts up ALL walls the upper handle can feel: the outer field FRAME plus rail lines on the
    // INNER cell boundaries (outermost skipped - the frame covers those): 9 vertical + 19
    // horizontal for 10x20, doubled if railBothSides. Uploaded through PantoSystem's staggered
    // queue. Only runs once the handle is in position (see gatesReleased).
    void BuildGates()
    {
        built = true;

        // The frame is an upper-handle-only wall, so it belongs to this component's wall lifecycle:
        // created once, then just re-enabled, so it is DOWN while the handle drives to the corner
        // (it starts outside the field - with the frame up it can never get in).
        if (frameObstacle == null)
            frameObstacle = PantoSystem.Instance.CreateBoxObstacle(gridManager.Frame.gameObject, onUpper: true, onLower: false);
        else
            frameObstacle.Enable();

        float cellSize = gridManager.CellSize;
        Vector3 min = gridManager.GridToWorld(new Vector2Int(0, 0)) - new Vector3(cellSize, 0f, cellSize) / 2f;
        float width = gridManager.Width * cellSize;
        float height = gridManager.Height * cellSize;
        fieldMin = min;
        fieldMax = min + new Vector3(width, 0f, height);
        float displacement = railDisplacementCells * cellSize;

        void CreateGate(Vector3 a, Vector3 b)
        {
            gates.Add(PantoSystem.Instance.CreateGridLine(a, b, GridLineCollider.Kind.Rail, displacement, onUpper: true, onLower: false));
            if (railBothSides)
                gates.Add(PantoSystem.Instance.CreateGridLine(b, a, GridLineCollider.Kind.Rail, displacement, onUpper: true, onLower: false));
        }

        for (int i = 1; i < gridManager.Width; i++)
        {
            float x = min.x + i * cellSize;
            CreateGate(new Vector3(x, 0f, min.z), new Vector3(x, 0f, min.z + height));
        }
        for (int j = 1; j < gridManager.Height; j++)
        {
            float z = min.z + j * cellSize;
            CreateGate(new Vector3(min.x, 0f, z), new Vector3(min.x + width, 0f, z));
        }

        if (debugLogging)
            Debug.Log($"[StackHandle] built {gates.Count} boundary rails (railDisplacement={railDisplacementCells})");
    }

    /// <summary>
    /// Drops the rail grid so it can be rebuilt for a new grid size (tutorial resize). The rails
    /// stay DOWN until the next MoveToStartCorner has put the handle in position - otherwise the
    /// handle would have to fight through walls on its way there. Cell tracking resets so the first
    /// cell after the resize doesn't fire a spurious step.
    /// </summary>
    public void RebuildGates()
    {
        TearDownGates();
        hasCurrent = false;
    }

    // Takes every wall the upper handle can feel back down (rails removed, frame disabled) and
    // suspends rebuilding (gatesReleased = false) until the handle is back in position. Rail
    // removal is queued one-op-per-frame by PantoSystem (anti-flood); the frame is only disabled
    // (single packet) since its geometry never changes and it can simply be re-enabled.
    void TearDownGates()
    {
        foreach (GridLineCollider gate in gates)
        {
            if (gate == null) continue;
            PantoSystem.Instance.RemoveObstacle(gate); // captures id + queues the serial remove
            Destroy(gate.gameObject);                  // then drop the Unity object + its visual
        }
        gates.Clear();
        if (frameObstacle != null) frameObstacle.Disable();
        built = false;
        gatesReleased = false;
    }

    /// <summary>
    /// Drives the handle to the bottom-right cell via position control, then frees it again (so the
    /// rails render). Called by GameManager on game start/restart. Cell tracking is muted during
    /// the sweep so it doesn't rattle off step clicks.
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
        gatesReleased = true; // in position - the next FixedUpdate builds the rails
    }

    /// <summary>
    /// Parks the handle back OUTSIDE the playfield, where it sat before it was first driven in -
    /// for tutorial steps where the upper handle plays no role and leaving it sitting in the field
    /// is just confusing. The walls stay DOWN (there is nothing to feel out there); the next
    /// MoveToStartCorner brings it back in and puts them up again.
    /// </summary>
    public async Task MoveOutOfField()
    {
        if (gridManager == null || gridManager.CellSize <= 0f) return;
        if (!hasHome) return; // never captured a home position - nothing to return to

        await DriveHandleTo(homePosition);
        parked = true;    // suppress cell tracking / edge feedback while it sits outside
        hasCurrent = false;
    }

    // Drives the handle to a world position, taking ALL walls down first (they would block it) and
    // leaving them down - the caller decides whether to put them back up (gatesReleased). Uses a
    // persistent follow target re-commanded every frame (the toolkit's continuous-follow via
    // MarkFollowReady) rather than PantoHandle.MoveToPosition, which sends the position ONCE and
    // frees the handle after a fixed ~3s SwitchTo timeout - on this device that single packet can be
    // dropped racing firmware readiness, or the handle can be too far to arrive in 3s, so it never
    // arrives. Re-sending every frame guarantees arrival; we free only once it's actually there (or
    // after moveToStartTimeoutSeconds as a failsafe).
    async Task DriveHandleTo(Vector3 target)
    {
        positioning = true;

        // Walls out of the way FIRST: with them up the handle collides on its way and never arrives.
        TearDownGates();
        float drainDeadline = Time.time + 3f;
        while (!PantoSystem.Instance.ObstacleQueueEmpty && Time.time < drainDeadline)
            await Task.Yield(); // let the removals actually reach the device before driving through

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

        PantoSystem.Instance.FreeHandle(isUpper: true); // free again (walls only work on a free handle)

        // Let the handle settle on the target BEFORE destroying the follow target. Destroying it
        // immediately after Free() left the handle slightly off (a race: the target vanished while
        // the firmware was still finishing the transition) - this settle window fixed it on
        // hardware. Keep it.
        await Task.Delay(TimeSpan.FromSeconds(settleAfterMoveSeconds));
        Destroy(targetObj);

        positioning = false;
    }
}
