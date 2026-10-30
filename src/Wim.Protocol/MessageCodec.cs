using System.Buffers.Binary;

namespace Wim.Protocol;

/// <summary>Decodes frame payloads into typed messages and builds frames to send.</summary>
public static class MessageCodec
{
    // Offsets inside the weighing payload (frame offset minus the 4-byte header).
    private const int FixedWeighingPart = 13; // date(7) + status(1) + speed(2) + accel(1) + axles(1) + groups(1)

    public static SensorStatus DecodeStatus(Frame frame)
    {
        if (frame.Payload.Length < 1)
            throw new ProtocolException("Status frame has no payload.");
        return new SensorStatus(frame.Payload[0]);
    }

    public static WeighingResult DecodeWeighing(Frame frame)
    {
        ReadOnlySpan<byte> p = frame.Payload;
        if (p.Length < FixedWeighingPart)
            throw new ProtocolException($"Weighing payload too short: {p.Length} bytes.");

        DateTime timestamp;
        try
        {
            timestamp = new DateTime(BinaryPrimitives.ReadUInt16BigEndian(p), p[2], p[3], p[4], p[5], p[6]);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ProtocolException($"Invalid timestamp bytes: {Convert.ToHexString(p[..7])}.");
        }

        var status = new SensorStatus(p[7]);
        decimal speed = BinaryPrimitives.ReadUInt16BigEndian(p[8..]) / 10m;
        byte acceleration = p[10];
        int axles = p[11];
        int groups = p[12];

        if (axles == 0)
            throw new ProtocolException("Weighing result with zero axles.");

        int required = FixedWeighingPart + axles * 2 + groups * 2 + groups;
        if (p.Length < required)
            throw new ProtocolException($"Payload has {p.Length} bytes, {required} required for {axles} axles / {groups} groups.");

        var reader = new SpanReader(p[FixedWeighingPart..]);
        var axleWeights = reader.ReadUInt16Array(axles);
        var groupWeights = reader.ReadUInt16Array(groups);
        var groupTypes = reader.ReadBytes(groups);

        // Distances are optional: present only when the device is configured to send them.
        var distances = reader.Remaining >= (axles - 1) * 2
            ? reader.ReadUInt16Array(axles - 1)
            : Array.Empty<int>();

        return new WeighingResult
        {
            Timestamp = timestamp,
            Status = status,
            Speed = speed,
            Acceleration = acceleration,
            AxleWeights = axleWeights,
            AxleGroupWeights = groupWeights,
            AxleGroupTypes = groupTypes,
            AxleDistances = distances,
        };
    }

    /// <summary>
    /// Acknowledgement sent back to the scale: <c>0xFF 0x00 command result CRC_hi CRC_lo</c>,
    /// CRC-16/XMODEM over the first four bytes. Result 0 = accepted, 1 = rejected.
    /// </summary>
    public static byte[] EncodeAck(Command command, bool success)
    {
        var ack = new byte[6];
        ack[0] = Frame.StartByte;
        ack[1] = 0;
        ack[2] = (byte)command;
        ack[3] = success ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4), Crc16Xmodem.Compute(ack.AsSpan(0, 4)));
        return ack;
    }

    /// <summary>Serialises a frame (used by the simulator and tests).</summary>
    public static byte[] EncodeFrame(Frame frame)
    {
        if (frame.TotalLength > Frame.MaxLength)
            throw new ArgumentException($"Frame too long: {frame.TotalLength} bytes.", nameof(frame));
        var bytes = new byte[frame.TotalLength];
        bytes[0] = Frame.StartByte;
        bytes[1] = frame.Address;
        bytes[2] = frame.Command;
        bytes[3] = (byte)frame.TotalLength;
        frame.Payload.CopyTo(bytes, Frame.HeaderLength);
        return bytes;
    }

    public static Frame BuildWeighingFrame(WeighingResult r, byte address = 0)
    {
        var payload = new List<byte>(64);
        void U16(int v) { payload.Add((byte)(v >> 8)); payload.Add((byte)v); }

        U16(r.Timestamp.Year);
        payload.AddRange([(byte)r.Timestamp.Month, (byte)r.Timestamp.Day, (byte)r.Timestamp.Hour,
                          (byte)r.Timestamp.Minute, (byte)r.Timestamp.Second, r.Status.Raw]);
        U16((int)(r.Speed * 10));
        payload.Add(r.Acceleration);
        payload.Add((byte)r.AxleWeights.Count);
        payload.Add((byte)r.AxleGroupWeights.Count);
        foreach (var w in r.AxleWeights) U16(w);
        foreach (var w in r.AxleGroupWeights) U16(w);
        payload.AddRange(r.AxleGroupTypes);
        foreach (var d in r.AxleDistances) U16(d);
        return new Frame(address, (byte)Command.Weighing, payload.ToArray());
    }

    public static Frame BuildStatusFrame(byte status, byte address = 0) =>
        new(address, (byte)Command.SensorStatus, [status]);

    private ref struct SpanReader(ReadOnlySpan<byte> span)
    {
        private ReadOnlySpan<byte> _span = span;
        public readonly int Remaining => _span.Length;

        public int[] ReadUInt16Array(int n)
        {
            var result = new int[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = BinaryPrimitives.ReadUInt16BigEndian(_span);
                _span = _span[2..];
            }
            return result;
        }

        public byte[] ReadBytes(int n)
        {
            var result = _span[..n].ToArray();
            _span = _span[n..];
            return result;
        }
    }
}
