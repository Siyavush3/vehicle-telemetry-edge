using System.IO.Ports;
using Wim.Protocol;

namespace Wim.Serial;

public sealed record ScaleReaderOptions
{
    /// <summary>COM port to use; if null, ports are probed until one sends a valid frame.</summary>
    public string? PortName { get; init; }
    public int BaudRate { get; init; } = 9600;
    public Parity Parity { get; init; } = Parity.None;
    public int DataBits { get; init; } = 8;
    public StopBits StopBits { get; init; } = StopBits.One;
    /// <summary>How long to listen on each port while auto-detecting.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A partial frame older than this is treated as garbage and skipped.</summary>
    public TimeSpan StalledFrameTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Delay before reopening the port after an error.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Reads a weigh-in-motion scale over a serial port: finds the port, decodes frames,
/// acknowledges weighings and reconnects after failures. Runs until cancelled.
/// </summary>
public sealed class ScaleReader(ScaleReaderOptions options)
{
    private SensorStatus? _lastStatus;

    public event Action<WeighingResult>? WeighingReceived;
    public event Action<SensorStatus>? StatusChanged;
    public event Action<string>? Log;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? portName = options.PortName ?? await DetectPortAsync(ct);
                if (portName is null)
                {
                    PublishStatus(SensorStatus.Offline);
                    Log?.Invoke("No scale found on any COM port.");
                    await Task.Delay(options.ReconnectDelay, ct);
                    continue;
                }

                using var port = Open(portName);
                Log?.Invoke($"Connected to scale on {portName}.");
                await ReadLoopAsync(port, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                PublishStatus(SensorStatus.Offline);
                Log?.Invoke($"Serial error: {ex.Message}. Reconnecting in {options.ReconnectDelay.TotalSeconds:0} s.");
                await Task.Delay(options.ReconnectDelay, ct);
            }
        }
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken ct)
    {
        var decoder = new FrameDecoder();
        var buffer = new byte[256];
        var lastProgress = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            int read = await ReadWithTimeoutAsync(port.BaseStream, buffer, options.StalledFrameTimeout, ct);
            if (read > 0)
            {
                var frames = decoder.Push(buffer.AsSpan(0, read));
                if (frames.Count > 0) lastProgress = DateTime.UtcNow;
                foreach (var frame in frames)
                    await HandleFrameAsync(port, frame, ct);
            }

            if (decoder.PendingBytes > 0 && DateTime.UtcNow - lastProgress > options.StalledFrameTimeout)
            {
                Log?.Invoke($"Partial frame stalled ({decoder.PendingBytes} bytes); resynchronising.");
                decoder.SkipCurrentFrame();
                lastProgress = DateTime.UtcNow;
            }
        }
    }

    private async Task HandleFrameAsync(SerialPort port, Frame frame, CancellationToken ct)
    {
        switch ((Command)frame.Command)
        {
            case Command.Weighing:
                bool ok;
                try
                {
                    var result = MessageCodec.DecodeWeighing(frame);
                    PublishStatus(result.Status);
                    WeighingReceived?.Invoke(result);
                    ok = true;
                }
                catch (ProtocolException ex)
                {
                    Log?.Invoke($"Rejected weighing frame: {ex.Message}");
                    ok = false;
                }
                // The scale waits for an acknowledgement; negative ack asks it to resend.
                await port.BaseStream.WriteAsync(MessageCodec.EncodeAck(Command.Weighing, ok), ct);
                break;

            case Command.SensorStatus:
                PublishStatus(MessageCodec.DecodeStatus(frame));
                break;

            case Command.Keepalive:
            case Command.AuxStatus:
                break;

            default:
                Log?.Invoke($"Unknown command {frame.Command}: {frame}");
                break;
        }
    }

    private void PublishStatus(SensorStatus status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private async Task<string?> DetectPortAsync(CancellationToken ct)
    {
        foreach (var name in SerialPort.GetPortNames())
        {
            try
            {
                using var port = Open(name);
                var decoder = new FrameDecoder();
                var buffer = new byte[256];
                var deadline = DateTime.UtcNow + options.ProbeTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    int read = await ReadWithTimeoutAsync(port.BaseStream, buffer, deadline - DateTime.UtcNow, ct);
                    if (read > 0 && decoder.Push(buffer.AsSpan(0, read)).Count > 0)
                        return name;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"Skipping {name}: {ex.Message}");
            }
        }
        return null;
    }

    private SerialPort Open(string name)
    {
        var port = new SerialPort(name, options.BaudRate, options.Parity, options.DataBits, options.StopBits);
        port.Open();
        return port;
    }

    private static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return 0; // timeout, not shutdown
        }
    }
}
