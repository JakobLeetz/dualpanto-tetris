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
    float readyAt;
    readonly List<Action> pendingObstacles = new List<Action>();
    readonly List<PantoBoxCollider> allObstacles = new List<PantoBoxCollider>();

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

    public void FreeHandle(bool isUpper) => GetHandle(isUpper).Free();
    public void FreezeHandle(bool isUpper) => GetHandle(isUpper).Freeze();

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
