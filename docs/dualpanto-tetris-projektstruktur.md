# DualPanto Tetris – Projektkontext & Architekturentscheidungen

Dieses Dokument fasst die Architektur- und Strukturentscheidungen für das Unity-Tetris-Projekt für den DualPanto zusammen. Gedacht als Kontext für Weiterarbeit (auch mit Claude Code / "Vibe Coding").

## Projektkontext

- **Ziel:** Tetris für den DualPanto (haptisches Gerät für blinde Nutzer:innen), siehe https://hpi.de/baudisch/projects/dualpanto.html
- **Toolkit:** https://github.com/HassoPlattnerInstituteHCI/unity-dualpanto-toolkit
- **Referenzprojekt aus dem Kurs:** https://github.com/HassoPlattnerInstituteHCI/bis-rogue (flache, pragmatische Struktur, keine strikte Layered Architecture – gilt als Maßstab für "akzeptabler Kursstandard")
- **Design-Vorlage:** eigene Slides (`S2-W12-BIS_TetrisDesign.pptx`) mit levelbasiertem Tutorial-Aufbau
- Kein Unity-Vorwissen außer Unterrichts-Tutorials; C++/OOP-Hintergrund aus PT2

## Grundlegende Architekturentscheidung

**Kein separates "Core"-Layer (reines C# ohne UnityEngine).** Ursprünglich diskutiert (Hexagonal/Ports&Adapters-Idee), aber bewusst verworfen, weil:
- Das Kursbeispiel `bis-rogue` selbst flach und eng gekoppelt ist (Singletons, `FindObjectOfType`, Logik direkt in `OnTriggerEnter`) – das ist offenbar akzeptabler Standard
- Tarodevs offizielles "Project Structure"-Template (`.unitypackage`, YouTube-Kanal Tarodev) hat ebenfalls **keine** Unity-freie Core-Schicht – alles ist MonoBehaviour oder ScriptableObject
- Für ein Uni-Projekt in diesem Umfang ist die Trennung Overhead ohne klaren Nutzen, solange keine dedizierten Unit-Tests für die Grid-Logik geplant sind

**Konsequenz:** Tetris-Logik lebt direkt in MonoBehaviours (`GridManager`). Trade-off bewusst: dafür keine schnellen Edit-Mode-Tests möglich (MonoBehaviours lassen sich nicht mit `new` instanziieren), nur Play-Mode-Tests mit echter Szene.

## Vorbilder / Quellen für die Struktur

1. **Tarodev "Project Structure" Template** – liefert: `StaticInstance<T>` / `Singleton<T>` / `PersistentSingleton<T>` Basisklassen, `Systems`-Root-Pattern, enum-basierte State-Machine-Manager, ScriptableObject-Pattern für Daten-Assets. Read-Me des Templates betont explizit: bewusst starke Kopplung, kein Over-Engineering für kleine/mittlere Projekte.
2. **bis-rogue (Kursbeispiel)** – bestätigt: flache MonoBehaviour-Struktur, Singletons wie `SoundManager.Instance`, direkte Kollisionslogik in `OnTriggerEnter`, ist völlig akzeptabel.
3. **Ryan Hipple – "Game Architecture with Scriptable Objects"** (Unite Austin 2017) – Hintergrund für ScriptableObject-getriebene Daten (v.a. für Level-Definitionen relevant).
4. Community-Begriff für Unity-freie reine Klassen: **"POCO"** (Plain Old C# Objects) – falls das Thema später doch wieder relevant wird.

## Finale Ordnerstruktur

```
Assets/
  _Scripts/
    Managers/        → Spielzustand + Tetris-Logik (State + Verhalten, das sich ändert)
      GameManager.cs   (State Machine: TutorialStep, orchestriert Level-Ablauf)
      GridManager.cs   (Grid-Zustand, Kollision, Zeilen-Clear, Fall-Timer)
    Systems/          → wiederverwendbare, spielunabhängige Infrastruktur
      Systems.cs        (PersistentSingleton-Root, hält andere Systems als Kinder)
      AudioSystem.cs
      SpeechSystem.cs   (Wrapper um SpeechIO für "System says"-Ansagen)
      PantoSystem.cs    (Piece-Handle-Positionierung, Obstacle-Erzeugung)
    Scriptables/       → Daten als Assets, keine Logik
      LevelDefinition.cs (pro Level: Grid-Größe, erlaubte Steine, Text, Erfolgsbedingung)
    Board/             → Verhalten einzelner Spielfeld-Objekte
      PieceCursor.cs    (visuelle Referenz für Position des fallenden Steins)
      LockedBlock.cs    (hält GridPosition eines gelockten Blocks)
    Utilities/         → generischer Code ohne jeden Spielbezug
      StaticInstance.cs (Singleton-Basisklassen, von Tarodev übernommen)
      Helpers.cs
  Prefabs/
    Blocks/             (Block-Prefab mit Panto Box Collider)
    Systems.prefab
  Resources/
    Levels/             (LevelDefinition-Assets zum dynamischen Laden)
  Scenes/
    Main.unity
```

**Entscheidungsregel pro neuer Datei:**

| Frage | Ordner |
|---|---|
| Verändert sich der Spielzustand dadurch? | `Managers` |
| Wiederverwendbarer technischer Dienst, kein Tetris-Wissen nötig? | `Systems` |
| Konfiguration/Daten, keine Logik? | `Scriptables` |
| Verhalten eines einzelnen Spielfeld-Objekts? | `Board` |
| Gar kein Spielbezug? | `Utilities` |
| Wird zur Laufzeit instanziiert? | `Prefabs` |
| Muss dynamisch per Code geladen werden? | `Resources` |

**Enums/kleine Hilfstypen** (`CellState`, `Direction`, `TutorialStep`) werden NICHT in eigene Dateien ausgelagert, sondern direkt unten in der Datei der Klasse definiert, die sie hauptsächlich nutzt – erst bei ordnerübergreifender Nutzung (z. B. `PieceType`) in eine eigene Datei ziehen. Prinzip: Struktur erst hinzufügen, wenn der Bedarf konkret auftritt, nicht vorausschauend.

## Objekt-/Szenen-Hierarchie (Scene-Fenster, nicht Skript-Ordner)

```
Main (Scene)
├── Systems
│   ├── AudioSystem
│   ├── SpeechSystem
│   └── PantoSystem
├── GameManager            ← GameManager.cs + GridManager.cs auf demselben Objekt
├── Panto                  ← Toolkit-Prefab (Lower/Upper Handle, Working Area)
├── Board
│   ├── PieceCursor
│   └── LockedBlocks       ← leerer Container, wird zur Laufzeit mit Block-Instanzen befüllt
└── ObstacleManager        ← Toolkit-eigene Component, scannt Panto Box Collider
```

## Singleton-Pattern (aus Tarodev-Template übernommen)

```csharp
public abstract class StaticInstance<T> : MonoBehaviour where T : MonoBehaviour {
    public static T Instance { get; private set; }
    protected virtual void Awake() => Instance = this as T;
}
public abstract class Singleton<T> : StaticInstance<T> where T : MonoBehaviour {
    protected override void Awake() {
        if (Instance != null) Destroy(gameObject);
        base.Awake();
    }
}
public abstract class PersistentSingleton<T> : Singleton<T> where T : MonoBehaviour {
    protected override void Awake() {
        base.Awake();
        DontDestroyOnLoad(gameObject);
    }
}
```
Genutzt für `GameManager`, `Systems`, `AudioSystem` etc. `GridManager` NICHT als Singleton (wird über `[SerializeField]`-Referenz vom `GameManager` gehalten).

## Toolkit-Spezifika (unity-dualpanto-toolkit)

- **Komponentenbasiert, nicht API-basiert**: kein zentraler Service mit Methodenaufrufen, sondern Components (`Me Handle`, `Panto Box Collider`), die auf GameObjects gesteckt werden. Verhalten "passiert" automatisch über Unity-Lifecycle, nicht über explizite Methodenaufrufe.
- **`Me Handle`** = wird vom Nutzer aktiv bewegt → entspricht der **stack-handle** aus den Slides (Feld/Stack ertasten, Force Feedback bei Kollision)
- **Piece-Handle** (aktuiert, vom Gerät bewegt) = entspricht der **it-handle** aus den Slides (zeichnet Position/Form des fallenden Steins nach) – **offene Frage:** welche Toolkit-Component genau die Zielposition programmatisch setzt, muss noch in den `ExampleScripts` des Repos geklärt werden
- **`Panto Box Collider`**: Component für physische Hindernisse, wird vom `Obstacle Manager` beim Start automatisch gescannt
- **`Obstacle Manager`**: Toolkit-eigenes GameObject/Component (kein eigener Code), scannt Szene nach `Panto Box Collider`n; Laufzeit-Toggle mit Tasten `E`/`D`
- **Debug/Test ohne Hardware**: `DualPantoSync` hat eingebauten Emulator-Modus (Maussteuerung), zusätzlich "Blind Emulator" (Taste `b`) für Sichtmodi – eigene Mock-Implementierung ist NICHT nötig
- **Wichtige Kapazitätsgrenze**: Gerät crasht laut Troubleshooting-Doku bei zu vielen gleichzeitigen Hindernissen. Bei vollem Tetris-Stack (bis zu 200 Blöcke bei 10x20-Grid) potenziell kritisch → zusammenhängende Blockreihen ggf. zu größeren Collider-Boxen zusammenfassen statt jeden Block einzeln zu registrieren; ggf. `onUpper`/`onLower` gezielt nur für die Handle setzen, die es braucht (vermutlich nur `me`/Stack-Handle)
- **Offene Frage:** ob es eine Methode gibt, um zur Laufzeit neu instanzierte Hindernisse nachzumelden, oder ob der Obstacle Manager nur einmalig beim Start scannt – noch in `ExampleScripts` zu klären
- Speech-Ausgabe vermutlich über **SpeechIO**-Submodul (im Toolkit-README verlinkt), passend zu den "System says"-Texten aus den Slides

## Spieldesign aus den Slides (erste Level)

Steuerung grundsätzlich anders als klassisches Tetris:
- **Piece-Bewegung** (links/rechts) über **Fußpedale**, nicht über Handles
- **it-handle/Piece-Handle**: rein passiv für den Nutzer, vom Gerät aktuiert, zeichnet fallenden Stein nach
- **me-handle/Stack-Handle**: aktiv vom Nutzer bewegt, zum Ertasten von Spielfeldrand und liegendem Stack, mit Force Feedback + Sound bei Blockberührung

Level-Progression (Tutorial-artig):
- **Level 0**: Spielfeldrand ertasten (Stack-Handle)
- **Level 1**: erster 4x1-Stein fällt automatisch, Stack-Handle muss unten warten
- **Level 2**: Pedal-Steuerung für 2x2-Stein (links/rechts)
- **Level 3**: Stein neben bestehendem Stein landen, Stack-Handle zum Ertasten des bestehenden Steins nutzen
- Danach (noch nicht im Detail spezifiziert): alle Steinformen, Rotation, Punktesystem, Feldgröße erhöhen, Next-Box-Vorschau

## Empfohlenes Vorgehen (Reihenfolge)

1. Toolkit-`ExampleScenes`/`ExampleScripts` durchgehen: klären, wie die Piece-Handle programmatisch positioniert wird; wie Fußpedal-Input reinkommt (oder Tastatur-Fallback)
2. SpeechIO installieren, Testsatz sprechen lassen
3. **Nur Level 0 + 1 als erstes Ziel** – bewusst klein, um einmal die volle Kette (Grid-Logik → Szene → Panto/Emulator → Ton) durchzuspielen
4. `GridManager` mit `Initialize(width, height)` (Grid-Größe kommt aus `LevelDefinition`, nicht hartcodiert), `Tick()`, `TryMove()`, Events `OnPieceLocked`/`OnLinesCleared`
5. `GameManager` als State Machine (`TutorialStep`-Enum), verbindet Grid-Events mit Sound/Speech/Panto-Reaktionen
6. Panto-Integration andocken (Me Handle-Prefab, Piece-Handle-Ziel, Panto Box Collider auf Spielfeldrand + gelockten Blöcken)
7. Level 0/1-Ablauf zunächst hart verdrahtet (einfaches enum + switch), generisches Level-Framework erst bauen, wenn bei Level 4+ ein klares Wiederholungsmuster sichtbar wird
8. Erst danach: Level 2+ (Pedale, zweiter Stein, weitere Mechaniken)

## Bewusst NICHT gemacht (und warum)

- Kein `IHapticOutput`-Interface / genereller Adapter-Layer – Toolkit ist komponentenbasiert, ein Interface hätte keinen echten Nutzen
- Keine Assembly Definitions / strikte Ordner-Trennung für Testbarkeit – nicht nötig ohne Edit-Mode-Tests
- Keine eigene Mock-Implementierung für den Panto – Emulator-Modus im Toolkit deckt das ab
- Kein generisches Level-/Tutorial-Framework von Anfang an – erst ab sichtbarem Wiederholungsmuster einführen
