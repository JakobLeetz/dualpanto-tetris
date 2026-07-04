using UnityEngine;

/// <summary>
/// Marks a GameObject as occupying a grid cell. Attached to blockPrefab, shared by both
/// LockedBlocksView (permanent stack blocks) and FallingPieceView (visual-only active piece).
/// </summary>
public class GridBlock : MonoBehaviour
{
    public Vector2Int GridPosition { get; set; }
}
