using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Stack handle (Me/Upper): drives the "magnetic grid" feel via force (the handle is free, so
/// ApplyForce works). The handle is guided toward the current cell centre (same grid as the
/// pieces) and can be pushed into a neighbour cell; while over a locked (stacked) cell an extra
/// pulsing force is added so the stack is felt as a vibration. Never a hard block (the field frame
/// stays a hard obstacle, registered separately in GameManager).
///
/// Stability / feel refinements (all WITHOUT any Unity-side velocity term, which amplified noise
/// and made things worse):
///  - a flat centre deadzone with NO force, so a let-go handle coasts to rest via the mechanism's
///    own friction instead of being sprung around the centre. Sized (>= 0.5 - hysteresis) so that
///    right after a cell switch the handle is already inside the new cell's deadzone - otherwise
///    the switch puts it ~half a cell from the new centre where the pull is strongest and yanks it
///    across, overshooting;
///  - the pull ramps from 0 at the deadzone edge up to pullStrength at the cell boundary, a gentle
///    edge-detent rather than a strong central spring;
///  - hysteresis + cardinal-only stepping on which cell is the target: it sticks until the handle
///    is pushed clearly past a boundary, then steps by ONE cell along whichever axis moved furthest
///    - never diagonally to a corner cell in one go, so diagonal drift is resisted and the force
///    never flips while resting on a boundary.
///
/// All force is hardware-only: PantoSystem.ApplyForce is a no-op in debug/emulator mode, where the
/// handle is mouse-driven and this component only tracks position (+ optional cell logging).
/// Values are starting guesses to tune in the Inspector on real hardware.
/// </summary>
public class StackHandle : MonoBehaviour
{
    [SerializeField] GridManager gridManager;

    [Tooltip("Max pull, as a [0,1] force (clamped to unit length). Only acts in the ring between " +
        "centerDeadzone and the cell boundary, so keep it modest.")]
    [SerializeField] float pullStrength = 0.2f;
    [Tooltip("Fraction of a cell (from the centre) that is force-free. MUST be >= 0.5 - " +
        "cellHysteresis so a cell switch doesn't yank the handle across the cell (overshoot).")]
    [SerializeField] float centerDeadzone = 0.4f;
    [Tooltip("Extra fraction of a cell past the midpoint the handle must be pushed before the " +
        "target cell switches, so the force doesn't flip on a boundary.")]
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
    [Tooltip("Amplitude of the pulsing force added while over a locked cell (only in the active " +
        "ring, not inside the deadzone).")]
    [SerializeField] float buzzStrength = 0.15f;
    [Tooltip("Pulses per second of the locked-cell vibration.")]
    [SerializeField] float buzzFrequency = 8f;
    [Tooltip("Speed used to move the handle to the top-left cell at game start.")]
    [SerializeField] float moveToStartSpeed = 15f;
    [SerializeField] bool debugLogging = false;

    Vector2Int currentCell;
    bool hasCurrent;
    Vector2Int lastLoggedCell = new Vector2Int(int.MinValue, int.MinValue);
    bool positioning;

    void FixedUpdate()
    {
        // While actively driving the handle to the start corner it's attached (SwitchTo), so we
        // must NOT ApplyForce (that only works on a free handle).
        if (positioning) return;

        Vector3 real = PantoSystem.Instance.GetHandlePosition(true, transform.position);
        transform.position = real;

        if (gridManager == null || gridManager.CellSize <= 0f) return;
        float cellSize = gridManager.CellSize;

        Vector2Int nearest = gridManager.WorldToGrid(real);

        // Hysteresis + cardinal-only stepping: stick with the current target cell until the handle
        // is pushed clearly past a boundary, then step it by ONE cell along whichever axis it moved
        // furthest on - never diagonally to a corner cell in one go.
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
                currentCell = new Vector2Int(
                    Mathf.Clamp(currentCell.x + step.x, 0, gridManager.Width - 1),
                    Mathf.Clamp(currentCell.y + step.y, 0, gridManager.Height - 1));
            }
        }

        bool locked = gridManager.IsLocked(currentCell);
        if (debugLogging && currentCell != lastLoggedCell)
        {
            Debug.Log($"[StackHandle] cell={currentCell} locked={locked}");
            lastLoggedCell = currentCell;
        }

        Vector3 toCenter = gridManager.GridToWorld(currentCell) - real;
        toCenter.y = 0f;
        float distance = toCenter.magnitude;

        Vector3 force = Vector3.zero;
        if (distance > 1e-4f)
        {
            Vector3 dir = toCenter / distance;

            // Normal edge detent: 0 inside the deadzone, ramping to pullStrength at the boundary.
            if (distance > cellSize * centerDeadzone)
            {
                float t = Mathf.Clamp01(Mathf.InverseLerp(cellSize * centerDeadzone, cellSize * 0.5f, distance));
                force += dir * (t * pullStrength);
            }

            if (locked) force += dir * Pulse();
        }

        // Corner pull: an extra, independently-tuned pull that grows as the handle nears a corner,
        // to fight diagonal crossings. It targets the PHYSICALLY NEAREST cell centre (not the
        // hysteretic currentCell): at a shared corner currentCell often lags to the lower/other
        // cell, whose centre is behind the handle, so pulling toward it dragged the handle further
        // INTO the corner instead of out. Toward the nearest centre it always points out of the
        // corner the handle is actually in. "Corner-ness" is the SMALLER of the two axis offsets,
        // so it only fires near corners (both axes far from centre), not near edge midpoints.
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
    }

    float Pulse() => Mathf.Sin(Time.time * buzzFrequency * 2f * Mathf.PI) * buzzStrength;

    /// <summary>
    /// Drives the handle to the top-left cell of the field, then frees it (so the force field
    /// resumes). Called by GameManager on game start/restart, since the free handle otherwise starts
    /// at the device's default position, often outside the field. No-op in debug/emulator.
    /// </summary>
    public async Task MoveToStartCorner()
    {
        if (gridManager == null || gridManager.CellSize <= 0f) return;
        Vector3 corner = gridManager.GridToWorld(new Vector2Int(0, gridManager.Height - 1));
        positioning = true;
        await PantoSystem.Instance.MoveHandleTo(isUpper: true, corner, moveToStartSpeed);
        positioning = false;
    }

    void OnDisable()
    {
        if (PantoSystem.Instance != null) PantoSystem.Instance.StopApplyingForce(true);
    }
}
