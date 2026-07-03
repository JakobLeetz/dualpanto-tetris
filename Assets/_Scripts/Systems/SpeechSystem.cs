using System.Threading.Tasks;
using SpeechIO;

/// <summary>
/// Wrapper around SpeechIO.SpeechOut for "System says" announcements (level intros, feedback).
/// </summary>
public class SpeechSystem : StaticInstance<SpeechSystem>
{
    SpeechOut speechOut;

    protected override void Awake()
    {
        base.Awake();
        speechOut = new SpeechOut();
    }

    public async Task Say(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        await speechOut.Speak(text);
    }

    public void Stop() => speechOut.Stop();

    protected override void OnApplicationQuit()
    {
        Stop();
        base.OnApplicationQuit();
    }
}
