namespace Unosquare.FFME.Rendering.Wave
{
    using Primitives;
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// A worker that feeds the audio device from a dedicated thread registered with the
    /// Multimedia Class Scheduler Service (MMCSS).
    /// </summary>
    /// <remarks>
    /// Interval workers run their cycles on thread pool threads with normal priority.
    /// Under CPU load those cycles can start too late to refill the device buffer,
    /// which makes the device replay stale samples (audible glitches).
    /// </remarks>
    internal abstract class AudioWorkerBase : WorkerBase
    {
        private const string MmcssTaskName = "Pro Audio";
        private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(5);
        private static readonly TimeSpan MinimumCycleDuration = TimeSpan.FromMilliseconds(1);

        private readonly Thread WorkerThread;
        private int m_MmcssRegistered;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioWorkerBase"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        protected AudioWorkerBase(string name)
            : base(name)
        {
            WorkerThread = new Thread(RunWorkerThread)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = name,
            };

            WorkerThread.Start();
        }

        protected bool IsMmcssRegistered => Volatile.Read(ref m_MmcssRegistered) != 0;

        protected bool IsWorkerThread => ReferenceEquals(WorkerThread, Thread.CurrentThread);

        /// <inheritdoc />
        protected override void Dispose(bool alsoManaged)
        {
            base.Dispose(alsoManaged);

            // Make sure the device is no longer fed before derived classes release it.
            if (WorkerThread.IsAlive && !ReferenceEquals(WorkerThread, Thread.CurrentThread))
                WorkerThread.Join();
        }

        /// <summary>
        /// Runs the worker cycles until the worker is stopped or disposed.
        /// </summary>
        private void RunWorkerThread()
        {
            var taskIndex = 0u;
            var mmcssHandle = NativeMethods.AvSetMmThreadCharacteristics(MmcssTaskName, ref taskIndex);
            Volatile.Write(ref m_MmcssRegistered, mmcssHandle == IntPtr.Zero ? 0 : 1);

            try
            {
                var cycleClock = new Stopwatch();
                while (!IsDisposed && !IsDisposing && WorkerState != WorkerState.Stopped)
                {
                    if (!TryBeginCycle())
                    {
                        Thread.Sleep(IdleWait);
                        continue;
                    }

                    cycleClock.Restart();
                    ExecuteCyle();

                    // Device waits pace the running cycles. Avoid spinning while paused
                    // or when a cycle returns immediately (e.g. repeated faults).
                    if (WorkerState != WorkerState.Running)
                        Thread.Sleep(IdleWait);
                    else if (cycleClock.Elapsed < MinimumCycleDuration)
                        Thread.Sleep(1);
                }
            }
            catch (ObjectDisposedException)
            {
                // The worker has been disposed.
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                    NativeMethods.AvRevertMmThreadCharacteristics(mmcssHandle);
            }
        }

        private static class NativeMethods
        {
            [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
            public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

            [DllImport("avrt.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
        }
    }
}
