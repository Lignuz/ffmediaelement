namespace Unosquare.FFME.Primitives
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// A base class for implementing interval workers.
    /// Each worker runs its cycles on a dedicated thread with the given priority.
    /// </summary>
    /// <remarks>
    /// Cycles used to be dispatched from a shared timer to the thread pool. Under CPU load
    /// normal priority thread pool work can be delayed long enough for the decoded
    /// buffers to run dry, which pauses playback.
    /// The reading and decoding workers use the highest priority, like the rendering worker:
    /// Windows boosts the threads of the foreground process by up to two levels, so a
    /// CPU-bound foreground application can starve above normal priority threads.
    /// </remarks>
    internal abstract class IntervalWorkerBase : WorkerBase
    {
        private readonly Thread WorkerThread;
        private readonly ManualResetEventSlim WakeEvent = new(false);
        private long m_LastCycleStartTicks;
        private long m_LastCycleDurationTicks;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntervalWorkerBase"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="priority">The priority of the worker thread.</param>
        protected IntervalWorkerBase(string name, ThreadPriority priority = ThreadPriority.AboveNormal)
            : base(name)
        {
            WorkerThread = new Thread(RunWorkerThread)
            {
                IsBackground = true,
                Priority = priority,
                Name = name,
            };

            WorkerThread.Start();
        }

        /// <summary>
        /// Gets the UTC time at which the last cycle started. Used for diagnostics.
        /// </summary>
        public DateTime LastCycleStartTimeUtc =>
            new(Interlocked.Read(ref m_LastCycleStartTicks), DateTimeKind.Utc);

        /// <summary>
        /// Gets the duration of the last completed cycle. Used for diagnostics.
        /// </summary>
        public TimeSpan LastCycleDuration =>
            TimeSpan.FromTicks(Interlocked.Read(ref m_LastCycleDurationTicks));

        /// <inheritdoc />
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);

            // Wake the thread so it notices the disposal without waiting for the next interval.
            WakeEvent.Set();
            if (WorkerThread.IsAlive && !ReferenceEquals(WorkerThread, Thread.CurrentThread))
                WorkerThread.Join();

            if (!WorkerThread.IsAlive)
                WakeEvent.Dispose();
        }

        /// <summary>
        /// Runs a cycle every <see cref="Constants.DefaultTimingPeriod"/> until the worker is stopped or disposed.
        /// </summary>
        private void RunWorkerThread()
        {
            try
            {
                var cycleClock = new Stopwatch();
                while (!IsDisposed && !IsDisposing && WorkerState != WorkerState.Stopped)
                {
                    cycleClock.Restart();
                    if (TryBeginCycle())
                    {
                        Interlocked.Exchange(ref m_LastCycleStartTicks, DateTime.UtcNow.Ticks);
                        ExecuteCyle();
                        Interlocked.Exchange(ref m_LastCycleDurationTicks, cycleClock.Elapsed.Ticks);
                    }

                    var waitTime = Constants.DefaultTimingPeriod - cycleClock.Elapsed;
                    if (waitTime > TimeSpan.Zero && !IsDisposing && !IsDisposed)
                        WakeEvent.Wait(waitTime);
                }
            }
            catch (ObjectDisposedException)
            {
                // The worker has been disposed.
            }
        }
    }
}
