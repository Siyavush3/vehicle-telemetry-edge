namespace Wim.Protocol;

/// <summary>Command codes observed on the wire.</summary>
public enum Command : byte
{
    Weighing = 1,
    SensorStatus = 5,
    /// <summary>Sent by the device but carries no data the host needs (ignored in v1 as well).</summary>
    Keepalive = 6,
    /// <summary>Auxiliary status byte; meaning is device specific.</summary>
    AuxStatus = 9,
}

/// <summary>
/// One raw frame: <c>0xFF | address | command | length | payload…</c>,
/// where <c>length</c> is the total frame length including the start byte.
/// </summary>
public sealed record Frame(byte Address, byte Command, byte[] Payload)
{
    public const byte StartByte = 0xFF;
    public const int HeaderLength = 4;
    public const int MaxLength = byte.MaxValue;

    public int TotalLength => HeaderLength + Payload.Length;

    public override string ToString() =>
        $"Frame(cmd={Command}, addr={Address}, payload={Convert.ToHexString(Payload)})";
}
