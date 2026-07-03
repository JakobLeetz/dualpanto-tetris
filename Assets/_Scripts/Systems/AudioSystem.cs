using UnityEngine;

public class AudioSystem : StaticInstance<AudioSystem>
{
    [SerializeField] AudioSource sfxSource;

    public void PlaySfx(AudioClip clip)
    {
        if (clip == null || sfxSource == null) return;
        sfxSource.PlayOneShot(clip);
    }
}
