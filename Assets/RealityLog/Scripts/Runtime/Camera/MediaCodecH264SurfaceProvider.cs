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
        [SerializeField] private int fps = 15;
        [SerializeField] private int bitrateBps = 2_000_000;

        [Header("Streaming")]
        [SerializeField] private OpenQuestCaptureStreamer? streamer;

        private AndroidJavaObject? javaProvider;
        private int captureWidth;
        private int captureHeight;
        private OpenQuestCaptureStreamer? cachedStreamer;

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
            if (javaProvider == null) return;
            if (cachedStreamer == null)
                cachedStreamer = streamer != null ? streamer : FindObjectOfType<OpenQuestCaptureStreamer>();
            if (cachedStreamer == null || !cachedStreamer.IsStreaming) return;

            while (true)
            {
                using var chunk = javaProvider.Call<AndroidJavaObject>("pollEncodedChunk");
                if (chunk == null) break;

                var data = chunk.Get<byte[]>("data");
                if (data == null || data.Length == 0) continue;

                var timestampNs = chunk.Get<long>("timestampNs");
                cachedStreamer.SendRgbFrame(timestampNs, data, captureWidth, captureHeight);
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
