# nullable enable

using UnityEngine;

namespace RealityLog.Common
{
    public class CaptureTimer : MonoBehaviour
    {
        [Header("Capture Timing")]
        [Tooltip("Target FPS for synchronized camera and depth capture. Set to 0 to capture at maximum rate (~25 FPS)")]
        [SerializeField] private float targetCaptureFPS = 15f;

        private float lastCaptureTime = 0f;
        private float captureInterval = 0f;
        private bool shouldCaptureThisFrame = false;
        private bool isCapturing = false;

        public bool ShouldCaptureThisFrame => shouldCaptureThisFrame;
        public bool IsCapturing => isCapturing;
        public float TargetCaptureFPS => targetCaptureFPS;

        public void StartCapture()
        {
            isCapturing = true;
            captureInterval = (targetCaptureFPS > 0) ? (1f / targetCaptureFPS) : 0f;
            // set to (now - interval) so first Update() fires immediately
            lastCaptureTime = Time.unscaledTime - captureInterval;
            shouldCaptureThisFrame = false;
            
            Debug.Log($"[{Constants.LOG_TAG}] CaptureTimer: started at {targetCaptureFPS} FPS (interval: {captureInterval}s)");
        }

        public void StopCapture()
        {
            isCapturing = false;
            shouldCaptureThisFrame = false;
            
            Debug.Log($"[{Constants.LOG_TAG}] CaptureTimer: stopped");
        }

        private void Update()
        {
            if (!isCapturing)
            {
                shouldCaptureThisFrame = false;
                return;
            }

            if (captureInterval <= 0f)
            {
                shouldCaptureThisFrame = true;
                return;
            }

            float currentTime = Time.unscaledTime;
            if ((currentTime - lastCaptureTime) >= captureInterval)
            {
                shouldCaptureThisFrame = true;
                lastCaptureTime = currentTime;
            }
            else
            {
                shouldCaptureThisFrame = false;
            }
        }
    }
}

