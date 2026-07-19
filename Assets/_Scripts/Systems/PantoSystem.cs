using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Thin wrapper around the DualPanto toolkit: handle positioning and box-collider obstacle
/// registration. Knows nothing about Tetris (grid/pieces) - that lives in Managers/Board.
/// </summary>
public class PantoSystem : StaticInstance<PantoSystem>
{
    // Registering obstacles before the device/sync handshake completes can silently fail
    // (toolkit README troubleshooting - the toolkit's own reference Obstacle Manager waits 1s
    // for the same reason). Any obstacle requested before this deadline is queued and created
    // once it passes, so every caller (frame, locked blocks, ...) gets this for free instead of
    // guessing a fixed delay themselves.
    const float ReadyDelaySeconds = 3f;

    UpperHandle upperHandle; // "Me"/stack handle, moved by the player
    LowerHandle lowerHandle; // "It"/piece handle, actuated by the device
    DualPantoSync sync;
    float readyAt;
    readonly List<Action> pendingObstacles = new List<Action>();
    readonly List<PantoCollider> allObstacles = new List<PantoCollider>();

    // Mirrors PantoHandle.MaxMovementSpeed() (protected there) - the toolkit clamps every speed it
    // sends the firmware to this, so SetHandleSpeed does the same when re-sending directly.
    const float MaxMovementSpeed = 100f;

    protected override void Awake()
    {
        base.Awake();
        readyAt = Time.time + ReadyDelaySeconds;
        GameObject panto = GameObject.Find("Panto");
        if (panto == null)
        {
            Debug.LogError("[PantoSystem] No GameObject named 'Panto' found in scene.");
            return;
        }
        upperHandle = panto.GetComponent<UpperHandle>();
        lowerHandle = panto.GetComponent<LowerHandle>();
        sync = panto.GetComponent<DualPantoSync>();
    }

    bool IsReady => Time.time >= readyAt;

    /// <summary>
    /// True once every queued obstacle create/remove has actually been sent to the device. Lets a
    /// caller wait for e.g. wall REMOVALS to land before driving the handle through where they were
    /// (the queue is staggered one op per frame, so removals aren't instant).
    /// </summary>
    public bool ObstacleQueueEmpty => pendingObstacles.Count == 0;

    void Update()
    {
        // Flush the pending queue ONE entry per frame instead of all at once: sending many
        // obstacle-creation packets in a single frame crashes the device (toolkit README FAQ
        // "too many obstacles at once"; the toolkit's own ColliderRegistry/ColliderPolyline
        // stagger with 10-20ms delays for the same reason - observed here as a connection loss
        // when 28 wall obstacles were flushed in one frame).
        if (pendingObstacles.Count > 0 && IsReady)
        {
            pendingObstacles[0]();
            pendingObstacles.RemoveAt(0);
        }

        // E/D toggle all obstacles on/off at runtime, matching the toolkit's own Obstacle
        // Manager convention - reimplemented here since Obstacle Manager itself can't be used
        // (see GameManager/LockedBlocksView for why).
        if (Input.GetKeyDown(KeyCode.E)) SetAllObstaclesEnabled(true);
        else if (Input.GetKeyDown(KeyCode.D)) SetAllObstaclesEnabled(false);
    }

    void SetAllObstaclesEnabled(bool enable)
    {
        foreach (PantoCollider obstacle in allObstacles)
        {
            if (obstacle == null) continue;
            if (enable) obstacle.Enable();
            else obstacle.Disable();
        }
    }

    PantoHandle GetHandle(bool isUpper) => isUpper ? (PantoHandle)upperHandle : lowerHandle;

    /// <summary>
    /// Debug-safe handle position: raycasts against real Unity colliders in the emulator so a
    /// GameObject tracking this doesn't clip through obstacles (see toolkit README troubleshooting -
    /// use this instead of PantoHandle.GetPosition()).
    /// </summary>
    public Vector3 GetHandlePosition(bool isUpper, Vector3 currentPosition)
    {
        return GetHandle(isUpper).HandlePosition(currentPosition);
    }

    /// <summary>
    /// Switches the handle to continuously follow target's position (toolkit's own "SwitchTo" a
    /// persistent object pattern - PantoHandle.FixedUpdate re-reads the target's transform every
    /// frame). Call once per target lifetime; afterwards just move the target's transform and the
    /// handle keeps following, no need to call this again.
    /// </summary>
    public Task FollowTarget(bool isUpper, GameObject target, float speed)
    {
        return GetHandle(isUpper).SwitchTo(target, speed);
    }

    /// <summary>
    /// Immediately ends the handle's "in transition" state after a FollowTarget/SwitchTo, so the
    /// toolkit's continuous position-follow (PantoHandle.FixedUpdate) engages right away. SwitchTo
    /// marks the handle inTransition and, on hardware, only clears it on a confirmed arrival report
    /// or a ~3s "couldn't be reached" timeout - and while inTransition the continuous follow is
    /// SUPPRESSED, so the handle sits still after its one initial command until the transition ends
    /// (observed as ~5s of a dead handle at game start, since arrival is never reported for the
    /// it-handle's first target here). Safe for our persistent-follow use: we don't need real
    /// arrival - the continuous follow drives the handle to wherever the target transform currently
    /// is every frame. Uses the toolkit's own public TweeningEnded (what a real arrival would call).
    /// </summary>
    public void MarkFollowReady(bool isUpper) => GetHandle(isUpper).TweeningEnded();

    /// <summary>
    /// Re-sends the handle's movement speed to the firmware. The toolkit only ever sets speed once,
    /// inside SwitchTo - and since we call SwitchTo exactly once per session (persistent-target
    /// design), that single SetSpeed is a one-shot: if its packet races the firmware's motor-task
    /// readiness and gets dropped, the handle stays stuck on the firmware's slow default speed
    /// (feels like low power / high resistance) for the whole session with nothing to correct it.
    /// PieceHandle calls this exactly once, right after the first SwitchTo/FollowTarget arrival is
    /// confirmed (firmware guaranteed ready by then), to re-assert the speed in case that first
    /// packet was dropped. Deliberately NOT called per fall step: re-sending on every step floods
    /// the position stream with SendSpeed packets and drags the handle to the bottom edge (observed
    /// on hardware). No-op in debug/emulator mode, matching how the toolkit only touches speed when
    /// !debug.
    /// </summary>
    public void SetHandleSpeed(bool isUpper, float speed)
    {
        if (sync == null || sync.debug) return;
        sync.SetSpeed(isUpper, Mathf.Min(speed, MaxMovementSpeed));
    }

    public void FreeHandle(bool isUpper) => GetHandle(isUpper).Free();
    public void FreezeHandle(bool isUpper) => GetHandle(isUpper).Freeze();

    /// <summary>
    /// Moves the handle once to a world position, then frees it again. Awaitable - completes when
    /// the move finishes. Used to place the stack handle at its start cell; the handle must be
    /// FREE afterwards so the firmware renders walls/rails for it (motor commands override the
    /// god-object wall rendering per handle).
    /// </summary>
    public Task MoveHandleTo(bool isUpper, Vector3 position, float speed) =>
        GetHandle(isUpper).MoveToPosition(position, speed, shouldFreeHandle: true);

    /// <summary>
    /// Hands rotation control of the handle back to the player while keeping the game's hold on its
    /// POSITION. Requires the toolkit fix in PantoHandle.FixedUpdate that respects
    /// userControlledRotation on hardware (otherwise the per-frame position re-send re-commands the
    /// target's angle and overrides this). Call once, after the handle is following a target.
    /// </summary>
    public void FreeRotation(bool isUpper) => GetHandle(isUpper).FreeRotation();

    /// <summary>Current rotation of the handle in degrees (Unity Y), for reading player turn input.</summary>
    public float GetHandleRotation(bool isUpper) => GetHandle(isUpper).GetRotation();

    /// <summary>
    /// Commands only the handle's rotation (position left free), via the toolkit's Rotate. Works
    /// without SwitchTo, so it's the clean way to drive a free handle's rotation. No-op in debug.
    /// </summary>
    public void RotateHandle(bool isUpper, float angleDegrees) => GetHandle(isUpper).Rotate(angleDegrees);

    // NOTE: no ApplyForce wrapper. Every Unity-side force field tried on the stack handle
    // (spring/deadzone/hysteresis/corner-pull, and later speed-gated/faded variants) ran away or
    // oscillated on hardware - a ~50Hz position-read/force-send loop has no stable damping here.
    // The stack handle now uses firmware rail geometry only (see StackHandle). Force mode also
    // overrides firmware wall/rail rendering per handle, so the two can't coexist anyway.

    /// <summary>
    /// Registers a box obstacle matching the GameObject's BoxCollider, so the stack handle can feel it.
    /// </summary>
    public PantoBoxCollider CreateBoxObstacle(GameObject target, bool onUpper = true, bool onLower = false)
    {
        PantoBoxCollider obstacle = target.GetComponent<PantoBoxCollider>();
        if (obstacle == null) obstacle = target.AddComponent<PantoBoxCollider>();
        obstacle.onUpper = onUpper;
        obstacle.onLower = onLower;

        void CreateAndEnable()
        {
            obstacle.CreateObstacle();
            obstacle.Enable();
            allObstacles.Add(obstacle);
        }

        // Always queued (never immediate), so creation is staggered one obstacle per frame even
        // for runtime rebuilds - see the flood note in Update.
        pendingObstacles.Add(CreateAndEnable);

        return obstacle;
    }

    /// <summary>
    /// Registers one firmware-rendered haptic grid line between two world positions (XZ plane) -
    /// a Rail barrier by default (see GridLineCollider.Kind). Creates its own GameObject (parented
    /// under this system). Goes through the staggered pending queue like every other obstacle.
    /// Used by StackHandle for the cell-boundary grid.
    /// </summary>
    public GridLineCollider CreateGridLine(Vector3 a, Vector3 b, GridLineCollider.Kind kind,
        float railDisplacement, bool onUpper = true, bool onLower = false)
    {
        GameObject holder = new GameObject($"GridLine ({a.x:F1},{a.z:F1})-({b.x:F1},{b.z:F1})");
        holder.transform.parent = transform;
        GridLineCollider obstacle = holder.AddComponent<GridLineCollider>();
        obstacle.onUpper = onUpper;
        obstacle.onLower = onLower;
        obstacle.kind = kind;
        obstacle.railDisplacement = railDisplacement;
        obstacle.start = new Vector2(a.x, a.z);
        obstacle.end = new Vector2(b.x, b.z);

        void CreateAndEnable()
        {
            if (obstacle == null) return; // destroyed before its turn (e.g. runtime rebuild)
            obstacle.CreateObstacle();
            obstacle.Enable();
            allObstacles.Add(obstacle);
        }
        pendingObstacles.Add(CreateAndEnable);

        return obstacle;
    }

    /// <summary>
    /// Removes an obstacle from the device - QUEUED (one serial op per frame), so tearing down
    /// many obstacles at once (runtime rebuild) can't flood the device either. The id/handle-index
    /// are captured immediately, so the caller may Destroy the GameObject right away. Obstacles
    /// that were never actually created on the device (id 0 - e.g. still pending, or debug mode)
    /// are skipped.
    /// </summary>
    public void RemoveObstacle(PantoCollider obstacle)
    {
        if (obstacle == null) return;
        allObstacles.Remove(obstacle);

        ushort id = obstacle.GetId();
        if (id == 0) return;
        // Mirror of PantoCollider.getPantoIndex (protected there): upper-only = 0, lower-only = 1,
        // both = 0xff.
        byte index = obstacle.onUpper && obstacle.onLower ? (byte)0xff : obstacle.onUpper ? (byte)0 : (byte)1;
        pendingObstacles.Add(() => sync.RemoveObstacle(index, id));
    }
}
