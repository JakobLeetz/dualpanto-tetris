using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// One firmware-rendered haptic line for the stack-handle grid (see StackHandle). Project-side
/// PantoCollider subclass supporting three primitives, switchable via Kind:
///  - Rail (the used mode): acts as a soft BARRIER - the handle moves freely between rails and
///    feels resistance when crossing one (hardware-verified). This is the working "wall you can
///    push through" (toolkit-proven CreateRail path).
///  - Passable: CreatePassableObstacle - kept for reference; hardware testing showed the firmware
///    silently accepts and never renders these.
///  - Hard: a solid wall via CreateObstacle - DIAGNOSTIC ONLY: a full lattice of these enclosing
///    the handle crashed the firmware.
/// start/end are Unity world-space XZ (the sync layer converts to device coordinates). Set
/// onUpper/onLower/kind before CreateObstacle().
/// </summary>
public class GridLineCollider : PantoCollider
{
    public enum Kind { Rail, Passable, Hard }

    public Vector2 start;
    public Vector2 end;
    public Kind kind = Kind.Rail;
    // Rail only: how far the handle can deviate from the line before the guiding force acts /
    // how hard it is to push across (Unity units).
    public float railDisplacement = 0.3f;

    public override void CreateObstacle()
    {
        UpdateId();
        byte index = getPantoIndex();
        if (index == 2)
        {
            Debug.LogWarning("[GridLineCollider] Skipping creation for object with no handles");
            return;
        }
        switch (kind)
        {
            case Kind.Rail:
                CreateRailForLine(start, end, railDisplacement);
                break;
            case Kind.Passable:
                GetPantoSync().CreatePassableObstacle(index, GetId(), start, end);
                break;
            case Kind.Hard:
                GetPantoSync().CreateObstacle(index, GetId(), start, end);
                break;
        }
        DrawLine(start, end);
    }

    // Same visual the toolkit draws for its obstacles (PantoCollider.DrawLine is private, so
    // recreated here): a thin LineRenderer child on the Walls2 layer. Purely visual - no collider,
    // so it never blocks the emulator's handle raycast.
    void DrawLine(Vector2 lineStart, Vector2 lineEnd)
    {
        GameObject n = new GameObject("GridLineVisual");
        n.transform.parent = transform;
        n.layer = LayerMask.NameToLayer("Walls2");
        LineRenderer lr = n.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, new Vector3(lineStart.x, 5, lineStart.y));
        lr.SetPosition(1, new Vector3(lineEnd.x, 5, lineEnd.y));
        lr.startWidth = 0.02f * GetPantoSync().gameObject.transform.localScale.magnitude;
        lr.material = Resources.Load("Materials/Colliders") as Material;
    }
}
