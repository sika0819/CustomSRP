using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Shared 0–1 content-audio duck for preset iris.
    /// Native <c>StopVoiceWithFade</c> is optional/stubbed on some platforms; hard Stop/Play and
    /// unsmoothed AttenuationGain zipper are audible. Duck to silence <b>before</b> Clear/Retarget,
    /// then fade back on reveal — C#-side root fix that does not depend on native fade.
    /// </summary>
    public sealed class ContentAudioDuck
    {
        readonly IslandAudioSession _session;

        public ContentAudioDuck(IslandAudioSession session)
        {
            _session = session;
        }

        const float DefaultSilentSeconds = 1.5f;
        const float DefaultFullSeconds = 0.85f;

        float _from = 1f;
        float _to = 1f;
        float _startTime;
        float _duration;
        int _driveGen;

        /// <summary>1 = full loudness, 0 = silence.</summary>
        public float Level01
        {
            get
            {
                if (_duration <= 1e-4f)
                    return _to;
                float t = Mathf.Clamp01((Time.realtimeSinceStartup - _startTime) / _duration);
                return Mathf.Lerp(_from, _to, t);
            }
        }

        public bool IsSilent => Level01 <= 0.001f;

        public void BeginSilent(float seconds = DefaultSilentSeconds)
        {
            StartRamp(0f, Mathf.Max(0.05f, seconds));
        }

        public void BeginFull(float seconds = DefaultFullSeconds)
        {
            StartRamp(1f, Mathf.Max(0.05f, seconds));
        }

        public void SnapFull()
        {
            _from = _to = 1f;
            _duration = 0f;
            _driveGen++;
            PushNow();
        }

        public void SnapSilent()
        {
            _from = _to = 0f;
            _duration = 0f;
            _driveGen++;
            PushNow();
        }

        /// <summary>停掉渐变协程，不再推动引擎。会话销毁时调用，避免异步把已毁会话留住。</summary>
        public void CancelDrive()
        {
            _driveGen++;
        }

        void StartRamp(float target, float seconds)
        {
            _from = Level01;
            _to = target;
            _startTime = Time.realtimeSinceStartup;
            _duration = seconds;
            int gen = ++_driveGen;
            Drive(gen).Forget();
        }

        async UniTaskVoid Drive(int gen)
        {
            // Frame-rate push: ambience Evaluate is 4 Hz and skips when listener is still /
            // preset-held — duck would otherwise staircase or stall mid-iris.
            while (gen == _driveGen)
            {
                PushNow();
                if (_duration <= 1e-4f
                    || Time.realtimeSinceStartup >= _startTime + _duration)
                {
                    _from = _to;
                    _duration = 0f;
                    PushNow();
                    return;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
            }
        }

        void PushNow()
        {
            GameEntry.Music?.PushVolumesToEngine();
            _session.Weather?.ReapplyDuckedRtpcs();
            _session.Ambience?.ApplyDuckToPlayingEmitters();
        }
    }
}
