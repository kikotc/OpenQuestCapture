# nullable enable

using System;
using RealityLog.Common;
using UnityEngine;

namespace RealityLog.Streaming
{
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
        [Tooltip("Max pose packets per second sent to workstation. 0 or negative = disabled. Lower = less Hydra load.")]
        [SerializeField] private float poseFps = 25f;
        [Tooltip("Fixed offset from HMD center to left camera in HMD local space (meters).")]
        [SerializeField] private Vector3 leftCameraExtrinsics = new Vector3(-0.032f, -0.018f, -0.062f);

        private QuestStreamUdpSender? sender;
        private float _lastPoseSendTime = -1f;

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
            if (!IsStreaming || !streamHmdPose || poseFps <= 0f)
                return;

            var cam = Camera.main;
            if (cam == null)
                return;

            // HIGH-RATE POSE STREAM (throttled to poseFps, OVR hardware clock timestamp)
            // Runs alongside the depth-cadence pose sent by DepthMapExporter. Both use the
            // same POSE_OPENXR_BINARY format and StreamType.Pose. The backend synchronizer
            // uses closest-timestamp matching so RGB frames match whichever pose is nearest.
            // Timestamp uses OVRPlugin.GetTimeInSeconds() to stay in the same clock domain as
            // depth and RGB frame timestamps (all use the device's monotonic hardware clock).
            var now = Time.unscaledTime;
            if (now - _lastPoseSendTime < 1f / poseFps)
                return;
            _lastPoseSendTime = now;

            var hmdPos = cam.transform.position;
            var hmdRot = cam.transform.rotation;
            var leftCamPos = hmdPos + hmdRot * leftCameraExtrinsics;
            var timestampNs = (long)(OVRPlugin.GetTimeInSeconds() * 1_000_000_000.0);

            SendLeftCameraPose(timestampNs, leftCamPos, hmdRot);
        }

        private void OnEnable()
        {
            if (startOnEnable)
                StartStreaming();
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
                return;

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
                    SendStatus("OpenQuestCapture streamer online");
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
                return;

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
                return;

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
