using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace SMSCapture
{
    

    public sealed class CaptureWorker : BackgroundService
    {
        private readonly ILogger<CaptureWorker> _logger;
        private readonly CaptureOptions _options;

        private ICaptureDevice? _device;
        private Thread? _statsThread;
        private Thread? _workerThread;

        private readonly BlockingCollection<CapturedItem> _queue;
        private readonly ManualResetEventSlim _stopEvent = new(false);

        private long _enqueuedPackets = 0;
        private long _dequeuedPackets = 0;
        private long _queueFullDrops = 0;
        private long _workerErrors = 0;
        private long _callbackErrors = 0;

        private long _lastReceived = 0;
        private long _lastDropped = 0;
        private long _lastInterfaceDropped = 0;
        private long _lastEnqueued = 0;
        private long _lastDequeued = 0;
        private long _lastQueueFullDrops = 0;
        private long _lastWorkerErrors = 0;
        private long _lastCallbackErrors = 0;

        private DateTime _captureStartUtc;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;

        private static readonly TimeSpan StatsWarmup = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        private static readonly int[] CandidateCaptureBufferSizes =
        {
        64 * 1024 * 1024,
        32 * 1024 * 1024,
        16 * 1024 * 1024,
         8 * 1024 * 1024
    };

        private sealed class CapturedItem
        {
            public LinkLayers LinkLayerType;
            public byte[] Data = Array.Empty<byte>();
        }

        public CaptureWorker(
            ILogger<CaptureWorker> logger,
            IOptions<CaptureOptions> options)
        {
            _logger = logger;
            _options = options.Value;

            _queue = new BlockingCollection<CapturedItem>(
                new ConcurrentQueue<CapturedItem>(),
                _options.QueueCapacity);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            _logger.LogInformation("SMS capture worker starting");

            try
            {
                StartWorker();

                PacketDotNet.SMS.SmsPipeline.StartImsiFilterHotReload(_options.ImsiFilePath);

                if (_options.UseFileMode)
                    RunFileMode();
                else
                    RunLiveMode();

                while (!stoppingToken.IsCancellationRequested && !_stopEvent.IsSet)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal capture worker error");
            }
            finally
            {
                try
                {
                    PacketDotNet.SMS.SmsPipeline.StopImsiFilterHotReload();
                }
                catch
                {
                }

                Shutdown();
                _logger.LogInformation("Capture worker stopped");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stop requested");
            _stopEvent.Set();
            return base.StopAsync(cancellationToken);
        }

        private void RunFileMode()
        {
            var capFile = _options.CaptureFile;
            _logger.LogInformation("Using capture file: {CaptureFile}", capFile);

            var fileDevice = new CaptureFileReaderDevice(capFile);
            _device = fileDevice;
            _device.OnPacketArrival += Device_OnPacketArrival;
            _device.Open();

            _logger.LogInformation("Reading packets from capture file");

            _captureStartUtc = DateTime.UtcNow;
            _lastHeartbeatUtc = DateTime.UtcNow;

            StartStatsPrinter();

            _device.Capture();

            WaitForQueueToDrain();
            PrintFinalAppStats();
        }

        private void RunLiveMode()
        {
            var devices = CaptureDeviceList.Instance;

            if (devices.Count < 1)
                throw new InvalidOperationException("No device found on this machine");

            if (_options.DeviceIndex < 0 || _options.DeviceIndex >= devices.Count)
                throw new InvalidOperationException($"Invalid DeviceIndex={_options.DeviceIndex}");

            _device = devices[_options.DeviceIndex];
            _device.OnPacketArrival += Device_OnPacketArrival;

            TryApplyLiveCaptureTuning(_device);

            _device.Open(
                DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness,
                _options.LiveReadTimeoutMs);

            _device.Filter = _options.BpfFilter;

            _logger.LogInformation("The following tcpdump filter will be applied: {Filter}", _options.BpfFilter);
            _logger.LogInformation("Waiting 500 ms after open/filter before capture start");
            Thread.Sleep(500);

            _logger.LogInformation("Queue capacity: {QueueCapacity}", _options.QueueCapacity);
            _logger.LogInformation("Stats warm-up: {WarmupSeconds} seconds", StatsWarmup.TotalSeconds);
            _logger.LogInformation("Listening on {DeviceDescription}", _device.Description);

            ResetStatsBaseline();

            _captureStartUtc = DateTime.UtcNow;
            _lastHeartbeatUtc = DateTime.UtcNow;

            StartStatsPrinter();
            _device.StartCapture();
        }

        private void ResetStatsBaseline()
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

        private void TryApplyLiveCaptureTuning(ICaptureDevice device)
        {
            bool anySettingApplied = false;

            foreach (int size in CandidateCaptureBufferSizes)
            {
                if (TrySetIntProperty(device, "KernelBufferSize", size))
                {
                    _logger.LogInformation("LIVE-TUNE | KernelBufferSize={Size}", size);
                    anySettingApplied = true;
                    break;
                }

                if (TrySetIntProperty(device, "BufferSize", size))
                {
                    _logger.LogInformation("LIVE-TUNE | BufferSize={Size}", size);
                    anySettingApplied = true;
                    break;
                }
            }

            if (TrySetIntProperty(device, "MinToCopy", 0))
            {
                _logger.LogInformation("LIVE-TUNE | MinToCopy=0");
                anySettingApplied = true;
            }

            if (!anySettingApplied)
            {
                _logger.LogInformation("LIVE-TUNE | No pre-open buffer property was available on this SharpPcap device");
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

        private void StartWorker()
        {
            _workerThread = new Thread(() =>
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        Interlocked.Increment(ref _dequeuedPackets);

                        var packet = Packet.ParsePacket(item.LinkLayerType, item.Data);
                        if (packet != null)
                        {
                            var sw = Stopwatch.StartNew();
                            PacketDotNet.SMS.SmsPipeline.ProcessPacket(packet);
                            sw.Stop();

                            if (sw.ElapsedMilliseconds >= 200)
                            {
                                _logger.LogWarning(
                                    "SLOW-PACKET | Ms={ElapsedMs} | Queue={QueueCount}",
                                    sw.ElapsedMilliseconds,
                                    _queue.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _workerErrors);
                        _logger.LogError(ex, "Worker packet processing failed");
                    }
                }
            });

            _workerThread.IsBackground = true;
            _workerThread.Name = "PacketWorker";
            _workerThread.Start();
        }

        private void Device_OnPacketArrival(object? sender, PacketCapture e)
        {
            try
            {
                var raw = e.GetPacket();
                if (raw?.Data == null || raw.Data.Length == 0)
                    return;

                byte[] copy = new byte[raw.Data.Length];
                Buffer.BlockCopy(raw.Data, 0, copy, 0, raw.Data.Length);

                var item = new CapturedItem
                {
                    LinkLayerType = raw.LinkLayerType,
                    Data = copy
                };

                if (_queue.TryAdd(item))
                {
                    Interlocked.Increment(ref _enqueuedPackets);
                }
                else
                {
                    Interlocked.Increment(ref _queueFullDrops);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _callbackErrors);
                _logger.LogError(ex, "Packet callback failed");
            }
        }

        private void StartStatsPrinter()
        {
            _statsThread = new Thread(() =>
            {
                while (!_stopEvent.IsSet)
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

        private void PrintCurrentStats()
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
                    _logger.LogInformation(
                        "APP-STATS | Enq={Enq} | Deq={Deq} | QueueCount={QueueCount} | QueueOwnDrop={QueueOwnDrop} | WorkerErr={WorkerErr} | CallbackErr={CallbackErr}",
                        enq, deq, qcount, qdrop, werr, cerr);

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
                    _logger.LogInformation(
                        "APP-STATS | Enq={Enq} | Deq={Deq} | QueueCount={QueueCount} | QueueOwnDrop={QueueOwnDrop} | WorkerErr={WorkerErr} | CallbackErr={CallbackErr}",
                        enq, deq, qcount, qdrop, werr, cerr);

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

            bool interesting =
                dDrop > 0 ||
                dIfDrop > 0 ||
                dQdrop > 0 ||
                dWerr > 0 ||
                dCerr > 0;

            bool heartbeat = DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval;

            if (interesting || heartbeat)
            {
                var proc = Process.GetCurrentProcess();

                long wsMb = proc.WorkingSet64 / (1024 * 1024);
                long privateMb = proc.PrivateMemorySize64 / (1024 * 1024);
                long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);

                if (dDrop > 5 || _queue.Count > 500 || heartbeat)
                {
                    _logger.LogInformation(
                        "DELTA | Recv+={RecvDelta} | PcapDrop+={PcapDropDelta} | IfDrop+={IfDropDelta} | Enq+={EnqDelta} | Deq+={DeqDelta} | QueueCount={QueueCount} | QueueOwnDrop+={QueueOwnDropDelta} | WorkerErr+={WorkerErrDelta} | CallbackErr+={CallbackErrDelta} | WS_MB={WorkingSetMb} | Private_MB={PrivateMb} | Managed_MB={ManagedMb} | LossSource={LossSource}",
                        dRecv, dDrop, dIfDrop, dEnq, dDeq, _queue.Count, dQdrop, dWerr, dCerr, wsMb, privateMb, managedMb, lossSource);
                }

                _lastHeartbeatUtc = DateTime.UtcNow;
            }

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

        private ICaptureStatistics? SafeGetStatistics()
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

        private void WaitForQueueToDrain()
        {
            _logger.LogInformation("Waiting for worker queue to drain");

            while (_queue.Count > 0)
            {
                _logger.LogInformation("QUEUE-DRAIN | Remaining={Remaining}", _queue.Count);
                Thread.Sleep(500);
            }
        }

        private void Shutdown()
        {
            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            try
            {
                if (_device != null)
                {
                    _device.OnPacketArrival -= Device_OnPacketArrival;
                    _device.StopCapture();
                }
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

        private void PrintFinalStats()
        {
            long enq = Interlocked.Read(ref _enqueuedPackets);
            long deq = Interlocked.Read(ref _dequeuedPackets);
            long qdrop = Interlocked.Read(ref _queueFullDrops);
            long werr = Interlocked.Read(ref _workerErrors);
            long cerr = Interlocked.Read(ref _callbackErrors);

            var stats = SafeGetStatistics();
            if (stats != null)
            {
                _logger.LogInformation(
                    "FINAL-STATS | PCAP | Received={Received} | Dropped={Dropped} | InterfaceDropped={InterfaceDropped}",
                    stats.ReceivedPackets,
                    stats.DroppedPackets,
                    stats.InterfaceDroppedPackets);
            }

            _logger.LogInformation(
                "FINAL-STATS | APP | Enqueued={Enqueued} | Dequeued={Dequeued} | QueueCount={QueueCount} | QueueOwnDrop={QueueOwnDrop} | WorkerErr={WorkerErr} | CallbackErr={CallbackErr}",
                enq, deq, _queue.Count, qdrop, werr, cerr);
        }

        private void PrintFinalAppStats()
        {
            long enq = Interlocked.Read(ref _enqueuedPackets);
            long deq = Interlocked.Read(ref _dequeuedPackets);
            long qdrop = Interlocked.Read(ref _queueFullDrops);
            long werr = Interlocked.Read(ref _workerErrors);
            long cerr = Interlocked.Read(ref _callbackErrors);

            _logger.LogInformation(
                "FINAL-APP-STATS | Enqueued={Enqueued} | Dequeued={Dequeued} | QueueCount={QueueCount} | QueueOwnDrop={QueueOwnDrop} | WorkerErr={WorkerErr} | CallbackErr={CallbackErr}",
                enq, deq, _queue.Count, qdrop, werr, cerr);
        }
    }

    public sealed class CaptureOptions
    {
        public int DeviceIndex { get; set; } = 0;
        public bool UseFileMode { get; set; } = false;
        public string CaptureFile { get; set; } = @"d:\test8.pcapng";
        public string ImsiFilePath { get; set; } = @"C:\imsi.txt";
        public int QueueCapacity { get; set; } = 50000;
        public int LiveReadTimeoutMs { get; set; } = 1000;

        public string BpfFilter { get; set; } =
            "sctp and port 3907 and " +
            "((src net 10.95.137.0/24 and (dst net 10.95.41.0/24 or dst net 10.95.42.0/23)) or " +
            " (dst net 10.95.137.0/24 and (src net 10.95.41.0/24 or src net 10.95.42.0/23)))";
    }
}
