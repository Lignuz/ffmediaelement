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
        /// <summary>
        /// How far the video packets must have been read past the last audio packet, with every read
        /// audio packet decoded, before audio can be treated as temporarily finished. This is a heuristic
        /// based on demuxers returning interleaved packets near their presentation times.
        /// </summary>
        private static readonly TimeSpan AudioStreamEndReadAhead = TimeSpan.FromSeconds(3);

        private readonly Action<IEnumerable<MediaType>, CancellationToken> SerialDecodeBlocks;
        private readonly Action<IEnumerable<MediaType>, CancellationToken> ParallelDecodeBlocks;

        /// <summary>
        /// Held by the audio worker while it decodes, so that the audio packet and codec state
        /// can be inspected from the main decoding worker without seeing a half-updated state.
        /// </summary>
        private readonly object AudioCycleLock = new();

        /// <summary>
        /// Packet queue callback owned by the main decoding worker to invalidate a reported audio end.
        /// </summary>
        private readonly Action<bool> AudioPacketSequenceHandler;

        /// <summary>
        /// The decoded frame count for a cycle. This is used to detect end of decoding scenarios.
        /// </summary>
        private int DecodedFrameCount;

        /// <summary>
        /// The end time of the last audio packet when the audio stream was found to have ended.
        /// </summary>
        private TimeSpan AudioEndPacketTime = TimeSpan.MinValue;

        /// <summary>
        /// Signals that a data packet arrived after an audio end was reported.
        /// </summary>
        private int AudioResumePending;

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

            AudioPacketSequenceHandler = isReset =>
            {
                if (isReset)
                {
                    MediaCore.HasAudioDecodingEnded = false;
                    Interlocked.Exchange(ref AudioResumePending, 0);
                }
                else if (MediaCore.HasAudioDecodingEnded)
                {
                    // This callback runs under the component's drain lock. Publishing an audio
                    // end uses the same lock, so a later packet always invalidates that end.
                    MediaCore.HasAudioDecodingEnded = false;
                    Interlocked.Exchange(ref AudioResumePending, 1);
                }
            };

            if (Container.Components.Audio is { } audio)
                audio.OnPacketSequenceChanged = AudioPacketSequenceHandler;

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
        internal bool HasDecodedAllAudio() => InspectDecodedAudio(true);

        /// <summary>
        /// Determines whether the audio packets read so far were all decoded into blocks by this audio
        /// worker, regardless of whether more packets can still be read. Like <see cref="HasDecodedAllAudio"/>,
        /// this returns false while a decoding cycle is in progress.
        /// </summary>
        /// <returns>True if no read audio packet is waiting to be decoded.</returns>
        internal bool HasDecodedQueuedAudio() => InspectDecodedAudio(false);

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

                // A newly queued audio packet invalidates an earlier end under the component lock.
                // Components are recreated when the selected streams change, so attach it again.
                var audio = Container.Components.Audio;
                if (audio != null && audio.OnPacketSequenceChanged == null)
                    audio.OnPacketSequenceChanged = AudioPacketSequenceHandler;
                if (Interlocked.Exchange(ref AudioResumePending, 0) != 0)
                {
                    this.LogInfo(Aspects.DecodingWorker,
                        $"AUDIO RESUMED: audio packets arrived again after {FormatAudioTime(AudioEndPacketTime)} | " +
                        $"position={MediaCore.PlaybackPosition.Format()}");
                }

                if (!MediaCore.HasAudioDecodingEnded && DetectHasAudioDecodingEnded())
                {
                    this.LogInfo(Aspects.DecodingWorker,
                        $"AUDIO END: no more audio {(Container.IsAtEndOfStream ? "at the end of the file" : "while the video continues")} | " +
                        $"last audio packet={FormatAudioTime(AudioEndPacketTime)} | position={MediaCore.PlaybackPosition.Format()}");
                }
            }
        }

        /// <inheritdoc />
        protected override void OnCycleException(Exception ex) =>
            this.LogError(Aspects.DecodingWorker, "Worker Cycle exception thrown", ex);

        /// <summary>
        /// Formats the end time of the last audio packet for logging.
        /// </summary>
        /// <param name="time">The packet end time.</param>
        /// <returns>The formatted time.</returns>
        private static string FormatAudioTime(TimeSpan time) =>
            time == TimeSpan.MinValue ? "none since seek" : time.Format();

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
        /// Detects whether the audio stream was read to its end and all of its frames were decoded.
        /// </summary>
        /// <returns>True if no more audio frames will be decoded.</returns>
        private bool DetectHasAudioDecodingEnded()
        {
            var audio = Container.Components.Audio;
            if (audio == null)
                return false;

            // The audio ends with the file, or its packets stopped while the video packets continue.
            // The latter does not wait for the rest of the file to be read.
            if (!Container.IsAtEndOfStream && !HaveAudioPacketsStopped(audio))
                return false;

            var hasDecodedQueuedAudio = IsAudioDecodedSeparately
                ? MediaCore.Workers?.AudioDecoding.HasDecodedQueuedAudio() == true
                : audio.BufferCount <= 0 && !audio.HasPacketsInCodec;

            if (!hasDecodedQueuedAudio)
                return false;

            // The decoder can hold back frames until it receives an empty packet. Enqueueing new
            // audio and publishing a completed drain share one lock, so they have a definite order.
            if (audio.TryMarkDrainCompleted(endTime =>
            {
                AudioEndPacketTime = endTime;
                MediaCore.HasAudioDecodingEnded = true;
            }))
                return true;

            // The container queues a drain packet at EOF. Before EOF, request one only when the
            // queue is idle; an arriving packet must be decoded before a new drain request.
            audio.RequestDrainIfIdle();
            return false;
        }

        /// <summary>
        /// Determines whether the video packets were read far past the last audio packet.
        /// </summary>
        /// <param name="audio">The audio component.</param>
        /// <returns>True if no audio packets arrived while the video was read ahead.</returns>
        private bool HaveAudioPacketsStopped(MediaComponent audio)
        {
            var video = Container.Components.Video;
            if (video == null || video.IsStillPictures || video.LastPacketEndTime == TimeSpan.MinValue)
                return false;

            // Without audio packets since the stream was repositioned, measure from the playback position,
            // but not before the audio stream starts.
            var audioReadEndTime = audio.LastPacketEndTime;
            if (audioReadEndTime == TimeSpan.MinValue)
            {
                audioReadEndTime = MediaCore.PlaybackPosition;
                if (audio.StartTime != TimeSpan.MinValue && audio.StartTime > audioReadEndTime)
                    audioReadEndTime = audio.StartTime;
            }

            return video.LastPacketEndTime - audioReadEndTime >= AudioStreamEndReadAhead;
        }

        /// <summary>
        /// Inspects the audio packet and codec state when the audio worker is not in a decoding cycle.
        /// </summary>
        /// <param name="includeUnreadPackets">Whether packets that can still be read count as pending audio.</param>
        /// <returns>True if no audio is waiting to be decoded; false while a cycle is in progress.</returns>
        private bool InspectDecodedAudio(bool includeUnreadPackets)
        {
            if (!Monitor.TryEnter(AudioCycleLock))
                return false;

            try
            {
                var audio = Container.Components.Audio;
                return includeUnreadPackets
                    ? !CanReadMoreFramesOf(MediaType.Audio)
                    : audio != null && audio.BufferCount <= 0 && !audio.HasPacketsInCodec;
            }
            finally
            {
                Monitor.Exit(AudioCycleLock);
            }
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
