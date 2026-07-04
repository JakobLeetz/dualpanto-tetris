using UnityEngine;

/// <summary>
/// Alternative to foot-pedal/arrow-key input: push the it-handle to move or rotate the piece.
/// Sideways push (world X, matches grid columns) moves the piece left/right. Push along the
/// other horizontal axis (world Z, matches grid rows/fall direction) rotates the piece relative
/// to its current orientation - same as pressing the up-arrow, just triggered by a physical
/// push instead.
/// Only reads pushes as input while PieceHandle.IsSettled is true (i.e. the handle is resting
/// at the last traced waypoint from the last fall step, not actively mid-trace) - during a trace
/// the handle normally lags behind its own moving target for reasons unrelated to the player
/// pushing it, which would otherwise look identical to a nudge.
/// Neither a shift nor a rotation moves/retraces the handle immediately (see
/// PieceHandle.HandlePieceShifted/HandlePieceRotated) - both are only felt once the piece
/// actually falls and the next full retrace picks them up. Since that means IsSettled itself
/// never goes false from a shift/rotation, a sustained push would otherwise keep firing
/// TryMove/TryRotate every single frame it stays past threshold. PieceHandle.CanShiftAgain/
/// CanRotateAgain gate that instead: each goes false the instant its action fires and only
/// becomes true again once the piece actually falls, so one held push yields exactly one
/// shift/rotation per fall step.
/// </summary>
public class PieceNudgeInput : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] PieceHandle pieceHandle;

    // Fractions of a cell's world size, so these scale automatically if the frame gets resized.
    // Separate values because rotation felt too sensitive at the same threshold as movement.
    [SerializeField] float moveTriggerFraction = 0.3f;
    [SerializeField] float rotateTriggerFraction = 0.6f;

    void Update()
    {
        if (!pieceHandle.IsSettled) return;

        Vector3 target = pieceHandle.transform.position;
        Vector3 real = PantoSystem.Instance.GetHandlePosition(isUpper: false, target);
        float cellSize = gridManager.CellSize;

        float sideways = real.x - target.x;
        float moveThreshold = moveTriggerFraction * cellSize;
        if (pieceHandle.CanShiftAgain)
        {
            if (sideways >= moveThreshold) gridManager.TryMove(Vector2Int.right);
            else if (sideways <= -moveThreshold) gridManager.TryMove(Vector2Int.left);
        }

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
