// author: https://github.com/pabloFuente

using System;
using LiveKit.Proto;
using LiveKit.Rtc.Internal;

namespace LiveKit.Rtc
{
    /// <summary>
    /// Shared base for video sources that publish frames to a track.
    /// Holds the FFI handle and the operations common to every source type
    /// (creating a track, disposal); frame capture is left to subclasses
    /// since native and encoded sources push very different payloads.
    /// </summary>
    public abstract class VideoSourceBase : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly FfiHandle _handle;
        private bool _disposed;

        private protected VideoSourceBase(
            VideoSourceType type,
            int width,
            int height,
            bool isScreencast = false
        )
        {
            _width = width;
            _height = height;

            var request = new FfiRequest
            {
                NewVideoSource = new NewVideoSourceRequest
                {
                    Type = type,
                    Resolution = new VideoSourceResolution
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                    },
                    IsScreencast = isScreencast,
                },
            };

            var response = FfiClient.Instance.SendRequest(request);
            var sourceInfo = response.NewVideoSource.Source;
            _handle = FfiHandle.FromId(sourceInfo.Handle.Id);
        }

        /// <summary>
        /// Gets the width of the video source in pixels.
        /// </summary>
        public int Width => _width;

        /// <summary>
        /// Gets the height of the video source in pixels.
        /// </summary>
        public int Height => _height;

        /// <summary>
        /// Gets the internal FFI handle.
        /// </summary>
        internal FfiHandle Handle => _handle;

        /// <summary>
        /// Whether this source has been disposed.
        /// </summary>
        protected bool Disposed => _disposed;

        /// <summary>
        /// Throws if this source has already been disposed.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>
        /// Creates a local video track from this video source.
        /// </summary>
        /// <param name="name">The name of the track.</param>
        /// <returns>A new LocalVideoTrack.</returns>
        public LocalVideoTrack CreateTrack(string name = "video")
        {
            var request = new FfiRequest
            {
                CreateVideoTrack = new CreateVideoTrackRequest
                {
                    Name = name,
                    SourceHandle = _handle.HandleId,
                },
            };

            var response = FfiClient.Instance.SendRequest(request);
            var trackInfo = response.CreateVideoTrack.Track;

            return new LocalVideoTrack(FfiHandle.FromId(trackInfo.Handle.Id), trackInfo);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _handle.Dispose();
        }
    }

    /// <summary>
    /// Represents a video source for publishing raw (unencoded) video frames.
    /// The native encoder (VP8/H264/etc., per <see cref="TrackPublishOptions"/>)
    /// encodes each frame you push here before it goes out over the wire.
    /// </summary>
    public class VideoSource : VideoSourceBase
    {
        /// <summary>
        /// Initializes a new instance of the video source.
        /// </summary>
        /// <param name="width">The width of the video source in pixels.</param>
        /// <param name="height">The height of the video source in pixels.</param>
        public VideoSource(int width, int height)
            : base(VideoSourceType.VideoSourceNative, width, height) { }

        /// <summary>
        /// Captures a video frame and sends it to the video source.
        /// </summary>
        /// <param name="frame">The video frame to capture.</param>
        /// <param name="timestampUs">Optional timestamp in microseconds. If 0, current time is used.</param>
        /// <param name="rotation">Optional rotation to apply to the frame.</param>
        public void CaptureFrame(
            VideoFrame frame,
            long timestampUs = 0,
            Proto.VideoRotation rotation = Proto.VideoRotation._0
        )
        {
            ThrowIfDisposed();

            var request = new FfiRequest
            {
                CaptureVideoFrame = new CaptureVideoFrameRequest
                {
                    SourceHandle = Handle.HandleId,
                    Buffer = frame.ToProtoInfo(),
                    Rotation = rotation,
                    TimestampUs = timestampUs,
                },
            };

            FfiClient.Instance.SendRequest(request);
        }
    }

    /// <summary>
    /// Feedback reported by the pre-encoded passthrough path, describing what
    /// the downstream connection needs from your encoder. Poll this
    /// periodically (e.g. once per frame or on a timer) and react by forcing
    /// a keyframe or adjusting bitrate/framerate as requested.
    /// </summary>
    public readonly struct EncodedVideoSourceFeedback
    {
        internal EncodedVideoSourceFeedback(bool keyframeRequested, EncodedRateControl? rateControl)
        {
            KeyframeRequested = keyframeRequested;
            RateControl = rateControl;
        }

        /// <summary>
        /// Whether the downstream connection is requesting a keyframe be sent
        /// as soon as possible (e.g. after a new subscriber joins, or packet
        /// loss requires a refresh).
        /// </summary>
        public bool KeyframeRequested { get; }

        /// <summary>
        /// The current target bitrate and framerate the encoder should aim
        /// for, or <c>null</c> if the source has none to report.
        /// </summary>
        public EncodedRateControl? RateControl { get; }
        // Note: EncodedRateControl is a protobuf message class (reference
        // type), so "EncodedRateControl?" here is an ordinary nullable
        // reference annotation, not Nullable<T> - no special handling needed
        // when assigning a possibly-null EncodedRateControl to it.
    }

    /// <summary>
    /// Represents a video source for publishing pre-encoded video frames
    /// (e.g. from a hardware encoder or an existing encoded stream) directly,
    /// bypassing LiveKit's built-in software encoder. You are responsible for
    /// producing a valid encoded bitstream (with correctly placed keyframes)
    /// and for reacting to <see cref="TakeFeedback"/>.
    /// </summary>
    public class EncodedVideoSource : VideoSourceBase
    {
        /// <summary>
        /// Initializes a new instance of the encoded video source.
        /// </summary>
        /// <param name="width">The width of the video source in pixels.</param>
        /// <param name="height">The height of the video source in pixels.</param>
        /// <param name="isScreencast">Whether this source represents a screen share.</param>
        public EncodedVideoSource(int width, int height, bool isScreencast = false)
            : base(VideoSourceType.VideoSourceEncoded, width, height, isScreencast) { }

        /// <summary>
        /// Pushes one complete pre-encoded access unit (a full frame's worth
        /// of encoded data) to the source.
        /// </summary>
        /// <param name="data">
        /// The encoded access unit bytes. The buffer only needs to remain
        /// valid for the duration of this call; it is copied natively before
        /// this method returns.
        /// </param>
        /// <param name="codec">The codec the data is encoded with.</param>
        /// <param name="frameType">Whether this is a key or delta frame.</param>
        /// <param name="width">The width of the encoded frame in pixels.</param>
        /// <param name="height">The height of the encoded frame in pixels.</param>
        /// <param name="timestampUs">Optional timestamp in microseconds. If 0, current time is used.</param>
        /// <param name="metadata">Optional per-frame metadata to attach.</param>
        /// <returns>
        /// <c>true</c> if the source accepted the frame; <c>false</c> if it
        /// was rejected (for example, if the source isn't attached to a
        /// track yet).
        /// </returns>
        public unsafe bool PushFrame(
            byte[] data,
            Proto.VideoCodec codec,
            Proto.EncodedFrameType frameType,
            int width,
            int height,
            long timestampUs = 0,
            FrameMetadata? metadata = null
        )
        {
            ThrowIfDisposed();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            FfiResponse response;

            fixed (byte* dataPtr = data)
            {
                var captureRequest = new CaptureEncodedVideoFrameRequest
                {
                    SourceHandle = Handle.HandleId,
                    Buffer = new EncodedVideoBufferInfo
                    {
                        DataPtr = (ulong)dataPtr,
                        DataLen = (ulong)data.Length,
                    },
                    Codec = codec,
                    FrameType = frameType,
                    Width = (uint)width,
                    Height = (uint)height,
                    TimestampUs = timestampUs,
                };

                if (metadata != null)
                {
                    captureRequest.Metadata = metadata;
                }

                var request = new FfiRequest { CaptureEncodedVideoFrame = captureRequest };

                // The FFI copies the buffer synchronously before returning, so it's
                // safe to unpin once SendRequest completes.
                response = FfiClient.Instance.SendRequest(request);
            }

            return response.CaptureEncodedVideoFrame.Accepted;
        }

        /// <summary>
        /// Retrieves feedback accumulated by the source since the previous
        /// call (or since the source was created, on the first call). Call
        /// this regularly so your encoder can respond to keyframe requests
        /// and rate control changes.
        /// </summary>
        public EncodedVideoSourceFeedback TakeFeedback()
        {
            ThrowIfDisposed();

            var request = new FfiRequest
            {
                TakeEncodedVideoSourceFeedback = new TakeEncodedVideoSourceFeedbackRequest
                {
                    SourceHandle = Handle.HandleId,
                },
            };

            var response = FfiClient.Instance.SendRequest(request);
            var feedback = response.TakeEncodedVideoSourceFeedback;

            // RateControl is a message-typed optional field: protobuf never
            // generates a HasXxx accessor for these, since the property
            // itself is already nullable (a reference type) and null means
            // "absent".
            return new EncodedVideoSourceFeedback(
                feedback.KeyframeRequested,
                feedback.RateControl
            );
        }
    }
}