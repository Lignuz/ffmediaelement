namespace Unosquare.FFME.Engine
{
    /// <summary>
    /// Defines the different worker types.
    /// </summary>
    internal enum MediaWorkerType
    {
        /// <summary>
        /// The packet reading worker.
        /// </summary>
        Read,

        /// <summary>
        /// The frame decoding worker.
        /// </summary>
        Decode,

        /// <summary>
        /// The block rendering worker.
        /// </summary>
        Render,

        /// <summary>
        /// The audio decoding worker used when the media has both audio and video.
        /// </summary>
        AudioDecode,
    }
}
