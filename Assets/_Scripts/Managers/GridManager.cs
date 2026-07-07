using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Grid state, collision, rotation, fall timer and line clearing. Not a singleton - held via
/// [SerializeField] reference by GameManager, since a game could in theory have more than one board.
/// Knows nothing about scoring/levels - GameManager translates events into score and pushes the
/// resulting fall speed back in via SetFallFramesPerRow.
/// </summary>
public class GridManager : MonoBehaviour
{
    public enum CellState { Empty, Locked }

    // Wall-kick offsets tried in order when a rotation doesn't fit in its target cell.
    static readonly int[] RotationKicks = { 0, -1, 1, -2, 2 };

    // 4 rotation states per piece, standard tetromino cell offsets (x right, y up). Cells within
    // each state are ordered so consecutive cells are grid-adjacent wherever the shape's topology
    // allows it, and the x coordinate never decreases along the path (traces left to right) -
    // PieceHandle traces this order. T is the only piece where a single jump is unavoidable (its
    // center cell touches all 3 others, but a path can only pass through it once); its jump is
    // placed between two cells that share the same x, so left-to-right order still holds overall.
    static readonly Dictionary<PieceType, Vector2Int[][]> Shapes = new Dictionary<PieceType, Vector2Int[][]>
    {
        {
            PieceType.I, new[]
            {
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1), new Vector2Int(3, 1) },
                new[] { new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(1, 3) },
                new[] { new Vector2Int(0, 2), new Vector2Int(1, 2), new Vector2Int(2, 2), new Vector2Int(3, 2) },
                new[] { new Vector2Int(2, 0), new Vector2Int(2, 1), new Vector2Int(2, 2), new Vector2Int(2, 3) },
            }
        },
        {
            PieceType.O, new[]
            {
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0) },
            }
        },
        {
            PieceType.T, new[]
            {
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(2, 1) },
                new[] { new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(2, 1) },
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(2, 1) },
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(1, 2) },
            }
        },
        {
            PieceType.S, new[]
            {
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(2, 2) },
                new[] { new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(2, 1), new Vector2Int(2, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(2, 1) },
                new[] { new Vector2Int(0, 2), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0) },
            }
        },
        {
            PieceType.Z, new[]
            {
                new[] { new Vector2Int(0, 2), new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(2, 1) },
                new[] { new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(2, 1), new Vector2Int(2, 2) },
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(2, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 2) },
            }
        },
        {
            PieceType.J, new[]
            {
                new[] { new Vector2Int(0, 2), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1) },
                new[] { new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(2, 2) },
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1), new Vector2Int(2, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2) },
            }
        },
        {
            PieceType.L, new[]
            {
                new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1), new Vector2Int(2, 2) },
                new[] { new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(2, 0) },
                new[] { new Vector2Int(0, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1) },
                new[] { new Vector2Int(0, 2), new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(1, 0) },
            }
        },
    };

    // T is the only piece whose 4 occupied cells can't form a jump-free path (its center cell
    // touches all 3 others, but a path can only pass through it once). For tracing purposes only
    // (not collision - GetPieceCells stays the real 4 distinct cells), this walks back through the
    // center between each outer cell instead of jumping diagonally: leaf -> center -> leaf ->
    // center -> leaf, 5 waypoints, every step grid-adjacent.
    static readonly Vector2Int[][] TTraceWaypoints =
    {
        new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(2, 1) },
        new[] { new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2), new Vector2Int(1, 1), new Vector2Int(2, 1) },
        new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(2, 1) },
        new[] { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(1, 2) },
    };

    [SerializeField] BoxCollider frame;

    CellState[,] cells;
    PieceType currentPieceType;
    Vector2Int[] pieceShape;
    Vector2Int pieceOrigin;
    int currentRotation;
    bool hasPiece;
    float fallInterval = 48f / 60f;
    float fallTimer;
    float cellSize;
    Vector3 originWorld;

    public event Action<List<Vector2Int>> OnPieceSpawned;
    // Fires only for an actual gravity/soft-drop step now - this is the sole trigger for
    // PieceHandle's full haptic retrace. Left/right shifts and rotation fire their own events
    // below instead, so the handle only re-traces the piece's shape once it's fallen.
    public event Action<List<Vector2Int>> OnPieceMoved;
    // Fires instead of OnPieceMoved for a pure left/right step - lets other listeners (falling
    // piece visual) react without the handle doing a full contour retrace.
    public event Action<Vector2Int> OnPieceShifted;
    // Fires instead of OnPieceMoved for a rotation - same reasoning as OnPieceShifted above.
    public event Action<List<Vector2Int>> OnPieceRotated;
    public event Action<List<Vector2Int>> OnPieceLocked;
    public event Action<List<int>> OnLinesCleared;
    public event Action OnGameOver;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float CellSize => cellSize;
    public BoxCollider Frame => frame;

    public void Initialize(int gridWidth, int gridHeight)
    {
        Width = gridWidth;
        Height = gridHeight;
        cells = new CellState[Width, Height];
        hasPiece = false;

        Vector3 frameSize = Vector3.Scale(frame.size, frame.transform.lossyScale);
        cellSize = Mathf.Min(frameSize.x / Width, frameSize.z / Height);

        Vector3 frameCenter = frame.transform.TransformPoint(frame.center);
        Vector3 gridExtent = new Vector3(Width * cellSize, 0f, Height * cellSize);
        originWorld = frameCenter - gridExtent / 2f + new Vector3(cellSize, 0f, cellSize) / 2f;

        Debug.Log($"[GridManager] Initialize: frame={frame.gameObject.name} frame.transform.position={frame.transform.position} " +
            $"frame.center={frame.center} frame.size={frame.size} frame.transform.lossyScale={frame.transform.lossyScale} " +
            $"frameSize={frameSize} cellSize={cellSize} frameCenter={frameCenter} originWorld={originWorld}");
    }

    public void SetFallFramesPerRow(int frames)
    {
        fallInterval = frames / 60f;
    }

    /// <returns>False if the spawn cells are already blocked (game over) - state is left unchanged.</returns>
    public bool SpawnPiece(PieceType type)
    {
        Vector2Int[] shape = Shapes[type][0];
        Vector2Int origin = new Vector2Int((Width - MaxX(shape)) / 2, Height - 1 - MaxY(shape));

        if (!IsValidPosition(origin, shape))
        {
            OnGameOver?.Invoke();
            return false;
        }

        currentPieceType = type;
        pieceShape = shape;
        pieceOrigin = origin;
        currentRotation = 0;
        hasPiece = true;
        fallTimer = 0f;
        OnPieceSpawned?.Invoke(GetPieceCells());
        return true;
    }

    static int MaxX(Vector2Int[] shape)
    {
        int maxX = 0;
        foreach (Vector2Int offset in shape) maxX = Mathf.Max(maxX, offset.x);
        return maxX;
    }

    static int MaxY(Vector2Int[] shape)
    {
        int maxY = 0;
        foreach (Vector2Int offset in shape) maxY = Mathf.Max(maxY, offset.y);
        return maxY;
    }

    void Update()
    {
        if (!hasPiece) return;
        fallTimer += Time.deltaTime;
        if (fallTimer >= fallInterval)
        {
            fallTimer -= fallInterval;
            StepDown();
        }
    }

    void StepDown()
    {
        bool moved = TryMove(Vector2Int.down);
        if (!moved) LockPiece();
    }

    /// <summary>Manually steps the piece down by one row (soft drop). Returns whether it moved.</summary>
    public bool SoftDrop()
    {
        if (!hasPiece) return false;
        fallTimer = 0f;
        bool moved = TryMove(Vector2Int.down);
        if (!moved) LockPiece();
        return moved;
    }

    public bool TryMove(Vector2Int direction)
    {
        if (!hasPiece) return false;
        Vector2Int newOrigin = pieceOrigin + direction;
        if (!IsValidPosition(newOrigin, pieceShape)) return false;
        pieceOrigin = newOrigin;

        if (direction.y == 0 && direction.x != 0) OnPieceShifted?.Invoke(direction);
        else OnPieceMoved?.Invoke(GetPieceCells());

        return true;
    }

    public bool TryRotate(int direction)
    {
        if (!hasPiece) return false;
        int nextRotation = ((currentRotation + direction) % 4 + 4) % 4;
        Vector2Int[] candidate = Shapes[currentPieceType][nextRotation];

        foreach (int kick in RotationKicks)
        {
            Vector2Int candidateOrigin = pieceOrigin + new Vector2Int(kick, 0);
            if (IsValidPosition(candidateOrigin, candidate))
            {
                pieceOrigin = candidateOrigin;
                pieceShape = candidate;
                currentRotation = nextRotation;
                OnPieceRotated?.Invoke(GetPieceCells());
                return true;
            }
        }
        return false;
    }

    bool IsValidPosition(Vector2Int origin, Vector2Int[] shape)
    {
        foreach (Vector2Int offset in shape)
        {
            Vector2Int cell = origin + offset;
            if (cell.x < 0 || cell.x >= Width || cell.y < 0 || cell.y >= Height) return false;
            if (cells[cell.x, cell.y] == CellState.Locked) return false;
        }
        return true;
    }

    void LockPiece()
    {
        List<Vector2Int> lockedCells = GetPieceCells();
        hasPiece = false;
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

    /// <summary>
    /// Waypoints for PieceHandle to trace - same as GetPieceCells() for every piece except T
    /// (walks back through its center cell instead of jumping, see TTraceWaypoints) and O (traces
    /// back to its start cell, see below). May contain the same cell more than once; never use this
    /// for collision/locking.
    /// </summary>
    public List<Vector2Int> GetPieceTraceWaypoints()
    {
        List<Vector2Int> result = new List<Vector2Int>();
        if (!hasPiece) return result;
        Vector2Int[] waypoints = currentPieceType == PieceType.T ? TTraceWaypoints[currentRotation] : pieceShape;
        foreach (Vector2Int offset in waypoints)
            result.Add(pieceOrigin + offset);
        // The O-piece ("circle") is a closed 2x2 loop; trace back to the starting cell so the
        // handle ends where it began instead of resting at the last (bottom-right) corner.
        if (currentPieceType == PieceType.O && waypoints.Length > 0)
            result.Add(pieceOrigin + waypoints[0]);
        return result;
    }

    public Vector3 GridToWorld(Vector2Int cell)
    {
        return originWorld + new Vector3(cell.x * cellSize, 0f, cell.y * cellSize);
    }

    /// <summary>
    /// Inverse of GridToWorld: the grid cell whose centre is nearest to a world position, clamped
    /// to the valid range so a position just outside the field still maps to the nearest edge cell.
    /// </summary>
    public Vector2Int WorldToGrid(Vector3 world)
    {
        int x = Mathf.RoundToInt((world.x - originWorld.x) / cellSize);
        int y = Mathf.RoundToInt((world.z - originWorld.z) / cellSize);
        return new Vector2Int(Mathf.Clamp(x, 0, Width - 1), Mathf.Clamp(y, 0, Height - 1));
    }

    public bool IsLocked(Vector2Int cell)
    {
        if (cell.x < 0 || cell.x >= Width || cell.y < 0 || cell.y >= Height) return false;
        return cells[cell.x, cell.y] == CellState.Locked;
    }

    public List<Vector2Int> GetLockedCells()
    {
        List<Vector2Int> result = new List<Vector2Int>();
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                if (cells[x, y] == CellState.Locked) result.Add(new Vector2Int(x, y));
            }
        }
        return result;
    }
}
