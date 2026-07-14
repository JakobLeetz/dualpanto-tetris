/// <summary>
/// Persistent root GameObject holding SpeechSystem and PantoSystem as children, so they survive
/// scene loads together. Each child exposes its own static Instance. (SFX use the toolkit's
/// DualPantoToolkit.SoundManager, which is its own persistent singleton.)
/// </summary>
public class Systems : PersistentSingleton<Systems> { }
