using UnityEngine;

/// <summary>
/// Represents the stack handle (Me/Upper) in the scene, following the toolkit's "Me Handle"
/// pattern (see toolkit README - attach a handle-tracking component to a GameObject). Needs a
/// trigger SphereCollider + kinematic Rigidbody + tag "MeHandle" set up in the scene so Unity's
/// own trigger events can fire against locked blocks/walls later.
/// </summary>
public class StackHandle : MonoBehaviour
{
    void FixedUpdate()
    {
        transform.position = PantoSystem.Instance.GetHandlePosition(true, transform.position);
    }
}
