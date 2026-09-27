namespace Unosquare.FFME.Engine
{
    using Common;
    using Container;
    using Diagnostics;
    using FFmpeg.AutoGen;
    using Primitives;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Implement frame decoding worker logic.
    /// </summary>
    /// <seealso cref="IMediaWorker" />
    internal sealed class FrameDecodingWorker : IntervalWorkerBase, IMediaWorker, ILoggingSource
    {
        private readonly Action<IEnumerable<MediaType>, CancellationToken> SerialDecodeBlocks;
        private readonly Action<IEnumerable<MediaType>, CancellationToken> ParallelDecodeBlocks;

        /// <summary>
        /// Held by the audio worker while it decodes, so that the audio packet and codec state
        /// can be inspected from the main decoding worker without seeing a half-updated state.
        /// </summary>
        private readonly object AudioCycleLock = new();

        /// <summary>
        /// The decoded frame count for a cycle. This is used to detect end of decoding scenarios.
        /// </summary>
        private int DecodedFrameCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameDecodingWorker"/> class.
        /// </summary>
        /// <param name="mediaCore">The media core.</param>
        /// <param name="isAudioWorker">Whether this worker only decodes audio for media that also has video.</param>
        public FrameDecodingWorker(MediaEngine mediaCore, bool isAudioWorker)
            : base(isAudioWorker ? "AudioFrameDecodingWorker" : nameof(FrameDecodingWorker), ThreadPriority.Highest)
        {
            MediaCore = mediaCore;
            Container = mediaCore.Container;
            State = mediaCore.State;
            IsAudioWorker = isAudioWorker;

            ParallelDecodeBlocks = (all, ct) =>
            {
                Parallel.ForEach(all, (t) =>
                    Interlocked.Add(ref DecodedFrameCount,
                    DecodeComponentBlocks(t, ct)));
            };

            SerialDecodeBlocks = (all, ct) =>
            {
                // A video frame can take long to decode (high resolutions, hybrid hardware
                // decoders, or decoder helper threads starved by a CPU-bound foreground
                // application). Audio is then decoded by the dedicated audio worker.
                var skipAudio = IsAudioDecodedSeparately;
                foreach (var t in Container.Components.MediaTypes)
                {
                    if (skipAudio && t == MediaType.Audio)
                        continue;

                    DecodedFrameCount += DecodeComponentBlocks(t, ct);
                }
            };

            if (isAudioWorker)
                return;

            Container.Components.OnFrameDecoded = (frame, type) =>
            {
                unsafe
                {
                    if (type == MediaType.Audio)
                        MediaCore.Connector?.OnAudioFrameDecoded((AVFrame*)frame.ToPointer(), Container.InputContext);
                    else if (type == MediaType.Video)
                        MediaCore.Connector?.OnVideoFrameDecoded((AVFrame*)frame.ToPointer(), Container.InputContext);
                }
            };

            Container.Components.OnSubtitleDecoded = (subtitle) =>
            {
                unsafe
                {
                    MediaCore.Connector?.OnSubtitleDecoded((AVSubtitle*)subtitle.ToPointer(), Container.InputContext);
                }
            };
        }

        /// <inheritdoc />
        public MediaEngine MediaCore { get; }

        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => MediaCore;

        /// <summary>
        /// Gets the Media Engine's Container.
        /// </summary>
        private MediaContainer Container { get; }

        /// <summary>
        /// Gets the Media Engine's State.
        /// </summary>
        private MediaEngineState State { get; }

        /// <summary>
        /// Gets a value indicating whether parallel decoding is enabled.
        /// </summary>
        private bool UseParallelDecoding => MediaCore.Timing.HasDisconnectedClocks || Container.MediaOptions.UseParallelDecoding;

        /// <summary>
        /// Gets a value indicating whether this worker only decodes audio.
        /// </summary>
        private bool IsAudioWorker { get; }

        /// <summary>
        /// Gets a value indicating whether audio is decoded by the dedicated audio worker.
        /// </summary>
        private bool IsAudioDecodedSeparately =>
            !UseParallelDecoding && Container.Components.HasAudio && Container.Components.HasVideo;

        /// <summary>
        /// Determines whether all the audio has been decoded into blocks by this audio worker.
        /// A packet leaves the queue before the codec is marked as holding it, so this returns
        /// false while a decoding cycle is in progress instead of reading that state mid-update.
        /// </summary>
        /// <returns>True if no more audio frames can be decoded.</returns>
        internal bool HasDecodedAllAudio()
        {
            if (!Monitor.TryEnter(AudioCycleLock))
                return false;

            try
            {
                return !CanReadMoreFramesOf(MediaType.Audio);
            }
            finally
            {
                Monitor.Exit(AudioCycleLock);
            }
        }

        /// <inheritdoc />
        protected override void ExecuteCycleLogic(CancellationToken ct)
        {
            if (IsAudioWorker)
            {
                lock (AudioCycleLock)
                {
                    if (IsAudioDecodedSeparately && !MediaCore.HasDecodingEnded && !ct.IsCancellationRequested)
                        DecodeComponentBlocks(MediaType.Audio, ct);
                }

                return;
            }

            try
            {
                if (MediaCore.HasDecodingEnded || ct.IsCancellationRequested)
                    return;

                // Call the frame decoding logic
                DecodedFrameCount = 0;
                if (UseParallelDecoding)
                    ParallelDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
                else
                    SerialDecodeBlocks.Invoke(Container.Components.MediaTypes, ct);
            }
            finally
            {
                // Provide updates to decoding stats -- don't count attached pictures
                var hasAttachedPictures = Container.Components.Video?.IsStillPictures ?? false;
                State.UpdateDecodingStats(MediaCore.Blocks.Values
                    .Sum(b => b.MediaType == MediaType.Video && hasAttachedPictures ? 0 : b.RangeBitRate));

                // Detect End of Decoding Scenarios
                // The Rendering will check for end of media when this condition is set.
                MediaCore.HasDecodingEnded = DetectHasDecodingEnded();
            }
        }

        /// <inheritdoc />
        protected override void OnCycleException(Exception ex) =>
            this.LogError(Aspects.DecodingWorker, "Worker Cycle exception thrown", ex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeComponentBlocks(MediaType t, CancellationToken ct)
        {
            var decoderBlocks = MediaCore.Blocks[t]; // the blocks reference
            var addedBlocks = 0; // the number of blocks that have been added
            var maxAddedBlocks = decoderBlocks.Capacity; // the max blocks to add for this cycle

            while (addedBlocks < maxAddedBlocks)
            {
                var position = MediaCore.Timing.GetPosition(t).Ticks;
                var rangeHalf = decoderBlocks.RangeMidTime.Ticks;

                // We break decoding if we have a full set of blocks and if the
                // clock is not past the first half of the available block range
                if (decoderBlocks.IsFull && position < rangeHalf)
                    break;

                // Try adding the next block. Stop decoding upon failure or cancellation
                if (ct.IsCancellationRequested || AddNextBlock(t) == false)
                    break;

                // At this point we notify that we have added the block
                addedBlocks++;
            }

            return addedBlocks;
        }

        /// <summary>
        /// Tries to receive the next frame from the decoder by decoding queued
        /// Packets and converting the decoded frame into a Media Block which gets
        /// queued into the playback block buffer.
        /// </summary>
        /// <param name="t">The MediaType.</param>
        /// <returns>True if a block could be added. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AddNextBlock(MediaType t)
        {
            // Decode the frames
            var block = MediaCore.Blocks[t].Add(Container.Components[t].ReceiveNextFrame(), Container);
            return block != null;
        }

        /// <summary>
        /// Detects the end of media in the decoding worker.
        /// </summary>
        /// <returns>True if media docding has ended.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DetectHasDecodingEnded()
        {
            if (DecodedFrameCount > 0 || CanReadMoreFramesOf(Container.Components.SeekableMediaType))
                return false;

            // The remaining audio is decoded by the audio worker; wait until it is done too.
            return !IsAudioDecodedSeparately || MediaCore.Workers?.AudioDecoding.HasDecodedAllAudio() == true;
        }

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded into blocks of the given type.
        /// </summary>
        /// <param name="t">The media type.</param>
        /// <returns>
        ///   <c>true</c> if more frames can be decoded; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanReadMoreFramesOf(MediaType t)
        {
            // Count packets rather than bytes: the empty packet queued at the end of the stream
            // has no data, but it still has to reach the decoder to drain its remaining frames.
            return
                Container.Components[t].BufferCount > 0 ||
                Container.Components[t].HasPacketsInCodec ||
                MediaCore.ShouldReadMorePackets;
        }
    }
}
