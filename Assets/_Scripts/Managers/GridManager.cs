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

    // Wall-kick offsets tried in order when a rotation doesn't fit in its target cell. Identity
    // first (rotate in place), then horizontal nudges (left/right off a wall or block), then
    // DOWNWARD nudges - the latter make Tetris-style top-edge rotation work: a piece flush against
    // the top whose rotated form pokes above the field (only the I piece: its vertical form is 4
    // tall) is pushed DOWN into the field instead of the rotation failing. y is up, so negative y
    // = down; the I vertical needs up to (0,-2). Down+horizontal combos handle corners near the top.
    static readonly Vector2Int[] RotationKicks =
    {
        new Vector2Int(0, 0),
        new Vector2Int(-1, 0), new Vector2Int(1, 0),
        new Vector2Int(-2, 0), new Vector2Int(2, 0),
        new Vector2Int(0, -1), new Vector2Int(0, -2),
        new Vector2Int(-1, -1), new Vector2Int(1, -1),
    };

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
    bool fallPaused;
    float fallPausedAt;
    // Failsafe: if nothing calls ResumeFall (e.g. no PieceHandle in the scene to run the cleared-
    // line trace), auto-resume so the game can never get stuck paused. Longer than any real trace.
    const float MaxClearPauseSeconds = 15f;
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
    // Fires when a relative rotation attempt (TryRotate) couldn't find any valid orientation/kick -
    // i.e. the player tried to rotate but it's genuinely blocked. Not fired for TrySetRotation calls
    // that are no-ops because the target state already matches (that's not a "failed" attempt).
    public event Action OnRotationFailed;
    // Fires when a pure left/right shift attempt is blocked (wall or a locked cell) - i.e. the
    // player tried to move but it's genuinely blocked. Not fired for a blocked fall/soft-drop step
    // (that's the piece landing, a normal outcome with its own OnPieceLocked event, not a failure).
    public event Action OnShiftFailed;
    // Fires when the board is cleared for a restart, so views (locked blocks etc.) can wipe themselves.
    public event Action OnReset;
    // Fires when the board is mutated directly (not via normal play) - locked cells added, or a
    // resize - so views rebuild from GetLockedCells(). Used by the tutorial's pre-placed blocks.
    public event Action OnBoardChanged;

    // The cells the most recently locked piece occupied (set in LockPiece). Lets the tutorial check
    // where the player actually placed a piece.
    public List<Vector2Int> LastLockedPieceCells { get; private set; } = new List<Vector2Int>();

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
        DeriveGeometry();

        Vector3 frameSize = Vector3.Scale(frame.size, frame.transform.lossyScale);
        Debug.Log($"[GridManager] Initialize: frame={frame.gameObject.name} frame.transform.position={frame.transform.position} " +
            $"frame.center={frame.center} frame.size={frame.size} frame.transform.lossyScale={frame.transform.lossyScale} " +
            $"frameSize={frameSize} cellSize={cellSize} originWorld={originWorld}");
    }

    // Recomputes cellSize/originWorld from the (fixed physical) frame for the current Width/Height.
    // Called by Initialize and Resize - a smaller grid gives larger cells, keeping the same footprint.
    void DeriveGeometry()
    {
        Vector3 frameSize = Vector3.Scale(frame.size, frame.transform.lossyScale);
        cellSize = Mathf.Min(frameSize.x / Width, frameSize.z / Height);

        Vector3 frameCenter = frame.transform.TransformPoint(frame.center);
        Vector3 gridExtent = new Vector3(Width * cellSize, 0f, Height * cellSize);
        originWorld = frameCenter - gridExtent / 2f + new Vector3(cellSize, 0f, cellSize) / 2f;
    }

    /// <summary>
    /// Changes the grid to a new size at runtime (tutorial). Re-derives cellSize/origin from the
    /// fixed frame, drops any piece, and fires OnReset so views wipe. The caller must also rebuild
    /// anything grid-derived that isn't event-driven (StackHandle rails - see TutorialManager).
    /// </summary>
    public void Resize(int gridWidth, int gridHeight)
    {
        Width = gridWidth;
        Height = gridHeight;
        cells = new CellState[Width, Height];
        hasPiece = false;
        fallPaused = false;
        fallTimer = 0f;
        LastLockedPieceCells.Clear();
        DeriveGeometry();
        OnReset?.Invoke();
    }

    /// <summary>
    /// Locks the given cells directly (not via a played piece) and refreshes views via
    /// OnBoardChanged. Used by the tutorial to pre-place blocks. Cells outside the grid are ignored.
    /// </summary>
    public void AddLockedCells(IEnumerable<Vector2Int> cellsToLock)
    {
        foreach (Vector2Int cell in cellsToLock)
            if (cell.x >= 0 && cell.x < Width && cell.y >= 0 && cell.y < Height)
                cells[cell.x, cell.y] = CellState.Locked;
        OnBoardChanged?.Invoke();
    }

    /// <summary>
    /// Clears the given cells back to empty and refreshes views via OnBoardChanged. Used by the
    /// tutorial to take back a piece the player placed badly.
    /// </summary>
    public void RemoveLockedCells(IEnumerable<Vector2Int> cellsToClear)
    {
        foreach (Vector2Int cell in cellsToClear)
            if (cell.x >= 0 && cell.x < Width && cell.y >= 0 && cell.y < Height)
                cells[cell.x, cell.y] = CellState.Empty;
        OnBoardChanged?.Invoke();
    }

    /// <summary>Wipes the board back to an empty, piece-less state for a restart. Fires OnReset so
    /// views can clear their visuals; the caller then spawns a fresh piece.</summary>
    public void Reset()
    {
        if (cells != null) System.Array.Clear(cells, 0, cells.Length);
        hasPiece = false;
        fallPaused = false;
        currentRotation = 0;
        fallTimer = 0f;
        OnReset?.Invoke();
    }

    public void SetFallFramesPerRow(int frames)
    {
        fallInterval = frames / 60f;
    }

    /// <returns>False if the spawn cells are already blocked (game over) - state is left unchanged.</returns>
    public bool SpawnPiece(PieceType type)
    {
        Vector2Int[] shape = Shapes[type][0];
        // Spawn flush against the top edge (like real Tetris). A piece whose rotated form is taller
        // than its spawn form - only the I piece (1 row horizontal vs. 4 vertical) - would then poke
        // above the field when rotated at the top; the DOWNWARD wall-kicks in RotationKicks push it
        // back down into the field, so it stays rotatable at the top edge (replaces the old
        // spawn-lower workaround).
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
        if (fallPaused)
        {
            if (Time.time - fallPausedAt > MaxClearPauseSeconds) ResumeFall();
            return;
        }
        if (!hasPiece) return;
        fallTimer += Time.deltaTime;
        if (fallTimer >= fallInterval)
        {
            fallTimer -= fallInterval;
            StepDown();
        }
    }

    // Suspends gravity (and soft drop) - used during the cleared-line trace so a freshly spawned
    // piece doesn't start falling until that animation has fully played out. See ResumeFall.
    public void PauseFall()
    {
        fallPaused = true;
        fallPausedAt = Time.time;
    }

    // Called by PieceHandle when the cleared-line trace finishes; gives the piece a full interval
    // before its next drop.
    public void ResumeFall()
    {
        fallPaused = false;
        fallTimer = 0f;
    }

    void StepDown()
    {
        bool moved = TryMove(Vector2Int.down);
        if (!moved) LockPiece();
    }

    /// <summary>Manually steps the piece down by one row (soft drop). Returns whether it moved.</summary>
    public bool SoftDrop()
    {
        if (!hasPiece || fallPaused) return false;
        fallTimer = 0f;
        bool moved = TryMove(Vector2Int.down);
        if (!moved) LockPiece();
        return moved;
    }

    public bool TryMove(Vector2Int direction)
    {
        if (!hasPiece) return false;
        bool isShift = direction.y == 0 && direction.x != 0;
        Vector2Int newOrigin = pieceOrigin + direction;
        if (!IsValidPosition(newOrigin, pieceShape))
        {
            if (isShift) OnShiftFailed?.Invoke();
            return false;
        }
        pieceOrigin = newOrigin;

        if (isShift) OnPieceShifted?.Invoke(direction);
        else OnPieceMoved?.Invoke(GetPieceCells());

        return true;
    }

    public int CurrentRotation => currentRotation;

    // Which tetromino is currently falling. Only meaningful while a piece exists (GetPieceCells()
    // non-empty); the tutorial reads it on OnPieceSpawned to describe each shape the first time.
    public PieceType CurrentPieceType => currentPieceType;

    public bool TryRotate(int direction)
    {
        if (!hasPiece) return false;
        bool rotated = TrySetRotation(((currentRotation + direction) % 4 + 4) % 4);
        if (!rotated) OnRotationFailed?.Invoke();
        return rotated;
    }

    /// <summary>
    /// Rotates the piece to an absolute rotation state (0-3), with the same horizontal wall-kick
    /// fallback as TryRotate. No-op (returns false) if already in that state or the piece can't fit.
    /// Used by handle-rotation input, which maps the physical handle angle to an absolute state.
    /// </summary>
    public bool TrySetRotation(int targetRotation)
    {
        if (!hasPiece) return false;
        targetRotation = (targetRotation % 4 + 4) % 4;
        if (targetRotation == currentRotation) return false;
        Vector2Int[] candidate = Shapes[currentPieceType][targetRotation];

        foreach (Vector2Int kick in RotationKicks)
        {
            Vector2Int candidateOrigin = pieceOrigin + kick;
            if (IsValidPosition(candidateOrigin, candidate))
            {
                pieceOrigin = candidateOrigin;
                pieceShape = candidate;
                currentRotation = targetRotation;
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
        LastLockedPieceCells = lockedCells;
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

        // Collapse from the TOP row down. Going bottom-up would be wrong: removing a low row shifts
        // every row above it down by one, so the next entry in clearedRows no longer points at the
        // row it named (only one of two full rows actually got cleared). Descending order leaves the
        // still-pending, lower indices untouched.
        for (int i = clearedRows.Count - 1; i >= 0; i--)
        {
            int row = clearedRows[i];
            for (int y = row; y < Height - 1; y++)
                for (int x = 0; x < Width; x++)
                    cells[x, y] = cells[x, y + 1];
            for (int x = 0; x < Width; x++)
                cells[x, Height - 1] = CellState.Empty;
        }
        // Freeze gravity until the cleared-line trace finishes (PieceHandle calls ResumeFall). The
        // next piece has already spawned (in OnPieceLocked above), so this holds it in place at the
        // top instead of letting it fall during the trace.
        PauseFall();
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

    /// <summary>
    /// True if the cell holds a locked block OR is part of the current falling piece - i.e. any
    /// cell where the player would feel a block. StackHandle uses this (not IsLocked) so the
    /// occupied-cell step sound also fires when moving over the falling piece, not just the stack.
    /// </summary>
    public bool IsOccupied(Vector2Int cell)
    {
        if (IsLocked(cell)) return true;
        if (!hasPiece) return false;
        foreach (Vector2Int offset in pieceShape)
            if (pieceOrigin + offset == cell) return true;
        return false;
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
