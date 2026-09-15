namespace Unosquare.FFME.Platform
{
    using System;

    /// <summary>
    /// Provides the current position of the audio output path.
    /// </summary>
    internal interface IAudioClockSource
    {
        /// <summary>
        /// Gets the expected audio output latency in milliseconds.
        /// </summary>
        int PlaybackLatencyMilliseconds { get; }

        /// <summary>
        /// Tries to get the position currently represented by the audio output buffer.
        /// </summary>
        /// <param name="position">The audio output position.</param>
        /// <returns><c>true</c> when the position is available; otherwise, <c>false</c>.</returns>
        bool TryGetPlaybackPosition(out TimeSpan position);
    }
}
