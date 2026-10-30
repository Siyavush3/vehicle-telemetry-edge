using System.IO.Ports;
using Wim.Protocol;
using Wim.Serial;

// Usage:
//   wim-sim hex [count]                 print random frames as hex (no hardware needed)
//   wim-sim scale <COMx> [intervalMs]   act as the scale: send frames to a COM port and print acks
//   wim-sim host  [COMx]                act as the host: read a real or simulated scale
//
// To test without hardware, create a virtual COM pair (e.g. com0com on Windows, socat on Linux)
// and run "scale" on one end and "host" on the other.

var mode = args.Length > 0 ? args[0] : "hex";
var rng = new Random(42);

switch (mode)
{
    case "hex":
        int count = args.Length > 1 ? int.Parse(args[1]) : 3;
        for (int i = 0; i < count; i++)
        {
            var frame = MessageCodec.BuildWeighingFrame(RandomVehicle(rng));
            Console.WriteLine(Convert.ToHexString(MessageCodec.EncodeFrame(frame)));
        }
        break;

    case "scale":
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: scale <COMx> [intervalMs]"); return 1; }
        string portName = args[1];
        int interval = args.Length > 2 ? int.Parse(args[2]) : 2000;
        using var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One);
        port.Open();
        Console.WriteLine($"Simulated scale on {portName}. Ctrl+C to stop.");
        while (true)
        {
            var status = MessageCodec.EncodeFrame(MessageCodec.BuildStatusFrame(0));
            port.Write(status, 0, status.Length);
            var vehicle = RandomVehicle(rng);
            var bytes = MessageCodec.EncodeFrame(MessageCodec.BuildWeighingFrame(vehicle));
            // Send in two chunks to exercise reassembly on the host side.
            port.Write(bytes, 0, bytes.Length / 2);
            await Task.Delay(50);
            port.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
            Console.WriteLine($"-> {vehicle.AxleCount} axles, total {vehicle.TotalWeight}, speed {vehicle.Speed}");
            await Task.Delay(interval);
            if (port.BytesToRead > 0)
            {
                var ack = new byte[port.BytesToRead];
                port.Read(ack, 0, ack.Length);
                Console.WriteLine($"<- ack {Convert.ToHexString(ack)}");
            }
        }
    }

    case "host":
    {
        var reader = new ScaleReader(new ScaleReaderOptions { PortName = args.Length > 1 ? args[1] : null });
        reader.Log += msg => Console.WriteLine($"[log] {msg}");
        reader.StatusChanged += s => Console.WriteLine($"[status] offline={s.IsOffline} faults={s.Faults}");
        reader.WeighingReceived += r => Console.WriteLine(
            $"[weighing] {r.Timestamp:yyyy-MM-dd HH:mm:ss} axles={r.AxleCount} total={r.TotalWeight} speed={r.Speed}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await reader.RunAsync(cts.Token);
        break;
    }

    default:
        Console.Error.WriteLine("Unknown mode. Use: hex [count] | scale <COMx> [ms] | host [COMx]");
        return 1;
}
return 0;

static WeighingResult RandomVehicle(Random rng)
{
    int axles = rng.Next(2, 7);
    var axleWeights = Enumerable.Range(0, axles).Select(_ => rng.Next(3000, 11000)).ToArray();
    // Simple grouping: front axle alone, the rest in pairs.
    var groups = new List<int[]> { axleWeights[..1] };
    for (int i = 1; i < axles; i += 2) groups.Add(axleWeights[i..Math.Min(i + 2, axles)]);
    return new WeighingResult
    {
        Timestamp = new DateTime(2026, 10, 30, 12, rng.Next(60), rng.Next(60)),
        Status = new SensorStatus(0),
        Speed = rng.Next(100, 900) / 10m,
        Acceleration = (byte)rng.Next(0, 10),
        AxleWeights = axleWeights,
        AxleGroupWeights = groups.Select(g => g.Sum()).ToArray(),
        AxleGroupTypes = groups.Select(g => (byte)g.Length).ToArray(),
        AxleDistances = Enumerable.Range(0, axles - 1).Select(_ => rng.Next(1200, 4500)).ToArray(),
    };
}
