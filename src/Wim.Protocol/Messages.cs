namespace Wim.Protocol;

/// <summary>Sensor fault bits from the status byte. A set bit means the sensor reports a fault.</summary>
[Flags]
public enum SensorFaults : byte
{
    None = 0,
    WeighSensor = 1 << 0,
    Infrared = 1 << 1,
    InductionLoop = 1 << 2,
    TireSensor = 1 << 3,
    Communication = 1 << 4,
}

/// <summary>Status reported by the device (or synthesised by the host when the port is down).</summary>
public sealed record SensorStatus(byte Raw)
{
    /// <summary>Value the host uses when the scale is unreachable (same convention as v1).</summary>
    public static readonly SensorStatus Offline = new(0xFF);

    public bool IsOffline => Raw == 0xFF;
    public SensorFaults Faults => IsOffline ? (SensorFaults)0x1F : (SensorFaults)(Raw & 0x1F);
    public bool AllOk => !IsOffline && Faults == SensorFaults.None;
}

/// <summary>One vehicle pass measured by the weigh-in-motion scale.</summary>
public sealed record WeighingResult
{
    public required DateTime Timestamp { get; init; }
    public required SensorStatus Status { get; init; }
    /// <summary>Speed as reported by the device (raw value divided by 10).</summary>
    public required decimal Speed { get; init; }
    /// <summary>Raw acceleration byte as reported by the device.</summary>
    public required byte Acceleration { get; init; }
    public required IReadOnlyList<int> AxleWeights { get; init; }
    public required IReadOnlyList<int> AxleGroupWeights { get; init; }
    public required IReadOnlyList<byte> AxleGroupTypes { get; init; }
    /// <summary>Distances between consecutive axles; empty if the device does not send them.</summary>
    public required IReadOnlyList<int> AxleDistances { get; init; }

    public int AxleCount => AxleWeights.Count;
    public int TotalWeight => AxleWeights.Sum();
}

/// <summary>Thrown when a frame is well-formed but its payload cannot be interpreted.</summary>
public sealed class ProtocolException(string message) : Exception(message);
