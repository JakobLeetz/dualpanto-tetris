using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "Level", menuName = "DualPantoTetris/Level Definition")]
public class LevelDefinition : ScriptableObject
{
    public string levelName;
    public int gridWidth = 4;
    public int gridHeight = 8;
    public List<PieceType> allowedPieces = new List<PieceType>();
    [Multiline] public string introText;
    public LevelGoal goal;
}

public enum LevelGoal
{
    ExploreBoundary,
    PieceLocked
}
