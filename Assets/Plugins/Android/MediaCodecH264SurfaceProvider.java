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

    private MediaCodec encoder;
    private Surface inputSurface;
    private final ArrayBlockingQueue<EncodedChunk> chunkQueue = new ArrayBlockingQueue<>(64);

    public MediaCodecH264SurfaceProvider(int width, int height, int fps, int bitrateBps)
            throws IOException {
        MediaFormat format = MediaFormat.createVideoFormat(MIME_H264, width, height);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, bitrateBps);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        // Force IDR on every frame so each NAL unit is independently decodable by the backend.
        // The backend uses a fresh ffmpeg process per chunk, so SPS+PPS must be present in every chunk.
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 0);
        // Prepend SPS/PPS to every IDR frame (string key works on Quest 3 / Android 12+).
        format.setInteger("prepend-sps-pps-to-idr-frames", 1);

        encoder = MediaCodec.createEncoderByType(MIME_H264);
        encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        inputSurface = encoder.createInputSurface();
        encoder.start();
    }

    @Override
    public Surface getSurface() {
        return inputSurface;
    }

    /**
     * Drain any pending encoded NAL units into the internal queue, then return the oldest chunk.
     * Returns null when no encoded data is available yet.
     */
    public EncodedChunk pollEncodedChunk() {
        drain();
        return chunkQueue.poll();
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
            if (info.size > 0 && !isConfig) {
                ByteBuffer buf = encoder.getOutputBuffer(index);
                if (buf != null) {
                    byte[] bytes = new byte[info.size];
                    buf.position(info.offset);
                    buf.get(bytes);
                    // presentationTimeUs is the camera hardware timestamp in microseconds
                    chunkQueue.offer(new EncodedChunk(info.presentationTimeUs * 1000L, bytes));
                }
            }
            encoder.releaseOutputBuffer(index, false);

            if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
        }
    }

    /** Carries one encoded access unit and its camera timestamp (nanoseconds). */
    public static class EncodedChunk {
        public final long timestampNs;
        public final byte[] data;

        EncodedChunk(long timestampNs, byte[] data) {
            this.timestampNs = timestampNs;
            this.data = data;
        }
    }
}
