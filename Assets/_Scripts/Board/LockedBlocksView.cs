using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps a visual blockPrefab instance per locked grid cell in sync with GridManager. Locked cells
/// are NOT registered as hard Panto obstacles - the stack handle feels them via the magnetic-grid
/// pulsing force instead (see StackHandle), which reads locked state straight from GridManager, so
/// these blocks are purely visual. Their colliders are made triggers so they never hard-block the
/// handle's raycast in the emulator either. Rebuilds from scratch after a line clear instead of
/// shifting individual blocks - simpler and correct, cheap enough at grid sizes up to ~200 cells.
/// </summary>
public class LockedBlocksView : MonoBehaviour
{
    [SerializeField] GridManager gridManager;
    [SerializeField] Transform container;
    [SerializeField] GameObject blockPrefab;

    readonly Dictionary<Vector2Int, GameObject> blocks = new Dictionary<Vector2Int, GameObject>();

    void OnEnable()
    {
        gridManager.OnPieceLocked += HandlePieceLocked;
        gridManager.OnLinesCleared += HandleLinesCleared;
    }

    void OnDisable()
    {
        gridManager.OnPieceLocked -= HandlePieceLocked;
        gridManager.OnLinesCleared -= HandleLinesCleared;
    }

    void HandlePieceLocked(List<Vector2Int> cells)
    {
        foreach (Vector2Int cell in cells)
        {
            SpawnBlock(cell);
        }
    }

    void HandleLinesCleared(List<int> rows)
    {
        foreach (GameObject block in blocks.Values)
        {
            Destroy(block);
        }
        blocks.Clear();

        foreach (Vector2Int cell in gridManager.GetLockedCells())
        {
            SpawnBlock(cell);
        }
    }

    void SpawnBlock(Vector2Int cell)
    {
        GameObject block = Instantiate(blockPrefab, container);
        block.name = $"LockedBlock_{cell.x}_{cell.y}";
        block.transform.position = gridManager.GridToWorld(cell);
        block.transform.localScale = Vector3.one * gridManager.CellSize;
        block.GetComponent<GridBlock>().GridPosition = cell;
        // Purely visual: not a Panto obstacle, and its collider is a trigger so it never hard-blocks
        // the stack handle's raycast in the emulator (the handle feels locked cells via StackHandle's
        // pulsing force instead, read straight from GridManager).
        Collider col = block.GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
        blocks[cell] = block;
    }
}
