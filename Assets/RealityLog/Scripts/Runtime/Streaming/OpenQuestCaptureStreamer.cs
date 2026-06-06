# nullable enable

using System;
using RealityLog.Common;
using UnityEngine;

namespace RealityLog.Streaming
{
    /// <summary>
    /// Small Unity-facing wrapper for the workstation UDP stream. Attach this to a scene object
    /// to smoke-test the existing QuestStreamer receiver before wiring camera/depth payloads.
    /// </summary>
    public class OpenQuestCaptureStreamer : MonoBehaviour
    {
        private const ulong DefaultOpenXrPoseLocationFlags = 0x0f;

        [Header("Workstation Receiver")]
        [SerializeField] private string host = "10.0.1.160";
        [SerializeField] private int port = 48002;

        [Header("Lifecycle")]
        [SerializeField] private bool startOnEnable = false;
        [SerializeField] private bool sendStartupStatus = true;

        [Header("HMD Pose Streaming")]
        [Tooltip("Stream left camera pose derived from HMD tracking at Update rate (~50 Hz).")]
        [SerializeField] private bool streamHmdPose = true;
        [Tooltip("Fixed offset from HMD center to left camera in HMD local space (meters).")]
        [SerializeField] private Vector3 leftCameraExtrinsics = new Vector3(-0.032f, -0.018f, -0.062f);

        private QuestStreamUdpSender? sender;

        public bool IsStreaming => sender?.IsOpen ?? false;

        public string Host
        {
            get => host;
            set => host = value;
        }

        public int Port
        {
            get => port;
            set => port = value;
        }

        private void Update()
        {
            if (!IsStreaming || !streamHmdPose)
            {
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            // HIGH-RATE POSE STREAM (~50 Hz, wall-clock timestamp)
            // This is a second pose stream running alongside the depth-cadence pose sent by
            // DepthMapExporter. Both use the same POSE_OPENXR_BINARY format and StreamType.Pose.
            // The backend synchronizer uses closest-timestamp matching, so:
            //   - depth frames will match the depth-cadence pose (exact timestamp)
            //   - RGB frames will match whichever pose is nearest in time (likely this stream)
            // Left camera position is derived from the HMD tracking pose plus the known
            // physical offset. Orientation is the HMD orientation directly — the Quest 3
            // passthrough cameras share the same optical axis as the HMD center (offset only,
            // no rotation), so hmdRot is the correct left-camera orientation.
            var hmdPos = cam.transform.position;
            var hmdRot = cam.transform.rotation;
            var leftCamPos = hmdPos + hmdRot * leftCameraExtrinsics;
            var timestampNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

            SendLeftCameraPose(timestampNs, leftCamPos, hmdRot);
        }

        private void OnEnable()
        {
            if (startOnEnable)
            {
                StartStreaming();
            }
        }

        private void OnDisable()
        {
            StopStreaming();
        }

        private void OnDestroy()
        {
            StopStreaming();
        }

        public void StartStreaming()
        {
            if (IsStreaming)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Streaming host is empty; not starting UDP sender.");
                return;
            }

            try
            {
                sender = new QuestStreamUdpSender(host, port);
                Debug.Log($"[{Constants.LOG_TAG}] OpenQuestCapture UDP streamer targeting {host}:{port}");

                if (sendStartupStatus)
                {
                    SendStatus("OpenQuestCapture streamer online");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to start UDP streamer: {exception}");
                sender?.Dispose();
                sender = null;
            }
        }

        public void StopStreaming()
        {
            if (sender == null)
            {
                return;
            }

            SendStatus("OpenQuestCapture streamer offline");
            sender.Dispose();
            sender = null;
        }

        public void SendStatus(string message)
        {
            sender?.SendStatus(message);
        }

        public void SendDepthFrame(long timestampNs, byte[] payload, int width, int height)
        {
            if (sender == null) return;
            sender.SendPayload(
                QuestStreamUdpSender.StreamType.Depth,
                QuestStreamUdpSender.PayloadFormat.DepthUint16OpenXrZ,
                (ulong)timestampNs,
                (ushort)width,
                (ushort)height,
                payload);
        }

        public void SendLeftCameraPose(long timestampNs, Vector3 position, Quaternion orientation)
        {
            if (sender == null || timestampNs <= 0)
            {
                return;
            }

            // Convert OpenXR (Y-up, Z-back) to Hydra optical convention (Y-down, Z-forward).
            // Negate Y and Z on position; negate qy and qz on quaternion.
            sender.SendOpenXrPose(
                (ulong) timestampNs,
                DefaultOpenXrPoseLocationFlags,
                 orientation.x,
                -orientation.y,
                -orientation.z,
                 orientation.w,
                 position.x,
                -position.y,
                -position.z);
        }

        public void SendRgbFrame(long timestampNs, byte[] nalData, int width, int height)
        {
            if (sender == null || nalData == null || nalData.Length == 0) return;

            sender.SendPayload(
                QuestStreamUdpSender.StreamType.Camera,
                QuestStreamUdpSender.PayloadFormat.CameraH264,
                (ulong)timestampNs,
                (ushort)width,
                (ushort)height,
                nalData);
        }
    }
}
