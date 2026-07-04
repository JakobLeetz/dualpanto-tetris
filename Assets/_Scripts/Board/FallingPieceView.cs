using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visual representation of the currently falling piece: one blockPrefab instance per cell
/// (every tetromino always has exactly 4). Purely visual - never registered as a Panto obstacle,
/// even though blockPrefab carries a PantoBoxCollider (it does nothing unless CreateObstacle()/
/// Enable() are called, which only LockedBlocksView does once the piece locks).
/// </summary>
public class FallingPieceView : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] Transform container;
    [SerializeField] GameObject blockPrefab;

    readonly List<GameObject> blocks = new List<GameObject>();

    void OnEnable()
    {
        gridManager.OnPieceSpawned += HandlePieceSpawned;
        gridManager.OnPieceMoved += HandlePieceMoved;
        gridManager.OnPieceRotated += HandlePieceMoved;
        gridManager.OnPieceShifted += HandlePieceShifted;
        gridManager.OnPieceLocked += HandlePieceLocked;
    }

    void OnDisable()
    {
        gridManager.OnPieceSpawned -= HandlePieceSpawned;
        gridManager.OnPieceMoved -= HandlePieceMoved;
        gridManager.OnPieceRotated -= HandlePieceMoved;
        gridManager.OnPieceShifted -= HandlePieceShifted;
        gridManager.OnPieceLocked -= HandlePieceLocked;
    }

    void HandlePieceSpawned(List<Vector2Int> cells)
    {
        ClearBlocks();
        foreach (Vector2Int cell in cells)
        {
            GameObject block = Instantiate(blockPrefab, container);
            block.transform.position = gridManager.GridToWorld(cell);
            block.transform.localScale = Vector3.one * gridManager.CellSize;
            blocks.Add(block);
        }
    }

    void HandlePieceMoved(List<Vector2Int> cells)
    {
        for (int i = 0; i < blocks.Count && i < cells.Count; i++)
        {
            blocks[i].transform.position = gridManager.GridToWorld(cells[i]);
        }
    }

    // OnPieceShifted only carries the direction, not cells (see GridManager) - the visual still
    // needs to reposition immediately regardless of how the it-handle reacts to the same event.
    void HandlePieceShifted(Vector2Int direction)
    {
        HandlePieceMoved(gridManager.GetPieceCells());
    }

    void HandlePieceLocked(List<Vector2Int> cells)
    {
        ClearBlocks();
    }

    void ClearBlocks()
    {
        foreach (GameObject block in blocks)
        {
            Destroy(block);
        }
        blocks.Clear();
    }
}
