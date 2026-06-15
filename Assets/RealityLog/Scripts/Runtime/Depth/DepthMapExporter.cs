# nullable enable

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Android;
using RealityLog.Common;
using RealityLog.IO;
using RealityLog.Streaming;

namespace RealityLog.Depth
{
    public class DepthMapExporter : MonoBehaviour
    {
        private static readonly string[] descriptorHeader = new[]
            {
                "timestamp_ms", "ovr_timestamp",
                "create_pose_location_x", "create_pose_location_y", "create_pose_location_z",
                "create_pose_rotation_x", "create_pose_rotation_y", "create_pose_rotation_z", "create_pose_rotation_w",
                "fov_left_angle_tangent", "fov_right_angle_tangent", "fov_top_angle_tangent", "fov_down_angle_tangent",
                "near_z", "far_z",
                "width", "height"
            };

        [HideInInspector]
        [SerializeField] private ComputeShader copyDepthMapShader = default!;
        [SerializeField] private string directoryName = "";
        [SerializeField] private string leftDepthMapDirectoryName = "left_depth";
        [SerializeField] private string rightDepthMapDirectoryName = "right_depth";
        [SerializeField] private string leftDepthDescFileName = "left_depth_descriptors.csv";
        [SerializeField] private string rightDepthDescFileName = "right_depth_descriptors.csv";
        [Header("Synchronized Capture")]
        [Tooltip("Required: Reference to CaptureTimer for FPS-based capture timing.")]
        [SerializeField] private CaptureTimer captureTimer = default!;
        [Header("Streaming")]
        [Tooltip("Optional: Sends the left camera pose as a standalone QuestStreamer pose packet.")]
        [SerializeField] private OpenQuestCaptureStreamer? poseStreamer = null;
        [Tooltip("Stream left eye depth over UDP as uint16 OpenXR window-depth with nearZ/farZ prefix.")]
        [SerializeField] private bool streamDepth = true;

        private DepthDataExtractor? depthDataExtractor;

        private DepthRenderTextureExporter? renderTextureExporter;
        private OpenQuestCaptureStreamer? resolvedPoseStreamer;
        private CsvWriter? leftDepthCsvWriter;
        private CsvWriter? rightDepthCsvWriter;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;

        private bool hasScenePermission = false;
        private bool depthSystemReady = false;

        public bool IsDepthSystemReady => depthSystemReady;

        public string DirectoryName
        {
            get => directoryName;
            set => directoryName = value;
        }

        public void StartExport()
        {
            leftDepthCsvWriter?.Dispose();
            rightDepthCsvWriter?.Dispose();

            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter: Reset base times: OVR={baseOvrTimeSec:F3}s, Unix={baseUnixTimeMs}ms");

            leftDepthCsvWriter = new(Path.Join(Application.persistentDataPath, DirectoryName, leftDepthDescFileName), descriptorHeader);
            rightDepthCsvWriter = new(Path.Join(Application.persistentDataPath, DirectoryName, rightDepthDescFileName), descriptorHeader);

            Directory.CreateDirectory(Path.Join(Application.persistentDataPath, DirectoryName, leftDepthMapDirectoryName));
            Directory.CreateDirectory(Path.Join(Application.persistentDataPath, DirectoryName, rightDepthMapDirectoryName));
        }

        public void StopExport()
        {
            leftDepthCsvWriter?.Dispose();
            leftDepthCsvWriter = null;
            rightDepthCsvWriter?.Dispose();
            rightDepthCsvWriter = null;

            // Note: We keep depth enabled to avoid re-initialization overhead on next recording
        }

        // Single place that resolves the streamer reference. Called lazily so it catches
        // streamers created dynamically by RecordingManager after Start() has already run.
        private OpenQuestCaptureStreamer? ResolvePoseStreamer()
        {
            if (resolvedPoseStreamer != null) return resolvedPoseStreamer;
            resolvedPoseStreamer = poseStreamer != null
                ? poseStreamer
                : FindObjectOfType<OpenQuestCaptureStreamer>();
            return resolvedPoseStreamer;
        }

        private void Start()
        {
            depthDataExtractor = new();
            renderTextureExporter = new(copyDepthMapShader);
            ResolvePoseStreamer();

            Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission);

            Application.onBeforeRender += OnBeforeRender;
        }

        private void Update()
        {
            // prime the depth system — once a valid frame arrives, mark ready and stop polling
            if (!depthSystemReady && depthDataExtractor != null)
            {
                if (!hasScenePermission)
                {
                    hasScenePermission = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
                    if (!hasScenePermission) return;

                    depthDataExtractor.SetDepthEnabled(true);
                    Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter: scene permission granted, enabling depth");
                }

                if (depthDataExtractor.TryGetUpdatedDepthTexture(out var renderTexture, out var frameDescriptors))
                {
                    if (renderTexture != null && renderTexture.IsCreated())
                    {
                        depthSystemReady = true;
                        Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter: depth system ready");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            depthDataExtractor?.SetDepthEnabled(false);
            
            renderTextureExporter?.Dispose();
            renderTextureExporter = null;

            Application.onBeforeRender -= OnBeforeRender;
        }

        private void OnBeforeRender()
        {
            if (renderTextureExporter == null || depthDataExtractor == null
                || leftDepthCsvWriter == null || rightDepthCsvWriter == null)
            {
                return;
            }

            if (!captureTimer.IsCapturing || !captureTimer.ShouldCaptureThisFrame)
            {
                return;
            }
            
            if (!hasScenePermission)
            {
                hasScenePermission = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);

                if (hasScenePermission)
                {
                    depthDataExtractor.SetDepthEnabled(true);
                }
                else
                {
                    return;
                }
            }

            if (!depthSystemReady) return;

            if (depthDataExtractor.TryGetUpdatedDepthTexture(out var renderTexture, out var frameDescriptors))
            {
                const int FRAME_DESC_COUNT = 2;

                if (renderTexture == null || !renderTexture.IsCreated())
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] DepthMapExporter: RenderTexture is null or not created.");
                    return;
                }

                if (frameDescriptors.Length != FRAME_DESC_COUNT)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] DepthMapExporter: expected 2 depth frame descriptors, got {frameDescriptors.Length}.");
                    return;
                }

                var width = renderTexture.width;
                var height = renderTexture.height;

                var unixTime = ConvertTimestampNsToUnixTimeMs(frameDescriptors[0].timestampNs);

                var leftDepthFilePath = Path.Join(Application.persistentDataPath, DirectoryName, $"{leftDepthMapDirectoryName}/{unixTime}.raw");
                var rightDepthFilePath = Path.Join(Application.persistentDataPath, DirectoryName, $"{rightDepthMapDirectoryName}/{unixTime}.raw");

                // Build depth streaming callback if streamer is active
                Action<Unity.Collections.NativeArray<float>>? depthStreamCallback = null;
                var activeStreamer = ResolvePoseStreamer();
                if (streamDepth && activeStreamer != null && activeStreamer.IsStreaming)
                {
                    var leftDesc = frameDescriptors[0];
                    var capturedWidth = width;
                    var capturedHeight = height;
                    var capturedStreamer = activeStreamer;
                    depthStreamCallback = (floatData) =>
                    {
                        var uint16Bytes = new byte[floatData.Length * 2];
                        for (int i = 0; i < floatData.Length; i++)
                        {
                            ushort val = (ushort)(Mathf.Clamp01(floatData[i]) * 65535f);
                            uint16Bytes[i * 2]     = (byte)(val & 0xFF);
                            uint16Bytes[i * 2 + 1] = (byte)(val >> 8);
                        }
                        var payload = BuildDepthPayload(leftDesc, uint16Bytes, capturedWidth, capturedHeight);
                        capturedStreamer.SendDepthFrame(leftDesc.timestampNs, payload, capturedWidth, capturedHeight);
                    };
                }

                renderTextureExporter.Export(renderTexture, leftDepthFilePath, rightDepthFilePath, depthStreamCallback);

                for (var i = 0; i < FRAME_DESC_COUNT; ++i)
                {
                    var frameDesc = frameDescriptors[i];

                    var timestampMs = ConvertTimestampNsToUnixTimeMs(frameDesc.timestampNs);
                    var ovrTimestamp = frameDesc.timestampNs / 1.0e9;

                    var row = new double[]
                    {
                        timestampMs,
                        ovrTimestamp,
                        frameDesc.createPoseLocation.x, frameDesc.createPoseLocation.y, frameDesc.createPoseLocation.z,
                        frameDesc.createPoseRotation.x, frameDesc.createPoseRotation.y, frameDesc.createPoseRotation.z, frameDesc.createPoseRotation.w,
                        frameDesc.fovLeftAngleTangent, frameDesc.fovRightAngleTangent,
                        frameDesc.fovTopAngleTangent, frameDesc.fovDownAngleTangent,
                        frameDesc.nearZ, frameDesc.farZ,
                        width, height
                    };

                    if (i == 0)
                    {
                        SendLeftCameraPose(frameDesc);
                        leftDepthCsvWriter?.EnqueueRow(row);
                    }
                    else
                    {
                        rightDepthCsvWriter?.EnqueueRow(row);
                    }
                }
            }
        }

        private void SendLeftCameraPose(DepthFrameDesc frameDesc)
        {
            ResolvePoseStreamer()?.SendLeftCameraPose(
                frameDesc.timestampNs,
                frameDesc.createPoseLocation,
                frameDesc.createPoseRotation);
        }

        private long ConvertTimestampNsToUnixTimeMs(long timestampNs)
        {
            var deltaMs = (long) (timestampNs / 1.0e6 - baseOvrTimeSec * 1000.0);
            return baseUnixTimeMs + deltaMs;
        }

        private static byte[] BuildDepthPayload(DepthFrameDesc desc, byte[] uint16Pixels, int width, int height)
        {
            const uint   DepthMagic  = 0x50445351u;
            const ushort Version     = 1;
            const ushort ViewCount   = 1;
            const ushort Flags       = 0;
            const int    PrefixSize  = 36;
            const int    ViewSize    = 48;
            const int    HeaderSize  = PrefixSize + ViewSize; // 84
            const uint   ImageOffset = HeaderSize;

            // Convert OpenXR (Y-up, Z-back) to quest_corrected_hydra_optical.
            float px =  desc.createPoseLocation.x;
            float py = -desc.createPoseLocation.y;
            float pz = -desc.createPoseLocation.z;
            float qx =  desc.createPoseRotation.x;
            float qy = -desc.createPoseRotation.y;
            float qz = -desc.createPoseRotation.z;
            float qw =  desc.createPoseRotation.w;

            // DepthFrameDesc stores Tan(Abs(angle)); restore signed radian angles.
            // In OpenXR: angleLeft < 0, angleRight > 0, angleUp > 0, angleDown < 0.
            float angleLeft  = -Mathf.Atan(desc.fovLeftAngleTangent);
            float angleRight =  Mathf.Atan(desc.fovRightAngleTangent);
            float angleUp    =  Mathf.Atan(desc.fovTopAngleTangent);
            float angleDown  = -Mathf.Atan(desc.fovDownAngleTangent);

            var buf = new byte[HeaderSize + uint16Pixels.Length];
            int o = 0;

            // DEPTH_HEADER_PREFIX  (struct "<IHHHHIIQff", 36 bytes)
            WriteU32(buf, ref o, DepthMagic);
            WriteU16(buf, ref o, Version);
            WriteU16(buf, ref o, (ushort)HeaderSize);   // header_bytes
            WriteU16(buf, ref o, ViewCount);
            WriteU16(buf, ref o, Flags);
            WriteU32(buf, ref o, (uint)width);
            WriteU32(buf, ref o, (uint)height);
            WriteU64(buf, ref o, (ulong)desc.timestampNs);
            WriteF32(buf, ref o, desc.nearZ);
            WriteF32(buf, ref o, desc.farZ);

            // DEPTH_VIEW_HEADER  (struct "<fffffffffffI", 48 bytes, left view)
            WriteF32(buf, ref o, angleLeft);
            WriteF32(buf, ref o, angleRight);
            WriteF32(buf, ref o, angleUp);
            WriteF32(buf, ref o, angleDown);
            WriteF32(buf, ref o, qx);
            WriteF32(buf, ref o, qy);
            WriteF32(buf, ref o, qz);
            WriteF32(buf, ref o, qw);
            WriteF32(buf, ref o, px);
            WriteF32(buf, ref o, py);
            WriteF32(buf, ref o, pz);
            WriteU32(buf, ref o, ImageOffset);

            Buffer.BlockCopy(uint16Pixels, 0, buf, HeaderSize, uint16Pixels.Length);
            return buf;
        }

        private static void WriteU16(byte[] buf, ref int o, ushort v)
        {
            buf[o++] = (byte)(v & 0xff);
            buf[o++] = (byte)((v >> 8) & 0xff);
        }

        private static void WriteU32(byte[] buf, ref int o, uint v)
        {
            buf[o++] = (byte)(v & 0xff);
            buf[o++] = (byte)((v >> 8) & 0xff);
            buf[o++] = (byte)((v >> 16) & 0xff);
            buf[o++] = (byte)((v >> 24) & 0xff);
        }

        private static void WriteU64(byte[] buf, ref int o, ulong v)
        {
            WriteU32(buf, ref o, (uint)(v & 0xfffffffful));
            WriteU32(buf, ref o, (uint)((v >> 32) & 0xfffffffful));
        }

        private static void WriteF32(byte[] buf, ref int o, float v)
        {
            var bytes = BitConverter.GetBytes(v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buf, o, 4);
            o += 4;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            const string COPY_DEPTH_MAP_SHADER_PATH = "Assets/RealityLog/ComputeShaders/CopyDepthMap.compute";

            if (copyDepthMapShader == null)
            {
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(COPY_DEPTH_MAP_SHADER_PATH);
                if (shader == null)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] DepthMapExporter: failed to load ComputeShader at {COPY_DEPTH_MAP_SHADER_PATH}");
                }
                else
                {
                    copyDepthMapShader = shader;
                    Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter: loaded ComputeShader from {COPY_DEPTH_MAP_SHADER_PATH}");
                }
            }
        }
# endif
    }
}
