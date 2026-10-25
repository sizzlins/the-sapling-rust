using System;
using System.Threading;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Runs TimeLine.JumpAhead on a background thread so the Unity UI stays at
    /// 60+ FPS during Time Skips. Progress is reported via volatile counters;
    /// completion callback is dispatched on the main thread by polling
    /// CheckMainThreadCompletion() from Update().
    /// </summary>
    public static class AsyncJumpWorker
    {
        private static Thread _workerThread;
        private static volatile bool _isRunning = false;
        private static volatile int _daysRemaining = 0;
        private static volatile int _totalDays = 0;
        private static Action _onCompleteCallback;

        public static bool IsRunning => _isRunning;
        public static int DaysRemaining => _daysRemaining;
        public static int TotalDays => _totalDays;

        public static void StartJump(TimeLine timeLine, int totalDays, Action onComplete = null)
        {
            if (_isRunning || timeLine == null || totalDays <= 0) return;

            _isRunning = true;
            _totalDays = totalDays;
            _daysRemaining = totalDays;
            _onCompleteCallback = onComplete;

            _workerThread = new Thread(() =>
            {
                try
                {
                    while (_daysRemaining > 0 && _isRunning)
                    {
                        int batch = Math.Min(25, _daysRemaining);
                        timeLine.JumpAhead(batch);
                        _daysRemaining -= batch;
                    }
                }
                finally
                {
                    _isRunning = false;
                }
            })
            {
                IsBackground = true,
                Name = "SaplingAsyncJumpWorker"
            };

            _workerThread.Start();
        }

        public static void Stop()
        {
            _isRunning = false;
            _daysRemaining = 0;
        }

        /// <summary>
        /// Call from Unity main thread Update(). Fires the completion callback
        /// exactly once after the worker finishes, ensuring all post-jump
        /// Unity API calls (CleanUpAllOrganismViews, ShowToday) happen on
        /// the main thread.
        /// </summary>
        public static void CheckMainThreadCompletion()
        {
            if (!_isRunning && _onCompleteCallback != null)
            {
                var cb = _onCompleteCallback;
                _onCompleteCallback = null;
                cb.Invoke();
            }
        }
    }
}
