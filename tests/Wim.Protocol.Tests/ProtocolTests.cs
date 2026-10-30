using Wim.Protocol;
using Xunit;

namespace Wim.Protocol.Tests;

public class Crc16XmodemTests
{
    [Fact]
    public void Matches_standard_check_value()
    {
        // CRC-16/XMODEM check value for "123456789" is 0x31C3.
        Assert.Equal(0x31C3, Crc16Xmodem.Compute("123456789"u8));
    }

    [Fact]
    public void Ack_is_compatible_with_v1_byte_order()
    {
        // v1 swapped the CRC bytes and wrote them little-endian, i.e. high byte first.
        var ack = MessageCodec.EncodeAck(Command.Weighing, success: true);
        ushort crc = Crc16Xmodem.Compute([0xFF, 0x00, 0x01, 0x00]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x01, 0x00, (byte)(crc >> 8), (byte)crc }, ack);
    }

    [Fact]
    public void Negative_ack_sets_result_byte()
    {
        Assert.Equal(1, MessageCodec.EncodeAck(Command.Weighing, success: false)[3]);
    }
}

public class FrameDecoderTests
{
    private static byte[] Status(byte s) => MessageCodec.EncodeFrame(MessageCodec.BuildStatusFrame(s));

    [Fact]
    public void Decodes_a_single_frame()
    {
        var frames = new FrameDecoder().Push(Status(0x04));
        var frame = Assert.Single(frames);
        Assert.Equal((byte)Command.SensorStatus, frame.Command);
        Assert.Equal(new byte[] { 0x04 }, frame.Payload);
    }

    [Fact]
    public void Reassembles_a_frame_split_across_reads()
    {
        var bytes = MessageCodec.EncodeFrame(MessageCodec.BuildWeighingFrame(Samples.Truck()));
        var decoder = new FrameDecoder();
        for (int i = 0; i < bytes.Length - 1; i++)
            Assert.Empty(decoder.Push(bytes.AsSpan(i, 1)));
        Assert.Single(decoder.Push(bytes.AsSpan(^1)));
        Assert.Equal(0, decoder.PendingBytes);
    }

    [Fact]
    public void Decodes_several_frames_from_one_read()
    {
        var bytes = Status(0).Concat(Status(1)).Concat(Status(2)).ToArray();
        var frames = new FrameDecoder().Push(bytes);
        Assert.Equal(new byte[] { 0, 1, 2 }, frames.Select(f => f.Payload[0]));
    }

    [Fact]
    public void Handles_0xFF_inside_payload()
    {
        // v1 searched for the next 0xFF to find the frame end and broke on this input.
        var frame = new Frame(0, (byte)Command.AuxStatus, [0xFF, 0x10, 0xFF]);
        var decoded = Assert.Single(new FrameDecoder().Push(MessageCodec.EncodeFrame(frame)));
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public void Skips_garbage_before_a_frame()
    {
        var bytes = new byte[] { 0x00, 0x13, 0x37 }.Concat(Status(0)).ToArray();
        var decoder = new FrameDecoder();
        Assert.Single(decoder.Push(bytes));
        Assert.Equal(3, decoder.DiscardedBytes);
    }

    [Fact]
    public void Rejects_impossible_length_and_resyncs()
    {
        // 0xFF followed by length 2 cannot be a frame header.
        var bytes = new byte[] { 0xFF, 0x00, 0x05, 0x02 }.Concat(Status(7)).ToArray();
        var frame = Assert.Single(new FrameDecoder().Push(bytes));
        Assert.Equal(7, frame.Payload[0]);
    }

    [Fact]
    public void Waits_for_truncated_frame_and_can_skip_it()
    {
        var decoder = new FrameDecoder();
        Assert.Empty(decoder.Push(new byte[] { 0xFF, 0x00, 0x01, 0x40, 0x01 })); // claims 64 bytes
        Assert.Equal(5, decoder.PendingBytes);

        decoder.SkipCurrentFrame(); // what the reader does after a timeout
        var frame = Assert.Single(decoder.Push(Status(3)));
        Assert.Equal(3, frame.Payload[0]);
    }

    [Fact]
    public void Header_bytes_are_not_read_before_they_arrive()
    {
        // v1 read buffer[pos + 3] immediately after finding 0xFF.
        Assert.Empty(new FrameDecoder().Push(new byte[] { 0xFF }));
    }
}

public class MessageCodecTests
{
    [Fact]
    public void Weighing_round_trip()
    {
        var truck = Samples.Truck();
        var decoded = MessageCodec.DecodeWeighing(
            Assert.Single(new FrameDecoder().Push(MessageCodec.EncodeFrame(MessageCodec.BuildWeighingFrame(truck)))));

        Assert.Equal(truck.Timestamp, decoded.Timestamp);
        Assert.Equal(truck.Speed, decoded.Speed);
        Assert.Equal(truck.AxleWeights, decoded.AxleWeights);
        Assert.Equal(truck.AxleGroupWeights, decoded.AxleGroupWeights);
        Assert.Equal(truck.AxleGroupTypes, decoded.AxleGroupTypes);
        Assert.Equal(truck.AxleDistances, decoded.AxleDistances);
        Assert.Equal(truck.AxleWeights.Sum(), decoded.TotalWeight);
    }

    [Fact]
    public void Distances_are_optional()
    {
        var truck = Samples.Truck() with { AxleDistances = [] };
        var decoded = MessageCodec.DecodeWeighing(MessageCodec.BuildWeighingFrame(truck));
        Assert.Empty(decoded.AxleDistances);
    }

    [Fact]
    public void Zero_axles_is_a_protocol_error_not_a_crash()
    {
        // v1 executed new int[NumberOfAxles - 1] and threw on this input.
        var frame = MessageCodec.BuildWeighingFrame(Samples.Truck() with
        {
            AxleWeights = [], AxleGroupWeights = [], AxleGroupTypes = [], AxleDistances = [],
        });
        Assert.Throws<ProtocolException>(() => MessageCodec.DecodeWeighing(frame));
    }

    [Fact]
    public void Truncated_payload_is_a_protocol_error()
    {
        var frame = MessageCodec.BuildWeighingFrame(Samples.Truck());
        var cut = frame with { Payload = frame.Payload[..20] };
        Assert.Throws<ProtocolException>(() => MessageCodec.DecodeWeighing(cut));
    }

    [Fact]
    public void Invalid_date_is_a_protocol_error()
    {
        var frame = MessageCodec.BuildWeighingFrame(Samples.Truck());
        frame.Payload[2] = 13; // month 13
        Assert.Throws<ProtocolException>(() => MessageCodec.DecodeWeighing(frame));
    }

    [Theory]
    [InlineData(0x00, SensorFaults.None)]
    [InlineData(0x02, SensorFaults.Infrared)]
    [InlineData(0x14, SensorFaults.InductionLoop | SensorFaults.Communication)]
    public void Status_bits_map_to_faults(byte raw, SensorFaults expected)
    {
        var status = MessageCodec.DecodeStatus(MessageCodec.BuildStatusFrame(raw));
        Assert.Equal(expected, status.Faults);
        Assert.Equal(expected == SensorFaults.None, status.AllOk);
    }

    [Fact]
    public void Offline_status_reports_every_fault()
    {
        Assert.True(SensorStatus.Offline.IsOffline);
        Assert.False(SensorStatus.Offline.AllOk);
    }
}

internal static class Samples
{
    public static WeighingResult Truck() => new()
    {
        Timestamp = new DateTime(2026, 10, 30, 11, 51, 7),
        Status = new SensorStatus(0),
        Speed = 42.5m,
        Acceleration = 3,
        AxleWeights = [6200, 9800, 9750, 8100, 8050],
        AxleGroupWeights = [6200, 19550, 16150],
        AxleGroupTypes = [1, 2, 2],
        AxleDistances = [3600, 1350, 5200, 1310],
    };
}
