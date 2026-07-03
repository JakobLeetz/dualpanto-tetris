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
    UpperHandle upperHandle; // "Me"/stack handle, moved by the player
    LowerHandle lowerHandle; // "It"/piece handle, actuated by the device

    protected override void Awake()
    {
        base.Awake();
        GameObject panto = GameObject.Find("Panto");
        if (panto == null)
        {
            Debug.LogError("[PantoSystem] No GameObject named 'Panto' found in scene.");
            return;
        }
        upperHandle = panto.GetComponent<UpperHandle>();
        lowerHandle = panto.GetComponent<LowerHandle>();
    }

    PantoHandle GetHandle(bool isUpper) => isUpper ? (PantoHandle)upperHandle : lowerHandle;

    public Task MoveHandleTo(bool isUpper, Vector3 worldPosition, float speed, bool shouldFreeHandle = false)
    {
        return GetHandle(isUpper).MoveToPosition(worldPosition, speed, shouldFreeHandle);
    }

    public async Task TraceWithHandle(bool isUpper, IReadOnlyList<Vector3> points, float speed)
    {
        PantoHandle handle = GetHandle(isUpper);
        foreach (Vector3 point in points)
        {
            await handle.MoveToPosition(point, speed, shouldFreeHandle: false);
        }
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
        obstacle.CreateObstacle();
        obstacle.Enable();
        return obstacle;
    }

    public void RemoveObstacle(PantoBoxCollider obstacle)
    {
        if (obstacle != null) obstacle.Remove();
    }
}
