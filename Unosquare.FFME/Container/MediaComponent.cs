namespace Unosquare.FFME.Container
{
    using Common;
    using Diagnostics;
    using FFmpeg.AutoGen;
    using Primitives;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Represents a media component of a given media type within a
    /// media container. Derived classes must implement frame handling
    /// logic.
    /// </summary>
    /// <seealso cref="IDisposable" />
    internal abstract unsafe class MediaComponent : IDisposable, ILoggingSource
    {
        #region Private Declarations

        /// <summary>
        /// Related to issue 94, looks like FFmpeg requires exclusive access when calling avcodec_open2().
        /// </summary>
        private static readonly object CodecLock = new();

        /// <summary>
        /// The logging handler.
        /// </summary>
        private readonly ILoggingHandler m_LoggingHandler;

        /// <summary>
        /// Contains the packets pending to be sent to the decoder.
        /// </summary>
        private readonly PacketQueue Packets = new();

        /// <summary>
        /// Keeps drain state changes ordered with packet queue changes across the reading and decoding workers.
        /// </summary>
        private readonly object m_DrainStateLock = new();

        /// <summary>
        /// Associates queued empty packets with the request that produced each one.
        /// </summary>
        private readonly Queue<long> m_QueuedDrainRequestIds = new();

        /// <summary>
        /// The decode packet function.
        /// </summary>
        private readonly Func<MediaFrame> DecodePacketFunction;

        /// <summary>
        /// Detects redundant, unmanaged calls to the Dispose method.
        /// </summary>
        private readonly AtomicBoolean m_IsDisposed = new(false);

        /// <summary>
        /// Determines if packets have been fed into the codec and frames can be decoded.
        /// </summary>
        private readonly AtomicBoolean m_HasCodecPackets = new(false);

        /// <summary>
        /// Holds the end time of the latest packet read into the queue.
        /// </summary>
        private readonly AtomicTimeSpan m_LastPacketEndTime = new(TimeSpan.MinValue);

        /// <summary>
        /// Holds whether an empty packet was queued after the last data packet.
        /// </summary>
        private readonly AtomicBoolean m_HasDrainRequest = new(false);

        /// <summary>
        /// Holds whether the decoder reported EOF after a drain request was queued.
        /// </summary>
        private readonly AtomicBoolean m_HasDrainCompleted = new(false);

        /// <summary>
        /// Holds a reference to the associated input context stream.
        /// </summary>
        private readonly IntPtr m_Stream;

        /// <summary>
        /// Monotonic ID assigned to each queued drain packet.
        /// </summary>
        private long m_NextDrainRequestId;

        /// <summary>
        /// ID of the latest queued drain request.
        /// </summary>
        private long m_LatestDrainRequestId;

        /// <summary>
        /// ID of the drain packet accepted by the codec.
        /// </summary>
        private long m_AcceptedDrainRequestId;

        /// <summary>
        /// Holds a reference to the Codec Context.
        /// </summary>
        private IntPtr m_CodecContext;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        /// <exception cref="ArgumentNullException">container.</exception>
        /// <exception cref="MediaContainerException">The container exception.</exception>
        protected MediaComponent(MediaContainer container, int streamIndex)
        {
            // Ported from: https://github.com/FFmpeg/FFmpeg/blob/master/fftools/ffplay.c#L2559
            Container = container ?? throw new ArgumentNullException(nameof(container));
            m_LoggingHandler = ((ILoggingSource)Container).LoggingHandler;
            m_CodecContext = new IntPtr(ffmpeg.avcodec_alloc_context3(null));
            RC.Current.Add(CodecContext);
            StreamIndex = streamIndex;
            m_Stream = new IntPtr(container.InputContext->streams[streamIndex]);
            StreamInfo = container.MediaInfo.Streams[streamIndex];

#pragma warning disable CS0618 // Type or member is obsolete

            // Set default codec context options from probed stream
            var setCodecParamsResult = ffmpeg.avcodec_parameters_to_context(CodecContext, Stream->codecpar);

#pragma warning restore CS0618 // Type or member is obsolete

            if (setCodecParamsResult < 0)
            {
                this.LogWarning(Aspects.Component,
                    $"Could not set codec parameters. Error code: {setCodecParamsResult}");
            }

            // We set the packet timebase in the same timebase as the stream as opposed to the typical AV_TIME_BASE
            if (this is VideoComponent && Container.MediaOptions.VideoForcedFps > 0)
            {
                var fpsRational = ffmpeg.av_d2q(Container.MediaOptions.VideoForcedFps, 1000000);
                Stream->r_frame_rate = fpsRational;
                CodecContext->pkt_timebase = new AVRational { num = fpsRational.den, den = fpsRational.num };
            }
            else
            {
                CodecContext->pkt_timebase = Stream->time_base;
            }

            // Find the default decoder codec from the stream and set it.
            var defaultCodec = ffmpeg.avcodec_find_decoder(Stream->codecpar->codec_id);
            AVCodec* forcedCodec = null;

            // If set, change the codec to the forced codec.
            if (Container.MediaOptions.DecoderCodec.ContainsKey(StreamIndex) &&
                string.IsNullOrWhiteSpace(Container.MediaOptions.DecoderCodec[StreamIndex]) == false)
            {
                var forcedCodecName = Container.MediaOptions.DecoderCodec[StreamIndex];
                forcedCodec = ffmpeg.avcodec_find_decoder_by_name(forcedCodecName);
                if (forcedCodec == null)
                {
                    this.LogWarning(Aspects.Component,
                        $"COMP {MediaType.ToString().ToUpperInvariant()}: " +
                        $"Unable to set decoder codec to '{forcedCodecName}' on stream index {StreamIndex}");
                }
            }

            // Check we have a valid codec to open and process the stream.
            if (defaultCodec == null && forcedCodec == null)
            {
                var errorMessage = $"Fatal error. Unable to find suitable decoder for {Stream->codecpar->codec_id}";
                CloseComponent();
                throw new MediaContainerException(errorMessage);
            }

            var codecCandidates = new[] { forcedCodec, defaultCodec };
            AVCodec* selectedCodec = null;
            var codecOpenResult = 0;

            foreach (var codec in codecCandidates)
            {
                if (codec == null)
                    continue;

                // Pass default codec stuff to the codec context
                CodecContext->codec_id = codec->id;

                // Process the decoder options
                {
                    var decoderOptions = Container.MediaOptions.DecoderParams;

                    // Configure the codec context flags
                    if (decoderOptions.EnableFastDecoding) CodecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
                    if (decoderOptions.EnableLowDelayDecoding) CodecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

                    // process the low res option
                    if (decoderOptions.LowResolutionIndex != VideoResolutionDivider.Full && codec->max_lowres > 0)
                    {
                        var lowResOption = Math.Min((byte)decoderOptions.LowResolutionIndex, codec->max_lowres)
                            .ToString(CultureInfo.InvariantCulture);
                        decoderOptions.LowResIndexOption = lowResOption;
                    }
                }

                // Setup additional settings. The most important one is Threads -- Setting it to 1 decoding is very slow. Setting it to auto
                // decoding is very fast in most scenarios.
                var codecOptions = Container.MediaOptions.DecoderParams.GetStreamCodecOptions(Stream->index);

                codecOptions.SetCopyOpaque();

                // Enable Hardware acceleration if requested
                (this as VideoComponent)?.AttachHardwareDevice(container.MediaOptions.VideoHardwareDevices);

                // Open the CodecContext. This requires exclusive FFmpeg access
                lock (CodecLock)
                {
                    var codecOptionsRef = codecOptions.Pointer;
                    codecOpenResult = ffmpeg.avcodec_open2((AVCodecContext*)m_CodecContext, codec, &codecOptionsRef);
                    codecOptions.UpdateReference(codecOptionsRef);
                }

                // Check if the codec opened successfully
                if (codecOpenResult < 0)
                {
                    this.LogWarning(Aspects.Component,
                        $"Unable to open codec '{Utilities.PtrToStringUTF8(codec->name)}' on stream {streamIndex}");

                    continue;
                }

                // If there are any codec options left over from passing them, it means they were not consumed
                var currentEntry = codecOptions.First();
                while (currentEntry?.Key != null)
                {
                    this.LogWarning(Aspects.Component,
                        $"Invalid codec option: '{currentEntry.Key}' for codec '{Utilities.PtrToStringUTF8(codec->name)}', stream {streamIndex}");
                    currentEntry = codecOptions.Next(currentEntry);
                }

                selectedCodec = codec;
                break;
            }

            if (selectedCodec == null)
            {
                CloseComponent();
                throw new MediaContainerException($"Unable to find suitable decoder codec for stream {streamIndex}. Error code {codecOpenResult}");
            }

            // Startup done. Set some options.
            Stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            MediaType = (MediaType)CodecContext->codec_type;

            switch (MediaType)
            {
                case MediaType.Audio:
                case MediaType.Video:
                    BufferCountThreshold = 25;
                    BufferDurationThreshold = TimeSpan.FromSeconds(1);
                    DecodePacketFunction = DecodeNextAVFrame;
                    break;
                case MediaType.Subtitle:
                    BufferCountThreshold = 0;
                    BufferDurationThreshold = TimeSpan.Zero;
                    DecodePacketFunction = DecodeNextAVSubtitle;
                    break;
                default:
                    throw new NotSupportedException($"A component of MediaType '{MediaType}' is not supported");
            }

            var contentDisposition = StreamInfo.Disposition;
            IsStillPictures = MediaType == MediaType.Video &&
                ((contentDisposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 ||
                (contentDisposition & ffmpeg.AV_DISPOSITION_STILL_IMAGE) != 0 ||
                (contentDisposition & ffmpeg.AV_DISPOSITION_TIMED_THUMBNAILS) != 0);

            if (IsStillPictures)
            {
                BufferCountThreshold = 0;
                BufferDurationThreshold = TimeSpan.Zero;
            }

            // Compute the start time
            StartTime = Stream->start_time == ffmpeg.AV_NOPTS_VALUE
                ? Container.MediaInfo.StartTime == TimeSpan.MinValue ? TimeSpan.Zero : Container.MediaInfo.StartTime
                : Stream->start_time.ToTimeSpan(Stream->time_base);

            // Compute the duration
            Duration = (Stream->duration == ffmpeg.AV_NOPTS_VALUE || Stream->duration <= 0)
                ? Container.MediaInfo.Duration
                : Stream->duration.ToTimeSpan(Stream->time_base);

            CodecName = Utilities.PtrToStringUTF8(selectedCodec->name);
            CodecId = CodecContext->codec_id;
            BitRate = CodecContext->bit_rate < 0 ? 0 : CodecContext->bit_rate;

            this.LogDebug(Aspects.Component,
                $"{MediaType.ToString().ToUpperInvariant()} - Start Time: {StartTime.Format()}; Duration: {Duration.Format()}");

            // Begin processing with a flush packet
            SendFlushPacket();
        }

        #endregion

        #region Properties

        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => m_LoggingHandler;

        /// <summary>
        /// Gets the pointer to the codec context.
        /// </summary>
        public AVCodecContext* CodecContext => (AVCodecContext*)m_CodecContext;

        /// <summary>
        /// Gets a pointer to the component's stream.
        /// </summary>
        public AVStream* Stream => (AVStream*)m_Stream;

        /// <summary>
        /// Gets the media container associated with this component.
        /// </summary>
        public MediaContainer Container { get; }

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public MediaType MediaType { get; }

        /// <summary>
        /// Gets the index of the associated stream.
        /// </summary>
        public int StreamIndex { get; }

        /// <summary>
        /// Gets the component's stream start timestamp as reported
        /// by the start time of the stream.
        /// Returns TimeSpan.MinValue when unknown.
        /// </summary>
        public TimeSpan StartTime { get; internal set; }

        /// <summary>
        /// Gets the duration of this stream component.
        /// If there is no such information it will return TimeSpan.MinValue.
        /// </summary>
        public TimeSpan Duration { get; internal set; }

        /// <summary>
        /// Gets the component's stream end timestamp as reported
        /// by the start and duration time of the stream.
        /// Returns TimeSpan.MinValue when unknown.
        /// </summary>
        public TimeSpan EndTime => (StartTime != TimeSpan.MinValue && Duration != TimeSpan.MinValue)
            ? TimeSpan.FromTicks(StartTime.Ticks + Duration.Ticks)
            : TimeSpan.MinValue;

        /// <summary>
        /// Gets the current length in bytes of the
        /// packet buffer. Limit your Reads to something reasonable before
        /// this becomes too large.
        /// </summary>
        public long BufferLength => Packets.BufferLength;

        /// <summary>
        /// Gets the duration of the packet buffer.
        /// </summary>
        public TimeSpan BufferDuration => Packets.GetDuration(StreamInfo.TimeBase);

        /// <summary>
        /// Gets the number of packets in the queue.
        /// Decode packets until this number becomes 0.
        /// </summary>
        public int BufferCount => Packets.Count;

        /// <summary>
        /// Gets the number of packets to cache before <see cref="HasEnoughPackets"/> returns true.
        /// </summary>
        public int BufferCountThreshold { get; }

        /// <summary>
        /// Gets the packet buffer duration threshold before <see cref="HasEnoughPackets"/> returns true.
        /// </summary>
        public TimeSpan BufferDurationThreshold { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the packet queue contains enough packets.
        /// Port of ffplay.c stream_has_enough_packets.
        /// </summary>
        public bool HasEnoughPackets
        {
            get
            {
                // We want to return true when we can't really get a buffer.
                if (IsDisposed ||
                    BufferCountThreshold <= 0 ||
                    IsStillPictures ||
                    (Container?.IsReadAborted ?? false) ||
                    (Container?.IsAtEndOfStream ?? false))
                    return true;

                // Enough packets means we have a duration of at least 1 second (if the packets report duration)
                // and that we have enough of a packet count depending on the type of media
                return (BufferDuration <= TimeSpan.Zero || BufferDuration.Ticks >= BufferDurationThreshold.Ticks) &&
                    BufferCount >= BufferCountThreshold;
            }
        }

        /// <summary>
        /// Gets the ID of the codec for this component.
        /// </summary>
        public AVCodecID CodecId { get; }

        /// <summary>
        /// Gets the name of the codec for this component.
        /// </summary>
        public string CodecName { get; }

        /// <summary>
        /// Gets the bit rate of this component as reported by the codec context.
        /// Returns 0 for unknown.
        /// </summary>
        public long BitRate { get; }

        /// <summary>
        /// Gets the stream information.
        /// </summary>
        public StreamInfo StreamInfo { get; }

        /// <summary>
        /// Gets a value indicating whether this component contains still images as opposed to real video frames.
        /// Will always return false for non-video components.
        /// </summary>
        public bool IsStillPictures { get; }

        /// <summary>
        /// Gets whether packets have been fed into the codec and frames can be decoded.
        /// </summary>
        public bool HasPacketsInCodec
        {
            get => m_HasCodecPackets.Value;
            private set => m_HasCodecPackets.Value = value;
        }

        /// <summary>
        /// Gets the end time of the latest packet read into the queue since the queue was cleared
        /// (e.g. by a seek). Returns <see cref="TimeSpan.MinValue"/> when no packet was read.
        /// </summary>
        public TimeSpan LastPacketEndTime => m_LastPacketEndTime.Value;

        /// <summary>
        /// Gets or sets a callback called while the drain state lock is held when data is queued or the queue is cleared.
        /// The audio decoding worker uses this to invalidate an already reported audio end.
        /// The argument is true when queued packets are cleared.
        /// </summary>
        public Action<bool> OnPacketSequenceChanged { get; set; }

        /// <summary>
        /// Gets a value indicating whether this instance is disposed.
        /// </summary>
        public bool IsDisposed
        {
            get => m_IsDisposed.Value;
            private set => m_IsDisposed.Value = value;
        }

        /// <summary>
        /// Gets or sets the last frame PTS.
        /// </summary>
        internal long? LastFramePts { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Clears the pending and sent Packet Queues releasing all memory held by those packets.
        /// Additionally it flushes the codec buffered packets.
        /// </summary>
        /// <param name="flushBuffers">if set to <c>true</c> flush codec buffers.</param>
        public void ClearQueuedPackets(bool flushBuffers)
        {
            // Release packets that are already in the queue.
            lock (m_DrainStateLock)
            {
                Packets.Clear();
                m_QueuedDrainRequestIds.Clear();
                m_AcceptedDrainRequestId = 0;
                m_LastPacketEndTime.Value = TimeSpan.MinValue;
                m_HasDrainRequest.Value = false;
                m_HasDrainCompleted.Value = false;
                OnPacketSequenceChanged?.Invoke(true);
            }

            if (flushBuffers)
                FlushCodecBuffers();

            Container.Components.ProcessPacketQueueChanges(PacketQueueOp.Clear, null, MediaType);
        }

        /// <summary>
        /// Sends a special kind of packet (an empty/null packet)
        /// that tells the decoder to refresh the attached picture or enter draining mode.
        /// This is a port of packet_queue_put_nullpacket.
        /// </summary>
        public void SendEmptyPacket()
        {
            var packet = MediaPacket.CreateEmptyPacket(Stream->index);
            SendPacket(packet);
        }

        /// <summary>
        /// Requests a decoder drain only if no request or unread packet is already pending.
        /// </summary>
        public void RequestDrainIfIdle()
        {
            var packet = MediaPacket.CreateEmptyPacket(Stream->index);
            if (!QueuePacket(packet, true))
                packet.Dispose();
        }

        /// <summary>
        /// Reports a completed drain and publishes the audio end while packet enqueueing is excluded.
        /// </summary>
        /// <param name="markEnded">Publishes the last packet time and ended state.</param>
        /// <returns>True if the current packet sequence was completely drained.</returns>
        public bool TryMarkDrainCompleted(Action<TimeSpan> markEnded)
        {
            lock (m_DrainStateLock)
            {
                if (!m_HasDrainRequest.Value || !m_HasDrainCompleted.Value)
                    return false;

                markEnded(m_LastPacketEndTime.Value);
                return true;
            }
        }

        /// <summary>
        /// Pushes a packet into the decoding Packet Queue
        /// and processes the packet in order to try to decode
        /// 1 or more frames.
        /// </summary>
        /// <param name="packet">The packet.</param>
        public void SendPacket(MediaPacket packet)
        {
            if (packet == null)
            {
                SendEmptyPacket();
                return;
            }

            QueuePacket(packet, false);
        }

        /// <summary>
        /// Feeds the decoder buffer and tries to return the next available frame.
        /// </summary>
        /// <returns>The received Media Frame. It is null if no frame could be retrieved.</returns>
        public MediaFrame ReceiveNextFrame()
        {
            var frame = DecodePacketFunction?.Invoke();

            // Check if we need to update the duration of this component.
            // This means we have decoded more frames than what was initially reported by the container.
            if (frame != null && Container.IsStreamSeekable && EndTime != TimeSpan.MinValue &&
                frame.HasValidStartTime && frame.EndTime.Ticks > EndTime.Ticks)
            {
                Duration = TimeSpan.FromTicks(frame.EndTime.Ticks - StartTime.Ticks);
            }

            return frame;
        }

        /// <summary>
        /// Converts decoded, raw frame data in the frame source into a a usable frame. <br />
        /// The process includes performing picture, samples or text conversions
        /// so that the decoded source frame data is easily usable in multimedia applications.
        /// </summary>
        /// <param name="input">The source frame to use as an input.</param>
        /// <param name="output">The target frame that will be updated with the source frame. If null is passed the frame will be instantiated.</param>
        /// <param name="previousBlock">The previous block from which to derive information in case the current frame contains invalid data.</param>
        /// <returns>
        /// Returns true of the operation succeeded. False otherwise.
        /// </returns>
        public abstract bool MaterializeFrame(MediaFrame input, ref MediaBlock output, MediaBlock previousBlock);

        /// <inheritdoc />
        public void Dispose() => Dispose(true);

        /// <summary>
        /// Creates a frame source object given the raw FFmpeg AVFrame or AVSubtitle reference.
        /// </summary>
        /// <param name="framePointer">The raw FFmpeg pointer.</param>
        /// <returns>The media frame.</returns>
        protected abstract MediaFrame CreateFrameSource(IntPtr framePointer);

        /// <summary>
        /// Releases the existing codec context and clears and disposes the packet queues.
        /// </summary>
        protected void CloseComponent()
        {
            if (m_CodecContext == IntPtr.Zero) return;
            RC.Current.Remove(m_CodecContext);
            var codecContext = CodecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            m_CodecContext = IntPtr.Zero;

            // free all the pending and sent packets
            ClearQueuedPackets(true);
            Packets.Dispose();
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            lock (CodecLock)
            {
                if (IsDisposed) return;
                CloseComponent();
                IsDisposed = true;
            }
        }

        /// <summary>
        /// Sends a special kind of packet (a flush packet)
        /// that tells the decoder to flush it internal buffers
        /// This an encapsulation of flush_pkt.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendFlushPacket()
        {
            var packet = MediaPacket.CreateFlushPacket(Stream->index);
            SendPacket(packet);
        }

        /// <summary>
        /// Queues a packet and updates the matching drain generation under one lock.
        /// </summary>
        /// <param name="packet">The packet to queue.</param>
        /// <param name="requireIdle">Whether to reject the drain request when packets are pending.</param>
        /// <returns>True if the packet was queued.</returns>
        private bool QueuePacket(MediaPacket packet, bool requireIdle)
        {
            if (packet.IsFlushPacket)
            {
                Packets.Push(packet);
            }
            else
            {
                lock (m_DrainStateLock)
                {
                    if (requireIdle && (m_HasDrainRequest.Value || Packets.Count > 0 || HasPacketsInCodec))
                        return false;

                    // An empty packet asks the decoder to output the frames it holds. Do not treat
                    // the request as completed until avcodec_receive_frame actually returns EOF.
                    var isDrainPacket = packet.Size == 0 && packet.Pointer->data == null;
                    m_HasDrainRequest.Value = isDrainPacket;
                    m_HasDrainCompleted.Value = false;
                    UpdateLastPacketEndTime(packet);
                    if (isDrainPacket)
                    {
                        m_LatestDrainRequestId = ++m_NextDrainRequestId;
                        m_QueuedDrainRequestIds.Enqueue(m_LatestDrainRequestId);
                    }

                    Packets.Push(packet);
                    if (!isDrainPacket)
                        OnPacketSequenceChanged?.Invoke(false);
                }
            }

            Container.Components.ProcessPacketQueueChanges(PacketQueueOp.Queued, packet, MediaType);
            return true;
        }

        /// <summary>
        /// Records the end time of a packet read from the stream. Flush and empty packets carry no data
        /// and are ignored. Presentation times of video packets are not in read order, so the latest is kept.
        /// </summary>
        /// <param name="packet">The packet.</param>
        private void UpdateLastPacketEndTime(MediaPacket packet)
        {
            if (packet.IsFlushPacket || (packet.Size == 0 && packet.Pointer->data == null))
                return;

            var pointer = packet.Pointer;
            var timestamp = pointer->pts != ffmpeg.AV_NOPTS_VALUE ? pointer->pts : pointer->dts;
            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
                return;

            var endTime = (timestamp + Math.Max(pointer->duration, 0)).ToTimeSpan(Stream->time_base);
            if (endTime > m_LastPacketEndTime.Value)
                m_LastPacketEndTime.Value = endTime;
        }

        /// <summary>
        /// Flushes the codec buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushCodecBuffers()
        {
            if (m_CodecContext != IntPtr.Zero)
                ffmpeg.avcodec_flush_buffers(CodecContext);

            HasPacketsInCodec = false;
            lock (m_DrainStateLock)
            {
                m_AcceptedDrainRequestId = 0;
                m_HasDrainCompleted.Value = false;
            }
        }

        /// <summary>
        /// Feeds the packets to decoder.
        /// </summary>
        /// <param name="fillDecoderBuffer">if set to <c>true</c> fills the decoder buffer with packets.</param>
        /// <returns>The number of packets fed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FeedPacketsToDecoder(bool fillDecoderBuffer)
        {
            var packetCount = 0;
            int sendPacketResult;

            while (Packets.Count > 0)
            {
                var packet = Packets.Peek();
                if (packet.IsFlushPacket)
                {
                    FlushCodecBuffers();

                    // Dequeue the flush packet. We don't add to the decode
                    // count or call the OnPacketDequeued callback because the size is 0
                    packet = Packets.Dequeue();

                    packet.Dispose();
                    continue;
                }

                // Send packet to the decoder but prevent null packets to be sent to it
                // Null packets have never been detected but it's just a safeguard
                var isDrainPacket = packet.Size == 0 && packet.Pointer->data == null;
                var sendStart = Stopwatch.GetTimestamp();
                sendPacketResult = packet.SafePointer != IntPtr.Zero
                    ? ffmpeg.avcodec_send_packet(CodecContext, packet.Pointer) : -ffmpeg.EINVAL;
                if (MediaType == MediaType.Video)
                    VideoPipelineStatistics.AddDecodeTime(Stopwatch.GetTimestamp() - sendStart);

                // EAGAIN means we have filled the decoder buffer
                if (sendPacketResult != -ffmpeg.EAGAIN)
                {
                    // Dequeue the packet and release it.
                    if (isDrainPacket)
                    {
                        lock (m_DrainStateLock)
                        {
                            var requestId = m_QueuedDrainRequestIds.Dequeue();
                            if (sendPacketResult >= 0 || sendPacketResult == ffmpeg.AVERROR_EOF)
                                m_AcceptedDrainRequestId = requestId;
                            else
                                m_HasDrainRequest.Value = false;

                            packet = Packets.Dequeue();
                        }
                    }
                    else
                    {
                        packet = Packets.Dequeue();
                    }

                    Container.Components.ProcessPacketQueueChanges(PacketQueueOp.Dequeued, packet, MediaType);

                    packet.Dispose();
                    packetCount++;
                }

                if (sendPacketResult >= 0)
                    HasPacketsInCodec = true;

                // The codec must be drained and flushed before any later data packet is sent.
                if (fillDecoderBuffer && sendPacketResult >= 0 && !isDrainPacket)
                    continue;

                break;
            }

            return packetCount;
        }

        /// <summary>
        /// Receives the next available frame from decoder.
        /// </summary>
        /// <param name="receiveFrameResult">The receive frame result.</param>
        /// <returns>The frame or null if no frames could be decoded.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MediaFrame ReceiveFrameFromDecoder(out int receiveFrameResult)
        {
            MediaFrame managedFrame = null;
            var outputFrame = MediaFrame.CreateAVFrame();
            var receiveStart = Stopwatch.GetTimestamp();
            receiveFrameResult = ffmpeg.avcodec_receive_frame(CodecContext, outputFrame);
            if (MediaType == MediaType.Video)
            {
                VideoPipelineStatistics.AddDecodeTime(Stopwatch.GetTimestamp() - receiveStart);
                if (receiveFrameResult >= 0)
                    VideoPipelineStatistics.AddDecodedFrame();
            }

            if (receiveFrameResult >= 0)
                managedFrame = CreateFrameSource(new IntPtr(outputFrame));

            if (managedFrame == null)
                MediaFrame.ReleaseAVFrame(outputFrame);

            if (receiveFrameResult == ffmpeg.AVERROR_EOF)
            {
                long completedRequestId;
                lock (m_DrainStateLock)
                    completedRequestId = m_AcceptedDrainRequestId;

                FlushCodecBuffers();
                lock (m_DrainStateLock)
                {
                    // A new data packet or drain request may arrive while the old decoder is being
                    // flushed. Its request ID must not be completed by the old decoder's EOF.
                    if (completedRequestId != 0 && m_HasDrainRequest.Value &&
                        completedRequestId == m_LatestDrainRequestId)
                        m_HasDrainCompleted.Value = true;
                }
            }

            if (receiveFrameResult == -ffmpeg.EAGAIN)
                HasPacketsInCodec = false;

            return managedFrame;
        }

        /// <summary>
        /// Decodes the next Audio or Video frame.
        /// Reference: https://www.ffmpeg.org/doxygen/4.0/group__lavc__encdec.html.
        /// </summary>
        /// <returns>A decoder result containing the decoder frames (if any).</returns>
        private MediaFrame DecodeNextAVFrame()
        {
            var frame = ReceiveFrameFromDecoder(out var receiveFrameResult);
            if (frame == null)
            {
                FeedPacketsToDecoder(false);
                frame = ReceiveFrameFromDecoder(out receiveFrameResult);
            }

            while (frame == null && FeedPacketsToDecoder(true) > 0)
            {
                frame = ReceiveFrameFromDecoder(out receiveFrameResult);
                if (receiveFrameResult < 0)
                    break;
            }

            if (frame == null || Container.Components.OnFrameDecoded == null)
                return frame;

            if (MediaType == MediaType.Audio && frame is AudioFrame audioFrame)
                Container.Components.OnFrameDecoded?.Invoke((IntPtr)audioFrame.Pointer, MediaType);
            else if (MediaType == MediaType.Video && frame is VideoFrame videoFrame)
                Container.Components.OnFrameDecoded?.Invoke((IntPtr)videoFrame.Pointer, MediaType);

            return frame;
        }

        /// <summary>
        /// Decodes the next subtitle frame.
        /// </summary>
        /// <returns>The managed frame.</returns>
        private MediaFrame DecodeNextAVSubtitle()
        {
            // For subtitles we use the old API (new API send_packet/receive_frame) is not yet available
            // We first try to flush anything we've already sent by using an empty packet.
            MediaFrame managedFrame = null;
            var packet = MediaPacket.CreateEmptyPacket(Stream->index);
            var gotFrame = 0;
            var outputFrame = MediaFrame.CreateAVSubtitle();
            var receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, outputFrame, &gotFrame, packet.Pointer);

            // If we don't get a frame from flushing. Feed the packet into the decoder and try getting a frame.
            if (gotFrame == 0)
            {
                packet.Dispose();

                // Dequeue the packet and try to decode with it.
                packet = Packets.Dequeue();

                if (packet != null)
                {
                    Container.Components.ProcessPacketQueueChanges(PacketQueueOp.Dequeued, packet, MediaType);
                    receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, outputFrame, &gotFrame, packet.Pointer);
                }
            }

            // If we got a frame, turn into a managed frame
            if (gotFrame != 0)
            {
                Container.Components.OnSubtitleDecoded?.Invoke((IntPtr)outputFrame);
                managedFrame = CreateFrameSource((IntPtr)outputFrame);
            }

            // Free the packet if we have dequeued it
            packet?.Dispose();

            // deallocate the subtitle frame if we did not associate it with a managed frame.
            if (managedFrame == null)
                MediaFrame.ReleaseAVSubtitle(outputFrame);

            if (receiveFrameResult < 0)
                HasPacketsInCodec = false;

            return managedFrame;
        }

        #endregion
    }
}
