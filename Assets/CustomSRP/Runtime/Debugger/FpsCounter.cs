namespace CustomSRP.Debugger
{
    sealed class FpsCounter
    {
        float _interval;
        float _fps;
        float _ms;
        int _frames;
        float _accum;
        float _timeLeft;

        public FpsCounter(float updateInterval)
        {
            _interval = updateInterval > 0f ? updateInterval : 0.5f;
            Reset();
        }

        public float CurrentFps => _fps;

        public float CurrentMs => _ms;

        public void Update(float realElapseSeconds)
        {
            _frames++;
            _accum += realElapseSeconds;
            _timeLeft -= realElapseSeconds;
            if (_timeLeft > 0f)
            {
                return;
            }

            _fps = _accum > 0f ? _frames / _accum : 0f;
            _ms = _frames > 0 ? 1000f * _accum / _frames : 0f;
            _frames = 0;
            _accum = 0f;
            _timeLeft += _interval;
        }

        void Reset()
        {
            _fps = 0f;
            _ms = 0f;
            _frames = 0;
            _accum = 0f;
            _timeLeft = 0f;
        }
    }
}
