using System.Collections.Generic;
using DualPantoToolkit;
using UnityEngine;

/// <summary>
/// Keeps an instance of blockPrefab per locked grid cell in sync with GridManager, registering
/// each as a box obstacle so the stack handle can feel the stack. Rebuilds from scratch after a
/// line clear instead of shifting individual blocks - simpler and correct, cheap enough at grid
/// sizes up to ~200 cells.
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
            // Destroying the GameObject alone doesn't unregister the obstacle from the Panto
            // engine - without this it keeps giving force feedback at the old position forever.
            PantoSystem.Instance.RemoveObstacle(block.GetComponent<PantoBoxCollider>());
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
        PantoSystem.Instance.CreateBoxObstacle(block, onUpper: true, onLower: false);
        blocks[cell] = block;
    }
}
