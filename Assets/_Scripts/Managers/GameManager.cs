using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Level 0/1 flow, hard-wired as a simple switch between two tutorial steps. A generic
/// level framework is intentionally not built yet - see docs/dualpanto-tetris-projektstruktur.md.
/// </summary>
public class GameManager : Singleton<GameManager>
{
    public enum TutorialStep
    {
        Level0_ExploreBoundary,
        Level1_FirstPieceFalls,
        Done
    }

    [SerializeField] GridManager gridManager;
    [SerializeField] LevelDefinition level0;
    [SerializeField] LevelDefinition level1;
    [SerializeField] Transform lockedBlocksContainer;

    readonly List<GameObject> boundaryObstacles = new List<GameObject>();

    public TutorialStep CurrentStep { get; private set; }

    async void Start()
    {
        await RunLevel0();
        await RunLevel1();
        CurrentStep = TutorialStep.Done;
    }

    async Task RunLevel0()
    {
        CurrentStep = TutorialStep.Level0_ExploreBoundary;
        gridManager.Initialize(level0.gridWidth, level0.gridHeight);
        BuildBoundaryObstacles();

        await SpeechSystem.Instance.Say(level0.introText);
        await WaitForContinueInput();
    }

    async Task RunLevel1()
    {
        CurrentStep = TutorialStep.Level1_FirstPieceFalls;
        ClearBoundaryObstacles();
        gridManager.Initialize(level1.gridWidth, level1.gridHeight);
        BuildBoundaryObstacles();

        await SpeechSystem.Instance.Say(level1.introText);

        var pieceLocked = new TaskCompletionSource<bool>();
        void OnLocked(List<Vector2Int> cells)
        {
            SpawnLockedBlocks(cells);
            pieceLocked.TrySetResult(true);
        }

        gridManager.OnPieceLocked += OnLocked;
        gridManager.SpawnPiece(level1.allowedPieces[0]);
        await pieceLocked.Task;
        gridManager.OnPieceLocked -= OnLocked;

        await SpeechSystem.Instance.Say("Der Stein ist gelandet.");
    }

    // Keyboard fallback for the pedal input, which isn't wired up yet (see docs, open question).
    async Task WaitForContinueInput()
    {
        while (!Input.GetKeyDown(KeyCode.Space))
        {
            await Task.Delay(10);
        }
    }

    void BuildBoundaryObstacles()
    {
        float cs = gridManager.CellSize;
        float fieldWidth = gridManager.Width * cs;
        float fieldHeight = gridManager.Height * cs;
        float wallThickness = cs * 0.2f;
        Vector3 center = gridManager.transform.position + new Vector3((fieldWidth - cs) / 2f, 0f, (fieldHeight - cs) / 2f);

        boundaryObstacles.Add(CreateWall("Wall_South", center + new Vector3(0f, 0f, -fieldHeight / 2f), new Vector3(fieldWidth, cs, wallThickness)));
        boundaryObstacles.Add(CreateWall("Wall_North", center + new Vector3(0f, 0f, fieldHeight / 2f), new Vector3(fieldWidth, cs, wallThickness)));
        boundaryObstacles.Add(CreateWall("Wall_West", center + new Vector3(-fieldWidth / 2f, 0f, 0f), new Vector3(wallThickness, cs, fieldHeight)));
        boundaryObstacles.Add(CreateWall("Wall_East", center + new Vector3(fieldWidth / 2f, 0f, 0f), new Vector3(wallThickness, cs, fieldHeight)));
    }

    void ClearBoundaryObstacles()
    {
        foreach (GameObject wall in boundaryObstacles)
        {
            Destroy(wall);
        }
        boundaryObstacles.Clear();
    }

    GameObject CreateWall(string name, Vector3 center, Vector3 size)
    {
        GameObject wall = new GameObject(name);
        wall.transform.SetParent(gridManager.transform, worldPositionStays: false);
        wall.transform.position = center;
        BoxCollider box = wall.AddComponent<BoxCollider>();
        box.size = size;
        PantoSystem.Instance.CreateBoxObstacle(wall, onUpper: true, onLower: false);
        return wall;
    }

    void SpawnLockedBlocks(List<Vector2Int> cells)
    {
        foreach (Vector2Int cell in cells)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = $"LockedBlock_{cell.x}_{cell.y}";
            block.transform.SetParent(lockedBlocksContainer, worldPositionStays: false);
            block.transform.position = gridManager.GridToWorld(cell);
            block.transform.localScale = Vector3.one * gridManager.CellSize;
            block.AddComponent<LockedBlock>().GridPosition = cell;
            PantoSystem.Instance.CreateBoxObstacle(block, onUpper: true, onLower: false);
        }
    }
}
