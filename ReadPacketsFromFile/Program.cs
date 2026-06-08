using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Threading;

class Program
{
    private static ICaptureDevice? _device;
    private static readonly ManualResetEventSlim _exitEvent = new(false);

    private static Thread? _statsThread;
    private static Thread? _workerThread;

    private static readonly BlockingCollection<CapturedItem> _queue =
        new(new ConcurrentQueue<CapturedItem>(), 50000);

    private static long _enqueuedPackets = 0;
    private static long _dequeuedPackets = 0;
    private static long _queueFullDrops = 0;
    private static long _workerErrors = 0;
    private static long _callbackErrors = 0;

    private static long _lastReceived = 0;
    private static long _lastDropped = 0;
    private static long _lastInterfaceDropped = 0;
    private static long _lastEnqueued = 0;
    private static long _lastDequeued = 0;
    private static long _lastQueueFullDrops = 0;
    private static long _lastWorkerErrors = 0;
    private static long _lastCallbackErrors = 0;

    private static DateTime _captureStartUtc;
    private static DateTime _lastHeartbeatUtc = DateTime.MinValue;

    private static readonly TimeSpan StatsWarmup = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(120);

    private const int LiveReadTimeoutMs = 1000;

    private static readonly int[] CandidateCaptureBufferSizes =
    {
        64 * 1024 * 1024,
        32 * 1024 * 1024,
        16 * 1024 * 1024,
         8 * 1024 * 1024
    };

    private class CapturedItem
    {
        public LinkLayers LinkLayerType;
        public byte[] Data = Array.Empty<byte>();
        public int Length;
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Console.WriteLine("SharpPcap SMS MT Capture");
        Console.WriteLine();

        var devices = CaptureDeviceList.Instance;

        if (devices.Count < 1)
        {
            Console.WriteLine("No device found on this machine");
            return;
        }

        Console.WriteLine("The following devices are available on this machine:");
        Console.WriteLine("----------------------------------------------------");
        Console.WriteLine();

        for (int i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"{i}) {devices[i].Name} {devices[i].Description}");
        }

        Console.WriteLine();
        Console.Write("-- Please choose a device to capture (or anything else for file mode): ");

        bool useFileMode = !int.TryParse(Console.ReadLine(), out int deviceIndex) ||
                           deviceIndex < 0 ||
                           deviceIndex >= devices.Count;

        try
        {
            StartWorker();

            string imsiPath = @"C:\imsi.txt";
            PacketDotNet.SMS.SmsPipeline.StartImsiFilterHotReload(imsiPath);

            if (useFileMode)
            {
                RunFileMode();
            }
            else
            {
                RunLiveMode(devices[deviceIndex]);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fatal error:");
            Console.WriteLine(ex);
        }
        finally
        {
            PacketDotNet.SMS.SmsPipeline.StopImsiFilterHotReload();
            Shutdown();
        }

        Console.WriteLine("Capture stopped.");
    }

    private static void RunFileMode()
    {
        var capFile = @"d:\test8.pcapng";
        Console.WriteLine($"Using capture file: {capFile}");

        var fileDevice = new CaptureFileReaderDevice(capFile);
        _device = fileDevice;
        _device.OnPacketArrival += Device_OnPacketArrival;
        _device.Open();

        Console.WriteLine();
        Console.WriteLine("-- Reading packets from capture file...");
        Console.WriteLine();

        _device.Capture();

        WaitForQueueToDrain();
        PrintFinalAppStats();
    }

    private static void RunLiveMode(ICaptureDevice selectedDevice)
    {
        _device = selectedDevice;
        _device.OnPacketArrival += Device_OnPacketArrival;

        TryApplyLiveCaptureTuning(_device);

        _device.Open(
            DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness,
            LiveReadTimeoutMs);

        string filter =
            "sctp and port 3907 and " +
            "((src net 10.95.137.0/24 and (dst net 10.95.41.0/24 or dst net 10.95.42.0/23)) or " +
            " (dst net 10.95.137.0/24 and (src net 10.95.41.0/24 or src net 10.95.42.0/23)))";

        _device.Filter = filter;

        Console.WriteLine();
        Console.WriteLine($"-- The following tcpdump filter will be applied: \"{filter}\"");
        Console.WriteLine("-- Waiting 500 ms after open/filter before capture start...");
        Thread.Sleep(500);

        Console.WriteLine($"-- Queue capacity: 50000");
        Console.WriteLine($"-- Stats warm-up: {StatsWarmup.TotalSeconds:0} seconds");
        Console.WriteLine($"-- Listening on {_device.Description}, hit Ctrl+C to exit.");
        Console.WriteLine();

        Console.CancelKeyPress += OnCancelKeyPress;

        ResetStatsBaseline();

        _captureStartUtc = DateTime.UtcNow;
        _lastHeartbeatUtc = DateTime.UtcNow;

        StartStatsPrinter();
        _device.StartCapture();

        _exitEvent.Wait();

        try
        {
            _device.StopCapture();
        }
        catch
        {
        }

        WaitForQueueToDrain();
        PrintFinalStats();
    }

    private static void ResetStatsBaseline()
    {
        _lastReceived = 0;
        _lastDropped = 0;
        _lastInterfaceDropped = 0;
        _lastEnqueued = 0;
        _lastDequeued = 0;
        _lastQueueFullDrops = 0;
        _lastWorkerErrors = 0;
        _lastCallbackErrors = 0;
    }

    private static void TryApplyLiveCaptureTuning(ICaptureDevice device)
    {
        bool anySettingApplied = false;

        foreach (int size in CandidateCaptureBufferSizes)
        {
            if (TrySetIntProperty(device, "KernelBufferSize", size))
            {
                Console.WriteLine($"LIVE-TUNE | KernelBufferSize={size}");
                anySettingApplied = true;
                break;
            }

            if (TrySetIntProperty(device, "BufferSize", size))
            {
                Console.WriteLine($"LIVE-TUNE | BufferSize={size}");
                anySettingApplied = true;
                break;
            }
        }

        if (TrySetIntProperty(device, "MinToCopy", 0))
        {
            Console.WriteLine("LIVE-TUNE | MinToCopy=0");
            anySettingApplied = true;
        }

        if (!anySettingApplied)
        {
            Console.WriteLine("LIVE-TUNE | No pre-open buffer property was available on this SharpPcap device");
        }
    }

    private static bool TrySetIntProperty(object obj, string propertyName, int value)
    {
        try
        {
            var prop = obj.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);

            if (prop == null || !prop.CanWrite)
                return false;

            if (prop.PropertyType != typeof(int))
                return false;

            prop.SetValue(obj, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartWorker()
    {
        _workerThread = new Thread(() =>
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Interlocked.Increment(ref _dequeuedPackets);

                    var exact = new byte[item.Length];
                    Buffer.BlockCopy(item.Data, 0, exact, 0, item.Length);

                    var packet = Packet.ParsePacket(item.LinkLayerType, exact);
                    if (packet != null)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        PacketDotNet.SMS.SmsPipeline.ProcessPacket(packet);
                        sw.Stop();

                        if (sw.ElapsedMilliseconds >= 200)
                        {
                            Console.WriteLine(
                                $"SLOW-PACKET | Ms={sw.ElapsedMilliseconds} | Queue={_queue.Count}");
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _workerErrors);
                }
                finally
                {
                    try
                    {
                        if (item.Data != null && item.Data.Length > 0)
                            ArrayPool<byte>.Shared.Return(item.Data);
                    }
                    catch
                    {
                    }
                }
            }
        });

        _workerThread.IsBackground = true;
        _workerThread.Name = "PacketWorker";
        _workerThread.Start();
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _exitEvent.Set();
    }

    private static void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            if (raw?.Data == null || raw.Data.Length == 0)
                return;

            byte[] rented = ArrayPool<byte>.Shared.Rent(raw.Data.Length);
            Buffer.BlockCopy(raw.Data, 0, rented, 0, raw.Data.Length);

            var item = new CapturedItem
            {
                LinkLayerType = raw.LinkLayerType,
                Data = rented,
                Length = raw.Data.Length
            };

            if (_queue.TryAdd(item))
            {
                Interlocked.Increment(ref _enqueuedPackets);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(rented);
                Interlocked.Increment(ref _queueFullDrops);
            }
        }
        catch
        {
            Interlocked.Increment(ref _callbackErrors);
        }
    }

    private static void StartStatsPrinter()
    {
        _statsThread = new Thread(() =>
        {
            while (!_exitEvent.IsSet)
            {
                Thread.Sleep(StatsInterval);

                try
                {
                    if (_captureStartUtc == default)
                        continue;

                    var uptime = DateTime.UtcNow - _captureStartUtc;
                    if (uptime < StatsWarmup)
                        continue;

                    PrintCurrentStats();
                }
                catch
                {
                }
            }
        });

        _statsThread.IsBackground = true;
        _statsThread.Name = "StatsPrinter";
        _statsThread.Start();
    }

    private static void PrintCurrentStats()
    {
        long enq = Interlocked.Read(ref _enqueuedPackets);
        long deq = Interlocked.Read(ref _dequeuedPackets);
        long qdrop = Interlocked.Read(ref _queueFullDrops);
        long werr = Interlocked.Read(ref _workerErrors);
        long cerr = Interlocked.Read(ref _callbackErrors);
        int qcount = _queue.Count;

        if (_device == null)
        {
            bool appInteresting =
                qdrop > _lastQueueFullDrops ||
                werr > _lastWorkerErrors ||
                cerr > _lastCallbackErrors;

            bool heartbeatDue = DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval;

            if (appInteresting || heartbeatDue)
            {
                Console.WriteLine(
                    $"APP-STATS | " +
                    $"Enq={enq} | Deq={deq} | QueueCount={qcount} | " +
                    $"QueueOwnDrop={qdrop} | WorkerErr={werr} | CallbackErr={cerr}");

                _lastHeartbeatUtc = DateTime.UtcNow;
            }

            _lastEnqueued = enq;
            _lastDequeued = deq;
            _lastQueueFullDrops = qdrop;
            _lastWorkerErrors = werr;
            _lastCallbackErrors = cerr;
            return;
        }

        var stats = SafeGetStatistics();
        if (stats == null)
        {
            bool appInteresting =
                qdrop > _lastQueueFullDrops ||
                werr > _lastWorkerErrors ||
                cerr > _lastCallbackErrors;

            bool heartbeatDue = DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval;

            if (appInteresting || heartbeatDue)
            {
                Console.WriteLine(
                    $"APP-STATS | " +
                    $"Enq={enq} | Deq={deq} | QueueCount={qcount} | " +
                    $"QueueOwnDrop={qdrop} | WorkerErr={werr} | CallbackErr={cerr}");

                _lastHeartbeatUtc = DateTime.UtcNow;
            }

            _lastEnqueued = enq;
            _lastDequeued = deq;
            _lastQueueFullDrops = qdrop;
            _lastWorkerErrors = werr;
            _lastCallbackErrors = cerr;
            return;
        }

        long recv = stats.ReceivedPackets;
        long drop = stats.DroppedPackets;
        long ifDrop = stats.InterfaceDroppedPackets;

        if (_lastReceived == 0 &&
            _lastDropped == 0 &&
            _lastInterfaceDropped == 0 &&
            _lastEnqueued == 0 &&
            _lastDequeued == 0 &&
            _lastQueueFullDrops == 0 &&
            _lastWorkerErrors == 0 &&
            _lastCallbackErrors == 0)
        {
            _lastReceived = recv;
            _lastDropped = drop;
            _lastInterfaceDropped = ifDrop;
            _lastEnqueued = enq;
            _lastDequeued = deq;
            _lastQueueFullDrops = qdrop;
            _lastWorkerErrors = werr;
            _lastCallbackErrors = cerr;
            return;
        }

        long dRecv = recv - _lastReceived;
        long dDrop = drop - _lastDropped;
        long dIfDrop = ifDrop - _lastInterfaceDropped;
        long dEnq = enq - _lastEnqueued;
        long dDeq = deq - _lastDequeued;
        long dQdrop = qdrop - _lastQueueFullDrops;
        long dWerr = werr - _lastWorkerErrors;
        long dCerr = cerr - _lastCallbackErrors;

        string lossSource = BuildLossSource(dDrop, dIfDrop, dQdrop, dWerr, dCerr);

        bool hasLossOrError =
            dDrop > 0 ||
            dIfDrop > 0 ||
            dQdrop > 0 ||
            dWerr > 0 ||
            dCerr > 0;

        bool heartbeat = DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval;

        if (!hasLossOrError && !heartbeat)
        {
            _lastReceived = recv;
            _lastDropped = drop;
            _lastInterfaceDropped = ifDrop;
            _lastEnqueued = enq;
            _lastDequeued = deq;
            _lastQueueFullDrops = qdrop;
            _lastWorkerErrors = werr;
            _lastCallbackErrors = cerr;
            return;
        }

        var proc = System.Diagnostics.Process.GetCurrentProcess();

        long wsMb = proc.WorkingSet64 / (1024 * 1024);
        long privateMb = proc.PrivateMemorySize64 / (1024 * 1024);
        long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);

        Console.WriteLine(
            $"{DateTime.Now}  DELTA | " +
            $"Recv+={dRecv} | " +
            $"PcapDrop+={dDrop} | " +
            $"IfDrop+={dIfDrop} | " +
            $"Enq+={dEnq} | " +
            $"Deq+={dDeq} | " +
            $"QueueCount={_queue.Count} | " +
            $"QueueOwnDrop+={dQdrop} | " +
            $"WorkerErr+={dWerr} | " +
            $"CallbackErr+={dCerr} | " +
            $"WS_MB={wsMb} | " +
            $"Private_MB={privateMb} | " +
            $"Managed_MB={managedMb} | " +
            $"LossSource={lossSource}");

        _lastHeartbeatUtc = DateTime.UtcNow;

        try
        {
            PacketDotNet.SMS.TcapCorrelator.CleanupExpired();
            PacketDotNet.SMS.AckCorrelator.CleanupExpired();
        }
        catch
        {
        }

        _lastReceived = recv;
        _lastDropped = drop;
        _lastInterfaceDropped = ifDrop;
        _lastEnqueued = enq;
        _lastDequeued = deq;
        _lastQueueFullDrops = qdrop;
        _lastWorkerErrors = werr;
        _lastCallbackErrors = cerr;
    }

    private static string BuildLossSource(long dDrop, long dIfDrop, long dQdrop, long dWerr, long dCerr)
    {
        bool pcapLoss = dDrop > 0 || dIfDrop > 0;
        bool appLoss = dQdrop > 0;
        bool appErrors = dWerr > 0 || dCerr > 0;

        if (pcapLoss && !appLoss && !appErrors)
            return "PCAP/KERNEL";

        if (!pcapLoss && appLoss && !appErrors)
            return "APP-QUEUE";

        if (!pcapLoss && !appLoss && appErrors)
            return "APP-ERROR";

        if (pcapLoss && appLoss)
            return "PCAP+APP";

        if (pcapLoss && appErrors)
            return "PCAP+APP-ERROR";

        if (appLoss && appErrors)
            return "APP-QUEUE+ERROR";

        if (pcapLoss || appLoss || appErrors)
            return "MIXED";

        return "NONE";
    }

    private static ICaptureStatistics? SafeGetStatistics()
    {
        try
        {
            return _device?.Statistics;
        }
        catch
        {
            return null;
        }
    }

    private static void WaitForQueueToDrain()
    {
        Console.WriteLine("Waiting for worker queue to drain...");

        while (_queue.Count > 0)
        {
            Console.WriteLine($"QUEUE-DRAIN | Remaining={_queue.Count}");
            Thread.Sleep(500);
        }
    }

    private static void Shutdown()
    {
        try
        {
            _exitEvent.Set();
        }
        catch
        {
        }

        try
        {
            _device?.Close();
        }
        catch
        {
        }

        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
        }

        try
        {
            if (_statsThread != null && _statsThread.IsAlive)
                _statsThread.Join(2000);
        }
        catch
        {
        }

        try
        {
            if (_workerThread != null && _workerThread.IsAlive)
                _workerThread.Join(5000);
        }
        catch
        {
        }
    }

    private static void PrintFinalStats()
    {
        long enq = Interlocked.Read(ref _enqueuedPackets);
        long deq = Interlocked.Read(ref _dequeuedPackets);
        long qdrop = Interlocked.Read(ref _queueFullDrops);
        long werr = Interlocked.Read(ref _workerErrors);
        long cerr = Interlocked.Read(ref _callbackErrors);

        Console.WriteLine();
        Console.WriteLine("FINAL-STATS");

        var stats = SafeGetStatistics();
        if (stats != null)
        {
            Console.WriteLine(
                $"PCAP | Received={stats.ReceivedPackets} | Dropped={stats.DroppedPackets} | InterfaceDropped={stats.InterfaceDroppedPackets}");
        }

        Console.WriteLine(
            $"APP  | Enqueued={enq} | Dequeued={deq} | QueueCount={_queue.Count} | QueueOwnDrop={qdrop} | WorkerErr={werr} | CallbackErr={cerr}");
        Console.WriteLine();
    }

    private static void PrintFinalAppStats()
    {
        long enq = Interlocked.Read(ref _enqueuedPackets);
        long deq = Interlocked.Read(ref _dequeuedPackets);
        long qdrop = Interlocked.Read(ref _queueFullDrops);
        long werr = Interlocked.Read(ref _workerErrors);
        long cerr = Interlocked.Read(ref _callbackErrors);

        Console.WriteLine();
        Console.WriteLine("FINAL-APP-STATS");
        Console.WriteLine(
            $"APP | Enqueued={enq} | Dequeued={deq} | QueueCount={_queue.Count} | QueueOwnDrop={qdrop} | WorkerErr={werr} | CallbackErr={cerr}");
        Console.WriteLine();
    }
}