using UnityEngine;

/// <summary>
/// Rotation input: push the it-handle along the world-Z axis (matches grid rows / the fall
/// direction) to rotate the piece relative to its current orientation - same as pressing the
/// up-arrow, just triggered by a physical push. (Left/right movement used to live here too but is
/// now handled by two foot pedals emitting keycodes - see GameManager.)
/// Only reads pushes as input while PieceHandle.IsSettled is true (i.e. the handle is resting at
/// the last traced waypoint from the last fall step, not actively mid-trace) - during a trace the
/// handle normally lags behind its own moving target for reasons unrelated to the player pushing
/// it, which would otherwise look identical to a nudge.
/// A rotation doesn't retrace the handle immediately (see PieceHandle.HandlePieceRotated) - it's
/// only felt once the piece actually falls and the next full retrace picks it up. Since that means
/// IsSettled never goes false from a rotation, a sustained push would otherwise keep firing
/// TryRotate every frame it stays past threshold. PieceHandle.CanRotateAgain gates that: it goes
/// false the instant a rotation fires and only becomes true again once the piece actually falls,
/// so one held push yields exactly one rotation per fall step.
/// </summary>
public class PieceNudgeInput : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] PieceHandle pieceHandle;

    // Fraction of a cell's world size, so it scales automatically if the frame gets resized.
    [SerializeField] float rotateTriggerFraction = 0.6f;

    void Update()
    {
        if (!pieceHandle.IsSettled) return;

        Vector3 target = pieceHandle.transform.position;
        Vector3 real = PantoSystem.Instance.GetHandlePosition(isUpper: false, target);
        float cellSize = gridManager.CellSize;

        // Sign/axis here is a guess at what "push up" feels like on the physical device - flip
        // the sign (or swap to a different axis) if it turns out backwards or on the wrong axis.
        float forward = real.z - target.z;
        float rotateThreshold = rotateTriggerFraction * cellSize;
        if (pieceHandle.CanRotateAgain)
        {
            if (forward >= rotateThreshold) gridManager.TryRotate(1);
            else if (forward <= -rotateThreshold) gridManager.TryRotate(-1);
        }
    }
}
