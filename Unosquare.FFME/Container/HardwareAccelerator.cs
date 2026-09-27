namespace Unosquare.FFME.Container
{
    using Common;
    using Diagnostics;
    using FFmpeg.AutoGen;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    /// <summary>
    /// Encapsulates Hardware Accelerator Properties.
    /// </summary>
    internal sealed unsafe class HardwareAccelerator
    {
        private bool m_HardwareFormatRequested;
        private bool m_IsHardwareUnavailable;
        private int m_ReportedMode;

        /// <summary>
        /// Initializes a new instance of the <see cref="HardwareAccelerator"/> class.
        /// </summary>
        /// <param name="component">The component this accelerator is attached to.</param>
        /// <param name="selectedConfig">The selected hardware device configuration.</param>
        public HardwareAccelerator(VideoComponent component, HardwareDeviceInfo selectedConfig)
        {
            Component = component;
            Name = selectedConfig.DeviceTypeName;
            DeviceType = selectedConfig.DeviceType;
            PixelFormat = selectedConfig.PixelFormat;
            GetFormatCallback = GetPixelFormat;
        }

        /// <summary>
        /// Gets the name of the HW accelerator.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the component this accelerator is attached to..
        /// </summary>
        public VideoComponent Component { get; }

        /// <summary>
        /// Gets the hardware output pixel format.
        /// </summary>
        public AVPixelFormat PixelFormat { get; }

        /// <summary>
        /// Gets the type of the hardware device.
        /// </summary>
        public AVHWDeviceType DeviceType { get; }

        /// <summary>
        /// Gets the callback used to resolve the hardware pixel format.
        /// </summary>
        public AVCodecContext_get_format GetFormatCallback { get; }

        /// <summary>
        /// Gets the supported hardware decoder device types for the given codec.
        /// </summary>
        /// <param name="codecId">The codec identifier.</param>
        /// <returns>
        /// A list of hardware device decoders compatible with the codec.
        /// </returns>
        public static List<HardwareDeviceInfo> GetCompatibleDevices(AVCodecID codecId)
        {
            const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
            var codec = ffmpeg.avcodec_find_decoder(codecId);
            var result = new List<HardwareDeviceInfo>(64);
            var configIndex = 0;

            // skip unsupported configs
            if (codec == null || codecId == AVCodecID.AV_CODEC_ID_NONE)
                return result;

            while (true)
            {
                var config = ffmpeg.avcodec_get_hw_config(codec, configIndex);
                if (config == null) break;

                if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0
                    && config->device_type != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    result.Add(new HardwareDeviceInfo(config));
                }

                configIndex++;
            }

            return result;
        }

        /// <summary>
        /// Downloads the frame from the hardware into a software frame if possible.
        /// The input hardware frame gets freed and the return value will point to the new software frame.
        /// </summary>
        /// <param name="codecContext">The codec context.</param>
        /// <param name="input">The input frame coming from the decoder (may or may not be hardware).</param>
        /// <param name="isHardwareFrame">if set to <c>true</c> [comes from hardware] otherwise, hardware decoding was not performed.</param>
        /// <returns>
        /// The frame downloaded from the device into RAM.
        /// </returns>
        /// <exception cref="Exception">Failed to transfer data to output frame.</exception>
        public AVFrame* ExchangeFrame(AVCodecContext* codecContext, AVFrame* input, out bool isHardwareFrame)
        {
            // Only frames that actually live in the device surface format were decoded by the
            // hardware. A device can be attached while the codec falls back to software.
            isHardwareFrame = codecContext->hw_device_ctx != null && input->format == (int)PixelFormat;
            ReportDecodingMode(isHardwareFrame, (AVPixelFormat)input->format);

            if (!isHardwareFrame)
                return input;

            var output = MediaFrame.CreateAVFrame();

            var transferStart = Stopwatch.GetTimestamp();
            var result = ffmpeg.av_hwframe_transfer_data(output, input, 0);
            VideoPipelineStatistics.AddTransfer(Stopwatch.GetTimestamp() - transferStart);
            ffmpeg.av_frame_copy_props(output, input);
            if (result < 0)
            {
                MediaFrame.ReleaseAVFrame(output);
                throw new MediaContainerException("Failed to transfer data to output frame");
            }

            MediaFrame.ReleaseAVFrame(input);

            return output;
        }

        /// <summary>
        /// Determines whether the pixel format is a hardware surface format.
        /// </summary>
        /// <param name="format">The pixel format.</param>
        /// <returns>Whether the format refers to hardware surfaces.</returns>
        private static bool IsHardwareFormat(AVPixelFormat format)
        {
            var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
            return descriptor != null && (descriptor->flags & (ulong)ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
        }

        /// <summary>
        /// Selects the pixel format for the decoder. Port of (get_format) method in ffmpeg.c.
        /// </summary>
        /// <remarks>
        /// When the hardware format is chosen but the hardware decoder cannot be initialized
        /// (e.g. the GPU does not support the codec profile), FFmpeg calls this callback again
        /// without that format. That failure is remembered so the stream does not retry the
        /// hardware decoder every time the decoder is reconfigured.
        /// </remarks>
        /// <param name="context">The codec context.</param>
        /// <param name="pixelFormats">The pixel formats, terminated by <see cref="AVPixelFormat.AV_PIX_FMT_NONE"/>.</param>
        /// <returns>The pixel format that the codec will be using.</returns>
        private AVPixelFormat GetPixelFormat(AVCodecContext* context, AVPixelFormat* pixelFormats)
        {
            var offersHardwareFormat = false;
            var softwareFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            for (var p = pixelFormats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == PixelFormat)
                    offersHardwareFormat = true;
                else if (softwareFormat == AVPixelFormat.AV_PIX_FMT_NONE && !IsHardwareFormat(*p))
                    softwareFormat = *p;
            }

            if (!offersHardwareFormat && m_HardwareFormatRequested && !m_IsHardwareUnavailable)
            {
                m_IsHardwareUnavailable = true;
                Component.LogInfo(Aspects.Component,
                    $"VIDEO DECODER: {Name} could not decode {Component.CodecName}; using software decoding.");
            }

            m_HardwareFormatRequested = offersHardwareFormat && !m_IsHardwareUnavailable;
            if (m_HardwareFormatRequested)
                return PixelFormat;

            // Use the first software format, which is the codec's preferred output.
            return softwareFormat != AVPixelFormat.AV_PIX_FMT_NONE ? softwareFormat : *pixelFormats;
        }

        /// <summary>
        /// Logs the decoding path whenever it changes (first frame, hardware to software and back).
        /// </summary>
        /// <param name="isHardwareFrame">Whether the frame was decoded by the hardware.</param>
        /// <param name="format">The decoded frame format.</param>
        private void ReportDecodingMode(bool isHardwareFrame, AVPixelFormat format)
        {
            var mode = isHardwareFrame ? 1 : 2;
            if (m_ReportedMode == mode)
                return;

            m_ReportedMode = mode;
            Component.LogInfo(Aspects.Component, isHardwareFrame
                ? $"VIDEO DECODER: {Name} hwaccel decoding {Component.CodecName} (frames in GPU surfaces)."
                : $"VIDEO DECODER: software decoding {Component.CodecName} ({format}) although {Name} is attached.");
        }
    }
}