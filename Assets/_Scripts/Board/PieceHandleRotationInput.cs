using UnityEngine;

/// <summary>
/// PARKED - does nothing on purpose. Handle-driven rotation input was tried several ways and none
/// worked out: it-handle detents/timer oscillated (its rotation servo can't hold under SwitchTo),
/// and driving rotation on the Me handle fought StackHandle's stack-exploration force field on the
/// same handle - which left the Me handle with no usable function. To avoid that conflict this
/// component intentionally sends nothing, so the Me handle stays fully owned by StackHandle.
/// Rotation input currently falls back to the keyboard (GameManager UpArrow). The GameObject can be
/// deleted; the empty class is kept only so the scene component reference doesn't break.
/// </summary>
public class PieceHandleRotationInput : MonoBehaviour
{
}
