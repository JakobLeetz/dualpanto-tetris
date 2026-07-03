using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visual reference for the falling piece's position. Drives the piece handle (it-handle)
/// to the piece's lowest cell whenever GridManager reports a move.
/// </summary>
public class PieceCursor : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] float handleSpeed = 15f;

    void OnEnable() => gridManager.OnPieceMoved += HandlePieceMoved;
    void OnDisable() => gridManager.OnPieceMoved -= HandlePieceMoved;

    // Only follows the lowest cell, not the piece's full outline: tracing all cells would need
    // several sequential MoveToPosition calls that overlap once the piece moves again mid-trace.
    // Revisit once Level 2+ needs the player to feel the whole shape.
    void HandlePieceMoved(List<Vector2Int> cells)
    {
        if (cells.Count == 0) return;
        Vector2Int leadCell = cells[0];
        foreach (Vector2Int cell in cells)
        {
            if (cell.y < leadCell.y) leadCell = cell;
        }
        transform.position = gridManager.GridToWorld(leadCell);
        _ = PantoSystem.Instance.MoveHandleTo(isUpper: false, transform.position, handleSpeed);
    }
}
