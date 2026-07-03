/// <summary>
/// Persistent root GameObject holding AudioSystem, SpeechSystem and PantoSystem as children,
/// so they survive scene loads together. Each child exposes its own static Instance.
/// </summary>
public class Systems : PersistentSingleton<Systems> { }
