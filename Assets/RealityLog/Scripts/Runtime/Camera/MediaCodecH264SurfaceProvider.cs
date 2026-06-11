# nullable enable

using System;
using UnityEngine;
using RealityLog.Common;
using RealityLog.Streaming;

namespace RealityLog.Camera
{
    public class MediaCodecH264SurfaceProvider : SurfaceProviderBase
    {
        private const string JAVA_CLASS = "com.realitylog.streaming.MediaCodecH264SurfaceProvider";

        [Header("H264 Encoder")]
        [SerializeField] private int fps = 5;
        [SerializeField] private int bitrateBps = 2_000_000;

        [Header("Streaming")]
        [SerializeField] private OpenQuestCaptureStreamer? streamer;

        private AndroidJavaObject? javaProvider;
        private int captureWidth;
        private int captureHeight;
        private OpenQuestCaptureStreamer? cachedStreamer;
        private int _framesSent = 0;
        private int _nullProviderLogCount = 0;  // how many LateUpdates javaProvider was null
        private int _noOutputLogCount = 0;       // how many drains produced no NAL output
        private bool _wasStreaming = false;

#if UNITY_ANDROID
        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata)
        {
            Close();
            captureWidth  = metadata.sensor.pixelArraySize.width;
            captureHeight = metadata.sensor.pixelArraySize.height;
            try
            {
                javaProvider = new AndroidJavaObject(JAVA_CLASS, captureWidth, captureHeight, fps, bitrateBps);
                Debug.Log($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: encoder started {captureWidth}x{captureHeight} @ {fps} fps, {bitrateBps} bps");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: failed to create encoder: {e}");
                javaProvider = null;
            }
            return javaProvider;
        }

        private void LateUpdate()
        {
            if (javaProvider == null)
            {
                _nullProviderLogCount++;
                if (_nullProviderLogCount % 300 == 1)
                    Debug.LogWarning($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: javaProvider is null — encoder not initialized ({_nullProviderLogCount} frames)");
                return;
            }

            if (cachedStreamer == null)
                cachedStreamer = streamer != null ? streamer : FindObjectOfType<OpenQuestCaptureStreamer>();

            var isStreaming = cachedStreamer != null && cachedStreamer.IsStreaming;

            // Flush stale encoder queue the moment streaming starts so the backend receives
            // only fresh frames. Without this, frames buffered since app open (up to 64 at
            // 5 fps ≈ 12 seconds) would be sent first with stale timestamps.
            if (isStreaming && !_wasStreaming)
            {
                javaProvider.Call("flushQueue");
                _noOutputLogCount = 0;
                Debug.Log($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: flushed stale encoder queue on streaming start");
            }
            _wasStreaming = isStreaming;

            if (!isStreaming) return;

            javaProvider.Call("drainIntoQueue");
            bool drainedAny = false;
            while (true)
            {
                using var chunk = javaProvider.Call<AndroidJavaObject>("pollEncodedChunk");
                if (chunk == null) break;

                var data = chunk.Get<byte[]>("data");
                if (data == null || data.Length == 0) continue;

                var timestampNs = chunk.Get<long>("timestampNs");
                cachedStreamer!.SendRgbFrame(timestampNs, data, captureWidth, captureHeight);
                drainedAny = true;
                _framesSent++;
                if (_framesSent == 1 || _framesSent % 30 == 0)
                    Debug.Log($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: sent RGB frame #{_framesSent} ts={timestampNs} size={data.Length}");
            }

            if (!drainedAny)
            {
                _noOutputLogCount++;
                if (_noOutputLogCount % 300 == 1)
                    Debug.LogWarning($"[{Constants.LOG_TAG}] MediaCodecH264SurfaceProvider: encoder running but no NAL output after {_noOutputLogCount} drain calls (sent {_framesSent} frames total)");
            }
            else
            {
                _noOutputLogCount = 0;
            }
        }
#else
        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata) => null;
#endif

        private void OnDestroy() => Close();

        private void Close()
        {
            javaProvider?.Call("close");
            javaProvider?.Dispose();
            javaProvider = null;
        }
    }
}
