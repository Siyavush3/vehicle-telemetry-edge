namespace Wim.Protocol;

/// <summary>
/// CRC-16/XMODEM (polynomial 0x1021, initial value 0x0000, no reflection, no final XOR).
/// Used by the scale for acknowledgement frames; transmitted high byte first.
/// </summary>
public static class Crc16Xmodem
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }
}
