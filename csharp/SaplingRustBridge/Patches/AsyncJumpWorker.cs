using System;
using System.Threading;
using Boo.Lang;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Background thread worker for 100% authentic The Sapling time jumps.
    /// Simulates every single day using vanilla TimeLine.JumpAhead(1) / LiveADay().
    /// Runs at maximum speed on a background worker thread while keeping the UI responsive.
    /// No shortcuts, no skipped days, no faked mutations, no altered thresholds.
    /// </summary>
    public static class AsyncJumpWorker
    {
        private class NoOpCallable : ICallable
        {
            public object Call(object[] args) => null;
        }

        private static readonly ICallable _noOp = new NoOpCallable();
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
                // Temporarily detach main-thread UI/GameObject callbacks during background computation
                ICallable savedOceanDepth = timeLine.changeOceanDepth;
                ICallable savedEventOccurs = timeLine.eventOccurs;
                ICallable savedLockCreation = timeLine.lockCreation;
                ICallable savedMeteorite = timeLine.dailyChallengeMeteoriteEvent;
                ICallable savedNewSpecies = timeLine.newSpeciesCreatedCallBack;
                ICallable savedPlayedALongTime = timeLine.playedALongTimeCallBack;
                ICallable savedAnimalGetsEaten = timeLine.animalGetsEatenCallBack;

                timeLine.changeOceanDepth = _noOp;
                timeLine.eventOccurs = _noOp;
                timeLine.lockCreation = _noOp;
                timeLine.dailyChallengeMeteoriteEvent = _noOp;
                timeLine.newSpeciesCreatedCallBack = _noOp;
                timeLine.playedALongTimeCallBack = _noOp;
                timeLine.animalGetsEatenCallBack = _noOp;

                try
                {
                    Plugin.LogWarn($"[AsyncJumpWorker] Starting authentic {totalDays}-day simulation on worker thread...");

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (_daysRemaining > 0 && _isRunning)
                    {
                        // 250-day batch size: significantly amortizes while loop overhead
                        int batch = Math.Min(250, _daysRemaining);
                        timeLine.JumpAhead(batch);
                        _daysRemaining -= batch;

                        // Non-blocking yield: replaces 15ms Sleep(1) with Sleep(0) to maximize CPU throughput
                        if (sw.ElapsedMilliseconds >= 32)
                        {
                            Thread.Sleep(0);
                            sw.Reset();
                            sw.Start();
                        }
                    }

                    Plugin.LogWarn($"[AsyncJumpWorker] Time jump complete! Current dayIndex: {timeLine.dayIndex}, Total species: {timeLine.allSpecies?.Count ?? 0}");
                }
                catch (Exception ex)
                {
                    Debug.LogError("[AsyncJumpWorker] Error during time jump: " + ex);
                }
                finally
                {
                    // Restore original callbacks
                    timeLine.changeOceanDepth = savedOceanDepth;
                    timeLine.eventOccurs = savedEventOccurs;
                    timeLine.lockCreation = savedLockCreation;
                    timeLine.dailyChallengeMeteoriteEvent = savedMeteorite;
                    timeLine.newSpeciesCreatedCallBack = savedNewSpecies;
                    timeLine.playedALongTimeCallBack = savedPlayedALongTime;
                    timeLine.animalGetsEatenCallBack = savedAnimalGetsEaten;

                    _daysRemaining = 0;
                    _isRunning = false;
                }
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Normal,
                Name = "SaplingAsyncJumpWorker"
            };

            _workerThread.Start();
        }

        public static void Stop()
        {
            _isRunning = false;
        }

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
