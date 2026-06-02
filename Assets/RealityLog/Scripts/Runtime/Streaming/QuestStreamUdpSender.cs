# nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RealityLog.Streaming
{
    public sealed class QuestStreamUdpSender : IDisposable
    {
        public enum StreamType : ushort
        {
            Status = 0,
            Camera = 1,
            Pose = 2,
            Depth = 3,
        }

        public enum PayloadFormat : ushort
        {
            Utf8 = 1,
            CameraI420 = 10,
            CameraH265 = 11,
            CameraH264 = 12,
            PoseOpenXrBinary = 20,
            DepthFloat32Meters = 30,
            DepthUint16Millimeters = 31,
            DepthUint16OpenXrZ = 32,
            DepthUint16OpenXrZLz4 = 33,
            DepthUint16OpenXrZstdPredictor = 34,
        }

        private const uint PacketMagic = 0x52545351u;
        private const uint PosePayloadMagic = 0x4f505351u;
        private const ushort ProtocolVersion = 2;
        private const ushort HeaderBytes = 48;
        private const ushort PosePayloadVersion = 1;
        private const ushort PosePayloadBytes = 52;
        private const int DefaultDatagramBytes = 1200;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly UdpClient client;
        private readonly IPEndPoint endpoint;
        private readonly int fragmentPayloadBytes;

        private uint sequence;
        private uint frameId;
        private bool disposed;

        public QuestStreamUdpSender(string host, int port, int datagramBytes = DefaultDatagramBytes)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host must not be empty.", nameof(host));
            }

            if (port <= 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be in range 1-65535.");
            }

            if (datagramBytes <= HeaderBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(datagramBytes), "Datagram size must exceed the packet header size.");
            }

            endpoint = new IPEndPoint(ParseAddress(host), port);
            client = new UdpClient(AddressFamily.InterNetwork);
            fragmentPayloadBytes = datagramBytes - HeaderBytes;
        }

        public bool IsOpen => !disposed;

        public void SendStatus(string message)
        {
            var payload = Encoding.UTF8.GetBytes(message ?? string.Empty);
            SendPayload(StreamType.Status, PayloadFormat.Utf8, WallClockTimestampNs(), 0, 0, payload);
        }

        public void SendOpenXrPose(
            ulong timestampNs,
            ulong locationFlags,
            float orientationX,
            float orientationY,
            float orientationZ,
            float orientationW,
            float positionX,
            float positionY,
            float positionZ)
        {
            var payload = new byte[PosePayloadBytes];
            WriteUInt32(payload, 0, PosePayloadMagic);
            WriteUInt16(payload, 4, PosePayloadVersion);
            WriteUInt16(payload, 6, PosePayloadBytes);
            WriteUInt64(payload, 8, timestampNs);
            WriteUInt64(payload, 16, locationFlags);
            WriteFloat32(payload, 24, orientationX);
            WriteFloat32(payload, 28, orientationY);
            WriteFloat32(payload, 32, orientationZ);
            WriteFloat32(payload, 36, orientationW);
            WriteFloat32(payload, 40, positionX);
            WriteFloat32(payload, 44, positionY);
            WriteFloat32(payload, 48, positionZ);

            SendPayload(StreamType.Pose, PayloadFormat.PoseOpenXrBinary, timestampNs, 0, 0, payload);
        }

        public void SendPayload(
            StreamType stream,
            PayloadFormat format,
            ulong timestampNs,
            ushort width,
            ushort height,
            byte[] payload)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(QuestStreamUdpSender));
            }

            payload ??= Array.Empty<byte>();
            if ((ulong) payload.LongLength > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "Payload is too large for QuestStreamer protocol.");
            }

            var currentFrame = frameId++;
            var fragmentCount = Math.Max(1, (payload.Length + fragmentPayloadBytes - 1) / fragmentPayloadBytes);
            if (fragmentCount > ushort.MaxValue)
            {
                throw new InvalidOperationException("Payload requires too many UDP fragments.");
            }

            for (var fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                var offset = fragmentIndex * fragmentPayloadBytes;
                var bytesThisFragment = Math.Min(fragmentPayloadBytes, payload.Length - offset);
                var datagram = new byte[HeaderBytes + bytesThisFragment];

                WriteHeader(
                    datagram,
                    stream,
                    format,
                    flags: 0,
                    sequence: sequence++,
                    frameId: currentFrame,
                    timestampNs: timestampNs,
                    totalBytes: (uint) payload.Length,
                    fragmentOffset: (uint) offset,
                    width: width,
                    height: height,
                    fragmentIndex: (ushort) fragmentIndex,
                    fragmentCount: (ushort) fragmentCount);

                if (bytesThisFragment > 0)
                {
                    Buffer.BlockCopy(payload, offset, datagram, HeaderBytes, bytesThisFragment);
                }

                client.Send(datagram, datagram.Length, endpoint);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            client.Dispose();
        }

        private static IPAddress ParseAddress(string host)
        {
            if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                return address;
            }

            var addresses = Dns.GetHostAddresses(host);
            foreach (var candidate in addresses)
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    return candidate;
                }
            }

            throw new ArgumentException($"Could not resolve IPv4 address for host '{host}'.", nameof(host));
        }

        private static ulong WallClockTimestampNs()
        {
            return (ulong) (DateTime.UtcNow - UnixEpochUtc).Ticks * 100UL;
        }

        private static void WriteHeader(
            byte[] target,
            StreamType stream,
            PayloadFormat format,
            uint flags,
            uint sequence,
            uint frameId,
            ulong timestampNs,
            uint totalBytes,
            uint fragmentOffset,
            ushort width,
            ushort height,
            ushort fragmentIndex,
            ushort fragmentCount)
        {
            WriteUInt32(target, 0, PacketMagic);
            WriteUInt16(target, 4, ProtocolVersion);
            WriteUInt16(target, 6, HeaderBytes);
            WriteUInt16(target, 8, (ushort) stream);
            WriteUInt16(target, 10, (ushort) format);
            WriteUInt32(target, 12, flags);
            WriteUInt32(target, 16, sequence);
            WriteUInt32(target, 20, frameId);
            WriteUInt64(target, 24, timestampNs);
            WriteUInt32(target, 32, totalBytes);
            WriteUInt32(target, 36, fragmentOffset);
            WriteUInt16(target, 40, width);
            WriteUInt16(target, 42, height);
            WriteUInt16(target, 44, fragmentIndex);
            WriteUInt16(target, 46, fragmentCount);
        }

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte) (value & 0xffu);
            target[offset + 1] = (byte) ((value >> 8) & 0xffu);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte) (value & 0xffu);
            target[offset + 1] = (byte) ((value >> 8) & 0xffu);
            target[offset + 2] = (byte) ((value >> 16) & 0xffu);
            target[offset + 3] = (byte) ((value >> 24) & 0xffu);
        }

        private static void WriteUInt64(byte[] target, int offset, ulong value)
        {
            WriteUInt32(target, offset, (uint) (value & 0xfffffffful));
            WriteUInt32(target, offset + 4, (uint) ((value >> 32) & 0xfffffffful));
        }

        private static void WriteFloat32(byte[] target, int offset, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            Buffer.BlockCopy(bytes, 0, target, offset, sizeof(float));
        }
    }
}
