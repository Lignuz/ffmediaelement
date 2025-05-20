namespace Unosquare.FFME
{
    using Common;
    using Diagnostics;
    using Engine;
    using System;
    using System.ComponentModel;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;

    public partial class MediaElement : ILoggingHandler, ILoggingSource, INotifyPropertyChanged
    {
        /// <inheritdoc />
        ILoggingHandler ILoggingSource.LoggingHandler => this;

        /// <summary>
        /// Provides access to the underlying media engine driving this control.
        /// This property is intended for advanced usages only.
        /// </summary>
        internal MediaEngine MediaCore { get; }

        #region Public API

        /// <summary>
        /// Requests new media options to be applied, including stream component selection.
        /// Handle the <see cref="MediaChanging"/> event to set new <see cref="MediaOptions"/> based on
        /// <see cref="MediaInfo"/> properties.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> ChangeMedia() => Task.Run(async () =>
        {
            try { return await MediaCore.ChangeMedia().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Begins or resumes playback of the currently loaded media.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> Play() => Task.Run(async () =>
        {
            try { return await MediaCore.Play().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Pauses playback of the currently loaded media.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> Pause() => Task.Run(async () =>
        {
            try { return await MediaCore.Pause().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Pauses and rewinds the currently loaded media.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> Stop() => Task.Run(async () =>
        {
            try { return await MediaCore.Stop().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Seeks to the specified target position.
        /// This is an alternative to using the <see cref="Position"/> dependency property.
        /// </summary>
        /// <param name="target">The target time to seek to.</param>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> Seek(TimeSpan target) => Task.Run(async () =>
        {
            try { return await MediaCore.Seek(target).ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// 지정된 시간 이전 또는 해당 시간의 가장 가까운 키프레임으로 빠르게 탐색합니다.
        /// 프레임 정확도가 필요 없는 빠른 이동에 적합합니다.
        /// </summary>
        /// <param name="target">탐색할 대상 시간 (가장 가까운 키프레임 기준).</param>
        /// <returns>대기 가능한 명령 결과입니다.</returns>
        public ConfiguredTaskAwaitable<bool> SeekKeyFrame(TimeSpan target) => Task.Run(async () =>
        {
            try { return await MediaCore.SeekKeyFrame(target).ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// 지정된 시간에 정확히 도달하도록 탐색을 수행합니다.
        /// 대상 시간 이상에 도달할 때까지 프레임을 디코딩합니다.
        /// </summary>
        /// <param name="target">정확히 탐색할 대상 시간입니다.</param>
        /// <returns>대기 가능한 명령 결과입니다.</returns>
        public ConfiguredTaskAwaitable<bool> SeekAccurate(TimeSpan target) => Task.Run(async () =>
        {
            try { return await MediaCore.SeekAccurate(target).ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Seeks a single frame forward.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> StepForward() => Task.Run(async () =>
        {
            try { return await MediaCore.StepForward().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Seeks a single frame backward.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> StepBackward() => Task.Run(async () =>
        {
            try { return await MediaCore.StepBackward().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }
            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Opens the specified URI.
        /// This is an alternative method of opening media vs using the
        /// <see cref="Source"/> Dependency Property.
        /// </summary>
        /// <param name="uri">The URI.</param>
        /// <returns>The awaitable task.</returns>
        public ConfiguredTaskAwaitable<bool> Open(Uri uri) => Task.Run(async () =>
        {
            try { return await MediaCore.Open(uri).ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }

            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Opens the specified custom input stream.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <returns>The awaitable task.</returns>
        public ConfiguredTaskAwaitable<bool> Open(IMediaInputStream stream) => Task.Run(async () =>
        {
            try { return await MediaCore.Open(stream).ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }

            return false;
        }).ConfigureAwait(true);

        /// <summary>
        /// Closes the currently loaded media.
        /// </summary>
        /// <returns>The awaitable command.</returns>
        public ConfiguredTaskAwaitable<bool> Close() => Task.Run(async () =>
        {
            try { return await MediaCore.Close().ConfigureAwait(false); }
            catch (Exception ex) { PostMediaFailedEvent(ex); }

            return false;
        }).ConfigureAwait(true);

        #endregion

        /// <inheritdoc />
        void ILoggingHandler.HandleLogMessage(LoggingMessage message) =>
            RaiseMessageLoggedEvent(message);
    }
}
