namespace Unosquare.FFME.Diagnostics
{
    using System;
    using System.Globalization;

    /// <summary>
    /// A snapshot of the <see cref="VideoPipelineStatistics"/> counters.
    /// </summary>
    public readonly struct VideoPipelineSnapshot
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VideoPipelineSnapshot"/> struct.
        /// </summary>
        /// <param name="decodedFrames">The decoded frames.</param>
        /// <param name="decodeTime">The time spent in decoder calls.</param>
        /// <param name="transferredFrames">The frames transferred from hardware.</param>
        /// <param name="transferTime">The time spent in hardware transfer calls.</param>
        /// <param name="convertedFrames">The converted frames.</param>
        /// <param name="convertTime">The conversion time.</param>
        /// <param name="presentedFrames">The frames written to the output bitmap.</param>
        /// <param name="presentWaitTime">The time spent waiting for the video dispatcher.</param>
        /// <param name="presentUiTime">The time spent on the video dispatcher.</param>
        /// <param name="presentCopyTime">The time spent copying pixels to the output bitmap.</param>
        /// <param name="staleFramesSkipped">The frames skipped because they were behind the audio clock.</param>
        public VideoPipelineSnapshot(
            long decodedFrames,
            TimeSpan decodeTime,
            long transferredFrames,
            TimeSpan transferTime,
            long convertedFrames,
            TimeSpan convertTime,
            long presentedFrames,
            TimeSpan presentWaitTime,
            TimeSpan presentUiTime,
            TimeSpan presentCopyTime,
            long staleFramesSkipped)
        {
            DecodedFrames = decodedFrames;
            DecodeTime = decodeTime;
            TransferredFrames = transferredFrames;
            TransferTime = transferTime;
            ConvertedFrames = convertedFrames;
            ConvertTime = convertTime;
            PresentedFrames = presentedFrames;
            PresentWaitTime = presentWaitTime;
            PresentUiTime = presentUiTime;
            PresentCopyTime = presentCopyTime;
            StaleFramesSkipped = staleFramesSkipped;
        }

        /// <summary>Gets the number of frames received from the decoder, including frames decoded while seeking.</summary>
        public long DecodedFrames { get; }

        /// <summary>Gets the time spent in decoder calls (send packet and receive frame).</summary>
        public TimeSpan DecodeTime { get; }

        /// <summary>Gets the number of frames transferred from hardware to system memory.</summary>
        public long TransferredFrames { get; }

        /// <summary>
        /// Gets the time spent in the hardware transfer calls. This includes waiting for the GPU
        /// to finish the frame, so it is not the copy time alone.
        /// </summary>
        public TimeSpan TransferTime { get; }

        /// <summary>Gets the number of frames converted to the output pixel format.</summary>
        public long ConvertedFrames { get; }

        /// <summary>Gets the time spent converting frames.</summary>
        public TimeSpan ConvertTime { get; }

        /// <summary>Gets the number of frames written to the output bitmap.</summary>
        public long PresentedFrames { get; }

        /// <summary>Gets the time the rendering thread waited for the video dispatcher.</summary>
        public TimeSpan PresentWaitTime { get; }

        /// <summary>
        /// Gets the time spent on the video dispatcher: preparing the bitmap, copying the pixels
        /// and handling the rendering event.
        /// </summary>
        public TimeSpan PresentUiTime { get; }

        /// <summary>Gets the time spent copying pixels to the output bitmap.</summary>
        public TimeSpan PresentCopyTime { get; }

        /// <summary>
        /// Gets the number of frames skipped because they were already behind the audio clock.
        /// Frames that the renderer passes over without selecting them are not counted. For the
        /// total number of dropped frames, compare <see cref="PresentedFrames"/> with the frame
        /// count expected for the elapsed time, which only applies to 1x playback without seeking.
        /// </summary>
        public long StaleFramesSkipped { get; }

        /// <summary>Gets a value indicating whether no frame or time was recorded for any pipeline stage.</summary>
        public bool IsEmpty =>
            DecodedFrames == 0 && TransferredFrames == 0 && ConvertedFrames == 0 &&
            PresentedFrames == 0 && StaleFramesSkipped == 0 &&
            DecodeTime == TimeSpan.Zero && TransferTime == TimeSpan.Zero && ConvertTime == TimeSpan.Zero &&
            PresentWaitTime == TimeSpan.Zero && PresentUiTime == TimeSpan.Zero && PresentCopyTime == TimeSpan.Zero;

        /// <inheritdoc />
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"decoded={DecodedFrames} ({PerFrame(DecodeTime, DecodedFrames):0.0} ms/frame) | " +
            $"hw transfer call={TransferredFrames} ({PerFrame(TransferTime, TransferredFrames):0.0} ms/frame) | " +
            $"convert={ConvertedFrames} ({PerFrame(ConvertTime, ConvertedFrames):0.0} ms/frame) | " +
            $"presented={PresentedFrames} (dispatcher wait {PerFrame(PresentWaitTime, PresentedFrames):0.0} ms, " +
            $"UI {PerFrame(PresentUiTime, PresentedFrames):0.0} ms incl. pixel copy {PerFrame(PresentCopyTime, PresentedFrames):0.0} ms per frame) | " +
            $"stale skipped={StaleFramesSkipped}");

        private static double PerFrame(TimeSpan total, long frames) =>
            frames > 0 ? total.TotalMilliseconds / frames : 0;
    }
}
