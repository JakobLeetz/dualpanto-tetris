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
    readonly List<PantoBoxCollider> allObstacles = new List<PantoBoxCollider>();

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

    void Update()
    {
        if (pendingObstacles.Count > 0 && IsReady)
        {
            foreach (Action createAndEnable in pendingObstacles) createAndEnable();
            pendingObstacles.Clear();
        }

        // E/D toggle all obstacles on/off at runtime, matching the toolkit's own Obstacle
        // Manager convention - reimplemented here since Obstacle Manager itself can't be used
        // (see GameManager/LockedBlocksView for why).
        if (Input.GetKeyDown(KeyCode.E)) SetAllObstaclesEnabled(true);
        else if (Input.GetKeyDown(KeyCode.D)) SetAllObstaclesEnabled(false);
    }

    void SetAllObstaclesEnabled(bool enable)
    {
        foreach (PantoBoxCollider obstacle in allObstacles)
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
    /// Applies a continuous force to the handle (toolkit force mode). direction is normalized and
    /// strength clamped to [0,1] by the toolkit, so passing a force vector as direction with its
    /// magnitude as strength just caps the force at unit length. No-op in debug/emulator mode
    /// (SendMotor only fires when !debug). Call every FixedUpdate while a force should be felt.
    /// </summary>
    public void ApplyForce(bool isUpper, Vector3 direction, float strength) =>
        GetHandle(isUpper).ApplyForce(direction, strength);

    public void StopApplyingForce(bool isUpper) => GetHandle(isUpper).StopApplyingForce();

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

        if (!IsReady) pendingObstacles.Add(CreateAndEnable);
        else CreateAndEnable();

        return obstacle;
    }

    public void RemoveObstacle(PantoBoxCollider obstacle)
    {
        if (obstacle == null) return;
        obstacle.Remove();
        allObstacles.Remove(obstacle);
    }
}
