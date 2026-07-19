using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpeechIO;
using UnityEngine;

/// <summary>
/// Wrapper around SpeechIO.SpeechOut for "System says" announcements (tutorial prompts, feedback).
/// SpeechOut is OS text-to-speech (macOS 'say', Windows SAPI, Linux espeak) - one OS process per
/// utterance, and SpeechBase.isSpeaking is a shared static flag, so overlapping Speak calls garble
/// each other. This queues utterances and speaks them strictly one after another. Say returns a
/// Task that completes when THAT utterance finishes, so callers (e.g. a tutorial) can await a line
/// before continuing.
/// </summary>
public class SpeechSystem : StaticInstance<SpeechSystem>
{
    [SerializeField] SpeechBase.LANGUAGE language = SpeechBase.LANGUAGE.ENGLISH;
    [SerializeField, Range(0.5f, 2f)] float speed = 1f;

    SpeechOut speechOut;
    readonly Queue<Utterance> queue = new Queue<Utterance>();
    bool running;

    class Utterance
    {
        public string Text;
        public TaskCompletionSource<bool> Done;
    }

    protected override void Awake()
    {
        base.Awake();
        speechOut = new SpeechOut();
    }

    void Start()
    {
        // Warm up the OS TTS engine/voice once at startup so the FIRST real announcement doesn't
        // pay the cold-start penalty (macOS 'say' loads the voice on first use, which is the main
        // "takes ages to start talking" delay). A single space is effectively silent.
        _ = Say(" ");
    }

    /// <summary>
    /// Speaks text. By default it queues after anything already queued (good for tutorial lines).
    /// With interrupt=true it drops the pending queue and cuts off the current utterance first, so
    /// the newest announcement plays right away instead of lagging behind a backlog (good for
    /// frequent gameplay callouts like per-line-clear score). The returned Task completes when this
    /// utterance finishes.
    /// </summary>
    public Task Say(string text, bool interrupt = false)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        if (interrupt)
        {
            DrainQueue();
            speechOut?.Stop(false); // kill the current utterance only, keep the source usable
        }
        Utterance u = new Utterance { Text = text, Done = new TaskCompletionSource<bool>() };
        queue.Enqueue(u);
        if (!running) _ = RunQueue();
        return u.Done.Task;
    }

    async Task RunQueue()
    {
        running = true;
        while (queue.Count > 0)
        {
            Utterance u = queue.Dequeue();
            try
            {
                await speechOut.Speak(u.Text, speed, language);
            }
            catch (Exception e)
            {
                // e.g. MacOSSpeechOut throws if the say process is killed; don't stall the queue.
                Debug.LogWarning($"[SpeechSystem] speak failed: {e.Message}");
            }
            u.Done.TrySetResult(true);
        }
        running = false;
    }

    public void SetLanguage(SpeechBase.LANGUAGE lang) => language = lang;
    public void SetSpeed(float value) => speed = value;

    /// <summary>
    /// Drops everything queued and cuts off the current utterance, but keeps the speech source
    /// USABLE - the next Say speaks normally. This is what you want to abandon an announcement
    /// mid-flight (e.g. the tutorial's dev skip key).
    /// </summary>
    public void StopSpeaking()
    {
        DrainQueue();
        speechOut?.Stop(false); // current utterance only - see Stop() for why the default is fatal
    }

    /// <summary>
    /// Shutdown only. SpeechOut.Stop() defaults to stopAll: true, which calls source.Cancel() on its
    /// CancellationTokenSource - permanently. Every later Speak then sees IsCancellationRequested and
    /// returns without saying anything, i.e. TTS is dead for the rest of the session. Use
    /// StopSpeaking() for anything that isn't quitting.
    /// </summary>
    public void Stop()
    {
        DrainQueue();
        speechOut?.Stop();
    }

    // Completes the dropped utterances instead of just clearing them - a caller awaiting one would
    // otherwise wait forever on a task nothing can ever complete.
    void DrainQueue()
    {
        while (queue.Count > 0) queue.Dequeue().Done.TrySetResult(false);
    }

    protected override void OnApplicationQuit()
    {
        Stop();
        base.OnApplicationQuit();
    }
}
