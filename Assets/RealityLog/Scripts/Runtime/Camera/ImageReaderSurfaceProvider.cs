# nullable enable

using System;
using System.IO;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Camera
{
    public class ImageReaderSurfaceProvider : SurfaceProviderBase
    {
        private const string IMAGE_READER_SURFACE_PROVIDER_CLASS_NAME = "com.samusynth.questcamera.io.ImageReaderSurfaceProvider";
        
        private const string UPDATE_DIRECTORY_PATHS_METHOD_NAME = "updateDirectoryPaths";
        private const string CAPTURE_NEXT_FRAME_METHOD_NAME = "captureNextFrame";
        private const string CLOSE_METHOD_NAME = "close";

        [SerializeField] private string dataDirectoryName = string.Empty;
        [SerializeField] private string imageSubdirName = "left_camera";
        [SerializeField] private string cameraMetaDataFileName = "left_camera_characteristics.json";
        [SerializeField] private string formatInfoFileName = "left_camera_image_format.json";
        [SerializeField] private int bufferPoolSize = 5;
        [Header("Synchronized Capture")]
        [Tooltip("Required: Reference to CaptureTimer for FPS-based capture timing.")]
        [SerializeField] private CaptureTimer captureTimer = default!;

        private AndroidJavaObject? currentInstance;
        private CameraMetadata? cameraMetadata;

        public string DataDirectoryName
        {
            get => dataDirectoryName;
            set => dataDirectoryName = value;
        }

        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata)
        {
            Close();

            // stored here but not written — dataDirectoryName may be empty at startup
            cameraMetadata = metadata;

            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);

            var imageFileDirPath = Path.Join(dataDirPath, imageSubdirName);
            var formatInfoFilePath = Path.Join(dataDirPath, formatInfoFileName);

            var size = metadata.sensor.pixelArraySize;

            currentInstance = new AndroidJavaObject(
                IMAGE_READER_SURFACE_PROVIDER_CLASS_NAME,
                size.width,
                size.height,
                imageFileDirPath,
                formatInfoFilePath,
                bufferPoolSize
            );

            Debug.Log($"[{Constants.LOG_TAG}] ImageReaderSurfaceProvider: camera initialized");

            return currentInstance;
        }

        public void UpdateDirectoryPaths()
        {
            if (currentInstance != null)
            {
                var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
                var imageFileDirPath = Path.Join(dataDirPath, imageSubdirName);
                var formatInfoFilePath = Path.Join(dataDirPath, formatInfoFileName);

                currentInstance.Call(UPDATE_DIRECTORY_PATHS_METHOD_NAME, imageFileDirPath, formatInfoFilePath);
                Debug.Log($"[{Constants.LOG_TAG}] ImageReaderSurfaceProvider: updated directory paths for session '{dataDirectoryName}'");

                if (cameraMetadata != null)
                {
                    var metaDataFilePath = Path.Join(dataDirPath, cameraMetaDataFileName);
                    var metaDataJson = JsonUtility.ToJson(cameraMetadata);
                    try
                    {
                        File.WriteAllText(metaDataFilePath, metaDataJson);
                        Debug.Log($"[{Constants.LOG_TAG}] ImageReaderSurfaceProvider: wrote camera characteristics to '{metaDataFilePath}'");
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }

        private void LateUpdate()
        {
            // LateUpdate so CaptureTimer.Update() has already set ShouldCaptureThisFrame
            if (currentInstance == null || captureTimer == null)
                return;

            if (captureTimer.IsCapturing && captureTimer.ShouldCaptureThisFrame)
            {
                currentInstance.Call(CAPTURE_NEXT_FRAME_METHOD_NAME);
            }
        }

        private void OnDestroy()
        {
            Close();
        }

        private void Close()
        {
            currentInstance?.Call(CLOSE_METHOD_NAME);
            currentInstance?.Dispose();
            currentInstance = null;
        }
    }
}