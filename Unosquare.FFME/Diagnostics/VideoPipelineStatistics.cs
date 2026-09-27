namespace Unosquare.FFME.Diagnostics
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// Process-wide timing counters for the video pipeline stages. They show where frames are
    /// delayed: decoding, hardware frame transfer, pixel format conversion and presentation.
    /// </summary>
    public static class VideoPipelineStatistics
    {
        private static long s_DecodeTicks;
        private static long s_DecodedFrames;
        private static long s_TransferTicks;
        private static long s_TransferredFrames;
        private static long s_ConvertTicks;
        private static long s_ConvertedFrames;
        private static long s_PresentWaitTicks;
        private static long s_PresentUiTicks;
        private static long s_PresentCopyTicks;
        private static long s_PresentedFrames;
        private static long s_StaleFramesSkipped;

        /// <summary>
        /// Captures the current counter values.
        /// </summary>
        /// <returns>A snapshot of the counters.</returns>
        public static VideoPipelineSnapshot Capture() => new(
            Interlocked.Read(ref s_DecodedFrames),
            ToTimeSpan(Interlocked.Read(ref s_DecodeTicks)),
            Interlocked.Read(ref s_TransferredFrames),
            ToTimeSpan(Interlocked.Read(ref s_TransferTicks)),
            Interlocked.Read(ref s_ConvertedFrames),
            ToTimeSpan(Interlocked.Read(ref s_ConvertTicks)),
            Interlocked.Read(ref s_PresentedFrames),
            ToTimeSpan(Interlocked.Read(ref s_PresentWaitTicks)),
            ToTimeSpan(Interlocked.Read(ref s_PresentUiTicks)),
            ToTimeSpan(Interlocked.Read(ref s_PresentCopyTicks)),
            Interlocked.Read(ref s_StaleFramesSkipped));

        /// <summary>
        /// Captures the current counter values and resets them. Each counter is swapped
        /// atomically, so nothing counted while capturing is lost; a frame that is still
        /// moving through the pipeline may be split between two consecutive snapshots.
        /// </summary>
        /// <returns>A snapshot of the counters before they were reset.</returns>
        public static VideoPipelineSnapshot CaptureAndReset() => new(
            Interlocked.Exchange(ref s_DecodedFrames, 0),
            ToTimeSpan(Interlocked.Exchange(ref s_DecodeTicks, 0)),
            Interlocked.Exchange(ref s_TransferredFrames, 0),
            ToTimeSpan(Interlocked.Exchange(ref s_TransferTicks, 0)),
            Interlocked.Exchange(ref s_ConvertedFrames, 0),
            ToTimeSpan(Interlocked.Exchange(ref s_ConvertTicks, 0)),
            Interlocked.Exchange(ref s_PresentedFrames, 0),
            ToTimeSpan(Interlocked.Exchange(ref s_PresentWaitTicks, 0)),
            ToTimeSpan(Interlocked.Exchange(ref s_PresentUiTicks, 0)),
            ToTimeSpan(Interlocked.Exchange(ref s_PresentCopyTicks, 0)),
            Interlocked.Exchange(ref s_StaleFramesSkipped, 0));

        /// <summary>
        /// Resets all counters.
        /// </summary>
        public static void Reset() => CaptureAndReset();

        /// <summary>Adds time spent in the video decoder calls.</summary>
        /// <param name="stopwatchTicks">The elapsed <see cref="Stopwatch"/> ticks.</param>
        internal static void AddDecodeTime(long stopwatchTicks) => Interlocked.Add(ref s_DecodeTicks, stopwatchTicks);

        /// <summary>Counts a frame received from the video decoder.</summary>
        internal static void AddDecodedFrame() => Interlocked.Increment(ref s_DecodedFrames);

        /// <summary>Adds a call that transferred a hardware frame to system memory.</summary>
        /// <param name="stopwatchTicks">The elapsed <see cref="Stopwatch"/> ticks.</param>
        internal static void AddTransfer(long stopwatchTicks)
        {
            Interlocked.Add(ref s_TransferTicks, stopwatchTicks);
            Interlocked.Increment(ref s_TransferredFrames);
        }

        /// <summary>Adds a pixel format conversion to the output format.</summary>
        /// <param name="stopwatchTicks">The elapsed <see cref="Stopwatch"/> ticks.</param>
        internal static void AddConversion(long stopwatchTicks)
        {
            Interlocked.Add(ref s_ConvertTicks, stopwatchTicks);
            Interlocked.Increment(ref s_ConvertedFrames);
        }

        /// <summary>Adds a frame that was written to the output bitmap.</summary>
        /// <param name="waitTicks">The <see cref="Stopwatch"/> ticks spent waiting for the video dispatcher.</param>
        /// <param name="uiTicks">The <see cref="Stopwatch"/> ticks spent on the video dispatcher, including the rendering event.</param>
        /// <param name="copyTicks">The <see cref="Stopwatch"/> ticks spent copying the pixels.</param>
        internal static void AddPresentation(long waitTicks, long uiTicks, long copyTicks)
        {
            Interlocked.Add(ref s_PresentWaitTicks, waitTicks);
            Interlocked.Add(ref s_PresentUiTicks, uiTicks);
            Interlocked.Add(ref s_PresentCopyTicks, copyTicks);
            Interlocked.Increment(ref s_PresentedFrames);
        }

        /// <summary>Counts a frame that was not shown because it was already behind the audio clock.</summary>
        internal static void AddStaleFrameSkipped() => Interlocked.Increment(ref s_StaleFramesSkipped);

        private static TimeSpan ToTimeSpan(long stopwatchTicks) =>
            TimeSpan.FromSeconds(stopwatchTicks / (double)Stopwatch.Frequency);
    }
}
