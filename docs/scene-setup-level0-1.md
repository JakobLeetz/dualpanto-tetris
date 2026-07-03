# Szenen-Setup für Level 0 + 1 (Unity-Editor-Checkliste)

Der komplette Code liegt unter `Assets/_Scripts/`. Diese Anleitung baut die `Main`-Szene
so zusammen, dass der Code greift. Reihenfolge einhalten, dann am Ende einmal Play drücken.

## 1. Panto-Prefab einfügen

1. `Main.unity` öffnen (`Assets/Scenes/Main.unity`).
2. `Assets/unity-dualpanto-toolkit/Assets/Resources/Panto.prefab` in die Szene ziehen (Hierarchy).
3. Sicherstellen, dass das Objekt in der Hierarchy **exakt** `Panto` heißt (Standardname des Prefabs) –
   der komplette Code sucht es per `GameObject.Find("Panto")`.
4. Im Inspector des `Panto`-Objekts das `DualPantoSync`-Component prüfen: Häkchen bei `debug` setzen,
   solange kein echtes Gerät angeschlossen ist (Maus-Emulator, Taste `b` für Blind-Emulator-Modus).

### 1a. Standard-Kamera/Licht entfernen (Kamera zu weit reingezoomt)

Das Panto-Prefab bringt laut Toolkit-README eine eigene, passend positionierte Kamera und ein
eigenes Licht mit (Kind-Objekte unter `Panto`). Die von Unity beim Scene-Erstellen automatisch
angelegte Standard-Kamera/-Licht kollidiert damit (beide sind `MainCamera`-getaggt):

1. In der Hierarchy das **oberste** `Main Camera`-Objekt (nicht das Kind-Objekt `Camera` unter `Panto`!) löschen.
2. Falls die Szene danach immer noch zu hell/ausgewaschen wirkt: auch das oberste `Directional Light`
   löschen (Panto hat sein eigenes als Kind-Objekt).
3. `Global Volume` kann vorerst bleiben, ggf. später entfernen falls die Farben durch Tonemapping/Color
   Grading noch komisch aussehen.

### 1b. Toolkit-Materialien auf URP konvertieren (graues statt farbiges Spielfeld)

Dieses Projekt nutzt URP, das komplette Toolkit ist aber mit dem alten Built-in-Standard-Shader gebaut
(betrifft u. a. `ember_area.mat`, `doerte_area.mat`, `MeHandleMaterial.mat`, `ItHandleMaterial.mat`,
`Colliders.mat`, `Rails.mat`, `Marker.mat`, `MainBodyIt.mat`, `MainBodyMe.mat`, `LetterMaterial.mat`,
`PerceptionConeMaterial.mat`, `TrailRenderMaterial.mat` und die `_white`-Varianten der Working-Areas).
Unter URP werden diese Materialien nicht korrekt gerendert → alles erscheint grau/texturlos.

1. Im Project-Fenster zu `Assets/unity-dualpanto-toolkit/Assets/Resources/Materials` navigieren.
2. Alle `.mat`-Dateien in diesem Ordner auswählen (Strg/Cmd+A).
3. Rechtsklick → `Rendering` → `Convert Selected Built-in Materials to URP Materials` (Wortlaut kann je
   nach Unity-Version leicht variieren, z. B. auch unter `Edit > Rendering`).
4. Falls dieser Menüpunkt fehlt: `Window > Rendering > Render Pipeline Converter` öffnen, dort
   `Built-in to URP` auswählen, die Material-Konvertierung aktivieren und auf den Ordner anwenden.
5. Das Tool remapped automatisch `_MainTex`→`_BaseMap`, `_Color`→`_BaseColor`, `_Glossiness`→`_Smoothness`
   etc. – nicht manuell die Shader-Zuweisung in den Materialien ändern, dabei gehen Textur/Farbe verloren.

## 2. Systems-Hierarchie

Leeres GameObject `Systems` anlegen, `Systems.cs` draufziehen. Darunter drei Kind-Objekte:

| Kind-Objekt   | Script            | Sonstiges |
|---|---|---|
| `AudioSystem`  | `AudioSystem.cs`  | Optional: `AudioSource`-Component hinzufügen und im Script-Feld `sfxSource` verknüpfen (kann für Level 0/1 leer bleiben) |
| `SpeechSystem` | `SpeechSystem.cs` | keine weiteren Felder |
| `PantoSystem`  | `PantoSystem.cs`  | keine weiteren Felder |

`Systems` ist ein `PersistentSingleton` (`DontDestroyOnLoad`) – für den aktuellen Ein-Szenen-Stand
nicht kritisch, schadet aber nicht.

## 3. GameManager + GridManager

1. Leeres GameObject `GameManager` anlegen.
2. `GameManager.cs` **und** `GridManager.cs` beide auf dasselbe Objekt ziehen (siehe Doku: beide State-
   Klassen leben auf einem GameObject).
3. Position von `GameManager` bestimmt den Ursprung des Grids (`GridManager.GridToWorld` rechnet relativ
   zu `transform.position`). Sinnvoll: irgendwo mittig im Panto-Arbeitsbereich platzieren.
4. `GridManager`-Felder im Inspector:
   - `Cell Size`: Weltgröße einer Zelle. An die Panto-Arbeitsfläche anpassen (auf `EmberWorkingArea`/
     `DoerteWorkingArea`-Kindobjekten im Panto-Prefab nachschauen, welche Skala dort verwendet wird).
   - `Fall Interval`: Sekunden pro automatischem Fallschritt (Default 1).

## 4. Board-Hierarchie

1. Leeres GameObject `Board` anlegen (rein strukturell).
2. Kind `PieceCursor`: `PieceCursor.cs` drauf, Feld `Grid Manager` → das `GameManager`-Objekt ziehen
   (dort liegt die `GridManager`-Komponente).
3. Kind `LockedBlocks`: leeres GameObject, wird zur Laufzeit mit den gelockten Block-Instanzen befüllt.

## 5. LevelDefinition-Assets anlegen

Im Project-Fenster: Rechtsklick in `Assets/Resources/Levels/` (Ordner ggf. anlegen) →
`Create > DualPantoTetris > Level Definition`. Zwei Assets erstellen:

**Level0** (`Assets/Resources/Levels/Level0.asset`)
- `Level Name`: `Level 0 - Spielfeldrand`
- `Grid Width` / `Grid Height`: z. B. `4` / `8` (nur für die Wand-Geometrie relevant, kein Stein fällt)
- `Allowed Pieces`: leer lassen
- `Intro Text`: z. B. `"Ertaste den Rand des Spielfelds mit der Stack-Handle. Drücke die Leertaste, wenn du bereit bist."`
- `Goal`: `Explore Boundary`

**Level1** (`Assets/Resources/Levels/Level1.asset`)
- `Level Name`: `Level 1 - Erster Stein`
- `Grid Width` / `Grid Height`: z. B. `4` / `8`
- `Allowed Pieces`: `I` hinzufügen (Liste, ein Eintrag)
- `Intro Text`: z. B. `"Jetzt fällt der erste Stein automatisch. Warte mit der Stack-Handle unten."`
- `Goal`: `Piece Locked`

## 6. GameManager-Referenzen verbinden

Am `GameManager`-Objekt im Inspector:
- `Grid Manager` → sich selbst ziehen (liegt auf demselben Objekt)
- `Level 0` → `Level0.asset`
- `Level 1` → `Level1.asset`
- `Locked Blocks Container` → das `LockedBlocks`-Objekt aus Schritt 4

## 7. Kein ObstacleManager nötig

Wände und gelockte Blöcke werden zur Laufzeit direkt über `PantoSystem.CreateBoxObstacle`
erzeugt und aktiviert (`CreateObstacle()` + `Enable()`), nicht über das Toolkit-eigene
`ObstacleManager`-GameObject aus den ExampleScenes. Das muss also **nicht** in die Szene.

## 8. Testen

1. Play drücken.
2. Level 0: Speech-Ansage sollte kommen, danach mit der Maus (Emulator) die vier Wände als
   Hindernisse ertasten können. Leertaste drücken, um zu Level 1 zu wechseln.
3. Level 1: Ansage kommt, ein 4x1-Stein erscheint oben mittig im Grid und fällt automatisch
   (`Fall Interval` Sekunden pro Zelle) nach unten. Die It-Handle sollte der untersten Zelle
   des Steins folgen. Sobald der Stein den Boden erreicht, wird er gelockt, als Box-Hindernis
   registriert und eine Abschluss-Ansage kommt.

## Bekannte offene Punkte für später

- Pedal-Input ist noch nicht angebunden (aktuell Leertaste als Fallback für "weiter" in Level 0).
- `PieceCursor` folgt nur der untersten Zelle des Steins, nicht der vollen Kontur (siehe Kommentar
  im Script) – für Level 2+ ggf. ausbauen.
- Kapazitätsgrenze des Geräts bei vielen Hindernissen (siehe Haupt-Doku) noch nicht relevant bei
  einem einzelnen 4x1-Stein, aber im Blick behalten sobald der Stack wächst.
