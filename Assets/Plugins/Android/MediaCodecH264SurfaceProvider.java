package com.realitylog.streaming;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.view.Surface;

import com.samusynth.questcamera.core.ISurfaceProvider;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.concurrent.ArrayBlockingQueue;

public class MediaCodecH264SurfaceProvider implements ISurfaceProvider {

    private static final String MIME_H264 = "video/avc";
    private static final int DEQUEUE_TIMEOUT_US = 0;
    // Match the actual camera repeating-request rate. KEY_FRAME_RATE is a bitrate-allocation hint
    // only; it does not gate how fast Camera2 pushes frames to the encoder surface. Setting it
    // equal to the actual input rate also avoids ambiguity with KEY_I_FRAME_INTERVAL=0: some
    // Qualcomm encoders derive IDR cadence as KEY_FRAME_RATE * KEY_I_FRAME_INTERVAL seconds,
    // which resolves to 0 when KEY_FRAME_RATE=5, triggering undefined behavior.
    private static final int ENCODER_FRAME_RATE = 30;

    private MediaCodec encoder;
    private Surface inputSurface;
    private final ArrayBlockingQueue<EncodedChunk> chunkQueue = new ArrayBlockingQueue<>(64);

    // Saved SPS+PPS from the CODEC_CONFIG buffer, prepended manually to every IDR frame.
    // prepend-sps-pps-to-idr-frames=1 is a non-standard Qualcomm key that is silently ignored
    // on some Quest firmware versions; manual prepending is the reliable fallback.
    private byte[] codecConfigBytes = null;

    // Timestamp throttle: only queue one IDR frame per minFrameIntervalUs microseconds.
    private final long minFrameIntervalUs;
    private long lastQueuedFrameUs = Long.MIN_VALUE;

    public MediaCodecH264SurfaceProvider(int width, int height, int fps, int bitrateBps)
            throws IOException {
        minFrameIntervalUs = fps > 0 ? 1_000_000L / fps : 0;

        MediaFormat format = MediaFormat.createVideoFormat(MIME_H264, width, height);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, bitrateBps);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, ENCODER_FRAME_RATE);
        // Every frame must be an IDR so each chunk is independently decodable — the backend
        // opens a fresh ffmpeg context per chunk and cannot handle inter-frame dependencies.
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 0);

        encoder = MediaCodec.createEncoderByType(MIME_H264);
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        inputSurface = encoder.createInputSurface();
        encoder.start();
    }

    @Override
    public Surface getSurface() {
        return inputSurface;
    }

    /** Drain all pending encoded NAL units from the encoder into the internal queue. */
    public void drainIntoQueue() {
        drain();
    }

    /** Return the oldest buffered chunk, or null if none. Call drainIntoQueue() first. */
    public EncodedChunk pollEncodedChunk() {
        return chunkQueue.poll();
    }

    /** Discard all buffered encoded chunks. Call when streaming starts to drop stale frames. */
    public void flushQueue() {
        chunkQueue.clear();
        lastQueuedFrameUs = Long.MIN_VALUE;
    }

    public void close() {
        if (encoder != null) {
            try {
                encoder.stop();
            } catch (IllegalStateException ignored) {
            }
            encoder.release();
            encoder = null;
        }
        if (inputSurface != null) {
            inputSurface.release();
            inputSurface = null;
        }
        chunkQueue.clear();
    }

    private void drain() {
        if (encoder == null) return;
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (true) {
            int index = encoder.dequeueOutputBuffer(info, DEQUEUE_TIMEOUT_US);
            if (index == MediaCodec.INFO_TRY_AGAIN_LATER
                    || index == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED) {
                break;
            }
            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                continue;
            }
            if (index < 0) break;

            boolean isConfig = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
            boolean isIDR    = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;

            if (isConfig && info.size > 0) {
                // Save SPS+PPS to prepend to every IDR frame.
                ByteBuffer buf = encoder.getOutputBuffer(index);
                if (buf != null) {
                    codecConfigBytes = new byte[info.size];
                    buf.position(info.offset);
                    buf.get(codecConfigBytes);
                }
            } else if (info.size > 0 && isIDR) {
                // Only queue IDR frames. Non-IDR (P/B) frames are useless to a stateless
                // backend decoder and must not be sent without the preceding IDR context.
                long presentationUs = info.presentationTimeUs;
                boolean withinRate = (minFrameIntervalUs == 0)
                        || (lastQueuedFrameUs == Long.MIN_VALUE)
                        || (presentationUs - lastQueuedFrameUs >= minFrameIntervalUs);

                if (withinRate) {
                    ByteBuffer buf = encoder.getOutputBuffer(index);
                    if (buf != null) {
                        byte[] frameBytes = new byte[info.size];
                        buf.position(info.offset);
                        buf.get(frameBytes);

                        byte[] toSend;
                        if (codecConfigBytes != null) {
                            // Prepend SPS+PPS so the backend's fresh decoder context can
                            // initialize from this chunk alone, without prior state.
                            toSend = new byte[codecConfigBytes.length + frameBytes.length];
                            System.arraycopy(codecConfigBytes, 0, toSend, 0, codecConfigBytes.length);
                            System.arraycopy(frameBytes, 0, toSend, codecConfigBytes.length, frameBytes.length);
                        } else {
                            toSend = frameBytes;
                        }

                        chunkQueue.offer(new EncodedChunk(presentationUs * 1000L, toSend));
                        lastQueuedFrameUs = presentationUs;
                    }
                }
            }

            encoder.releaseOutputBuffer(index, false);

            if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
        }
    }

    /** Carries one encoded IDR access unit and its camera timestamp (nanoseconds). */
    public static class EncodedChunk {
        public final long timestampNs;
        public final byte[] data;

        EncodedChunk(long timestampNs, byte[] data) {
            this.timestampNs = timestampNs;
            this.data = data;
        }
    }
}
