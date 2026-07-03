using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Grid state, collision, fall timer and line clearing. Not a singleton - held via
/// [SerializeField] reference by GameManager, since a game could in theory have more than one board.
/// </summary>
public class GridManager : MonoBehaviour
{
    public enum CellState { Empty, Locked }

    static readonly Dictionary<PieceType, Vector2Int[]> Shapes = new Dictionary<PieceType, Vector2Int[]>
    {
        { PieceType.I, new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(2, 0), new Vector2Int(3, 0) } },
    };

    [SerializeField] float cellSize = 1f;
    [SerializeField] float fallInterval = 1f;

    CellState[,] cells;
    List<Vector2Int> pieceShape;
    Vector2Int pieceOrigin;
    bool hasPiece;
    bool falling;
    float fallTimer;

    public event Action<List<Vector2Int>> OnPieceMoved;
    public event Action<List<Vector2Int>> OnPieceLocked;
    public event Action<List<int>> OnLinesCleared;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float CellSize => cellSize;

    public void Initialize(int gridWidth, int gridHeight)
    {
        Width = gridWidth;
        Height = gridHeight;
        cells = new CellState[Width, Height];
        hasPiece = false;
        falling = false;
    }

    public void SpawnPiece(PieceType type)
    {
        pieceShape = Shapes[type].ToList();
        int shapeWidth = pieceShape.Max(offset => offset.x) + 1;
        pieceOrigin = new Vector2Int((Width - shapeWidth) / 2, Height - 1);
        hasPiece = true;
        falling = true;
        fallTimer = 0f;
        OnPieceMoved?.Invoke(GetPieceCells());
    }

    void Update()
    {
        if (!falling || !hasPiece) return;
        fallTimer += Time.deltaTime;
        if (fallTimer >= fallInterval)
        {
            fallTimer = 0f;
            Tick();
        }
    }

    public void Tick()
    {
        if (!hasPiece) return;
        if (!TryMove(Vector2Int.down))
        {
            LockPiece();
        }
    }

    public bool TryMove(Vector2Int direction)
    {
        if (!hasPiece) return false;
        Vector2Int newOrigin = pieceOrigin + direction;
        if (!IsValidPosition(newOrigin)) return false;
        pieceOrigin = newOrigin;
        OnPieceMoved?.Invoke(GetPieceCells());
        return true;
    }

    bool IsValidPosition(Vector2Int origin)
    {
        foreach (Vector2Int offset in pieceShape)
        {
            Vector2Int cell = origin + offset;
            if (cell.x < 0 || cell.x >= Width || cell.y < 0 || cell.y >= Height) return false;
            if (cells[cell.x, cell.y] == CellState.Locked) return false;
        }
        return true;
    }

    void LockPiece()
    {
        falling = false;
        hasPiece = false;
        List<Vector2Int> lockedCells = GetPieceCells();
        foreach (Vector2Int cell in lockedCells)
        {
            cells[cell.x, cell.y] = CellState.Locked;
        }
        OnPieceLocked?.Invoke(lockedCells);
        ClearFullLines();
    }

    void ClearFullLines()
    {
        List<int> clearedRows = new List<int>();
        for (int y = 0; y < Height; y++)
        {
            bool full = true;
            for (int x = 0; x < Width; x++)
            {
                if (cells[x, y] != CellState.Locked) { full = false; break; }
            }
            if (full) clearedRows.Add(y);
        }
        if (clearedRows.Count == 0) return;

        foreach (int row in clearedRows)
        {
            for (int y = row; y < Height - 1; y++)
                for (int x = 0; x < Width; x++)
                    cells[x, y] = cells[x, y + 1];
            for (int x = 0; x < Width; x++)
                cells[x, Height - 1] = CellState.Empty;
        }
        OnLinesCleared?.Invoke(clearedRows);
    }

    public List<Vector2Int> GetPieceCells()
    {
        List<Vector2Int> result = new List<Vector2Int>();
        if (!hasPiece) return result;
        foreach (Vector2Int offset in pieceShape)
            result.Add(pieceOrigin + offset);
        return result;
    }

    public Vector3 GridToWorld(Vector2Int cell)
    {
        return transform.position + new Vector3(cell.x * cellSize, 0f, cell.y * cellSize);
    }
}
