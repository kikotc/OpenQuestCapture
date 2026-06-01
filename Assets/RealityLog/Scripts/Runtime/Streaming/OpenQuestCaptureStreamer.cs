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

        public void SendLeftCameraPose(long timestampNs, Vector3 position, Quaternion orientation)
        {
            if (sender == null || timestampNs <= 0)
            {
                return;
            }

            sender.SendOpenXrPose(
                (ulong) timestampNs,
                DefaultOpenXrPoseLocationFlags,
                orientation.x,
                orientation.y,
                orientation.z,
                orientation.w,
                position.x,
                position.y,
                position.z);
        }
    }
}
