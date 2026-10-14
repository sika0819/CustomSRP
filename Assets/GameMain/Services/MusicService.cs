using Game.Native;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Track selection + optional AudioClip playback. Volume driven by VolumeForm settings.
    /// </summary>
    public sealed class MusicService : MonoBehaviour
    {
        public static readonly string[] TrackNames =
        {
            "散步", "旅行", "工作", "驾车", "学习", "清晨", "午后", "夜晚"
        };

        public int CurrentIndex { get; private set; }
        public float MusicVolume { get; private set; } = 1f;
        public float EffectVolume { get; private set; } = 1f;
        public bool IsPlaying => _music != null && _music.isPlaying;

        AudioSource _music;
        AudioClip[] _clips = System.Array.Empty<AudioClip>();

        public static MusicService Ensure()
        {
            MusicService existing = FindAnyObjectByType<MusicService>();
            if (existing != null)
                return existing;

            var go = new GameObject("MusicService");
            DontDestroyOnLoad(go);
            return go.AddComponent<MusicService>();
        }

        void Awake()
        {
            _music = gameObject.AddComponent<AudioSource>();
            _music.playOnAwake = false;
            _music.loop = true;
            _music.spatialBlend = 0f;
            ApplySavedVolumes();
            TryLoadClips();
            if (_clips.Length > 0)
                PlayIndex(0, restart: true);
        }

        public void Pause()
        {
            if (_music != null && _music.isPlaying)
                _music.Pause();
            PublishNowPlaying();
        }

        public void Resume()
        {
            if (_music != null && _music.clip != null)
                _music.UnPause();
            if (_music != null && !_music.isPlaying && _music.clip != null)
                _music.Play();
            PublishNowPlaying();
        }

        public void Next()
        {
            if (TrackNames.Length == 0)
                return;
            PlayIndex((CurrentIndex + 1) % TrackNames.Length, restart: true);
        }

        public void Prev()
        {
            if (TrackNames.Length == 0)
                return;
            int i = CurrentIndex - 1;
            if (i < 0)
                i = TrackNames.Length - 1;
            PlayIndex(i, restart: true);
        }

        void PublishNowPlaying()
        {
            string title = CurrentIndex >= 0 && CurrentIndex < TrackNames.Length
                ? TrackNames[CurrentIndex]
                : "";
            int posMs = _music != null ? Mathf.RoundToInt(_music.time * 1000f) : 0;
            int durMs = _music != null && _music.clip != null
                ? Mathf.RoundToInt(_music.clip.length * 1000f)
                : 0;
            NativeBridgeFacade.PublishNowPlaying(
                IsPlaying, 0, CurrentIndex, title, "Island", posMs, durMs);
        }

        public void ApplySavedVolumes()
        {
            MusicVolume = LoadVolume(VolumeForm.MusicVolumeKey);
            EffectVolume = LoadVolume(VolumeForm.EffectVolumeKey);
            if (_music != null)
                _music.volume = MusicVolume;
        }

        public void SetMusicVolume(float value)
        {
            MusicVolume = Mathf.Clamp01(value);
            if (_music != null)
                _music.volume = MusicVolume;
        }

        public void SetEffectVolume(float value)
        {
            EffectVolume = Mathf.Clamp01(value);
        }

        public void PlayIndex(int index, bool restart = false)
        {
            if (TrackNames.Length == 0)
                return;

            index = Mathf.Clamp(index, 0, TrackNames.Length - 1);
            bool same = index == CurrentIndex;
            CurrentIndex = index;

            if (_clips == null || index >= _clips.Length || _clips[index] == null)
            {
                if (_music != null && _music.isPlaying && !same)
                    _music.Stop();
                return;
            }

            if (same && _music.isPlaying && !restart)
                return;

            _music.clip = _clips[index];
            _music.volume = MusicVolume;
            _music.Play();
            PublishNowPlaying();
        }

        void TryLoadClips()
        {
            AudioClip[] loaded = Resources.LoadAll<AudioClip>("Music");
            if (loaded == null || loaded.Length == 0)
            {
                _clips = System.Array.Empty<AudioClip>();
                return;
            }

            _clips = new AudioClip[TrackNames.Length];
            for (int i = 0; i < TrackNames.Length; i++)
            {
                for (int j = 0; j < loaded.Length; j++)
                {
                    if (loaded[j] != null && loaded[j].name == TrackNames[i])
                    {
                        _clips[i] = loaded[j];
                        break;
                    }
                }

                if (_clips[i] == null && i < loaded.Length)
                    _clips[i] = loaded[i];
            }
        }

        static float LoadVolume(string key)
        {
            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(key))
                return Mathf.Clamp01(GameEntry.Setting.GetFloat(key, 1f));
            return 1f;
        }
    }
}
