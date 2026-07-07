using UnityEngine;

/// <summary>
/// Stack handle (Me/Upper): drives the "magnetic grid" feel. The handle snaps softly to the grid
/// cell centres (same grid as the pieces) - each cell pulls the handle toward its centre, but the
/// player can push past a cell boundary with enough force, at which point WorldToGrid reports the
/// neighbour cell and the pull flips toward its centre (a detent, never a hard block). While the
/// handle is over a locked (stacked) cell, an extra pulsing force is added so the stack is felt as
/// a vibration - purely tactile, movement is never blocked (the field frame stays a hard obstacle,
/// registered separately in GameManager).
/// All force is hardware-only: PantoSystem.ApplyForce is a no-op in debug/emulator mode, where the
/// handle is mouse-driven and this component only tracks position (+ optional cell logging).
/// Force/frequency values are starting guesses to tune in the Inspector on real hardware.
/// </summary>
public class StackHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;

    [Tooltip("Max pull toward the current cell centre, as a [0,1] force (clamped to unit length).")]
    [SerializeField] float pullStrength = 0.3f;
    [Tooltip("Amplitude of the pulsing force added while over a locked cell.")]
    [SerializeField] float buzzStrength = 0.15f;
    [Tooltip("Pulses per second of the locked-cell vibration.")]
    [SerializeField] float buzzFrequency = 8f;
    [SerializeField] bool debugLogging = false;

    Vector2Int lastCell = new Vector2Int(int.MinValue, int.MinValue);

    void FixedUpdate()
    {
        Vector3 real = PantoSystem.Instance.GetHandlePosition(true, transform.position);
        transform.position = real;

        if (gridManager == null || gridManager.CellSize <= 0f) return;

        Vector2Int cell = gridManager.WorldToGrid(real);
        bool locked = gridManager.IsLocked(cell);

        if (debugLogging && cell != lastCell)
        {
            Debug.Log($"[StackHandle] cell={cell} locked={locked}");
            lastCell = cell;
        }

        Vector3 toCenter = gridManager.GridToWorld(cell) - real;
        toCenter.y = 0f;
        float distance = toCenter.magnitude;

        Vector3 force = Vector3.zero;
        if (distance > 1e-4f)
        {
            Vector3 dir = toCenter / distance;
            force = dir * (Mathf.Min(distance / (gridManager.CellSize * 0.5f), 1f) * pullStrength);
            if (locked) force += dir * Pulse();
        }
        else if (locked)
        {
            // Exactly at the cell centre: no pull direction, so buzz along a fixed axis instead so
            // the vibration is still felt.
            force = Vector3.right * Pulse();
        }

        PantoSystem.Instance.ApplyForce(true, force, force.magnitude);
    }

    float Pulse() => Mathf.Sin(Time.time * buzzFrequency * 2f * Mathf.PI) * buzzStrength;

    void OnDisable()
    {
        if (PantoSystem.Instance != null) PantoSystem.Instance.StopApplyingForce(true);
    }
}
