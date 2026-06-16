using PacketDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PacketDotNet.SMS
{
    public static class SmsPipeline
    {
        // Keep the IMSI allow-list behavior
        private static HashSet<string> _filterImsis = new(StringComparer.Ordinal);

        // New: IMSI -> MSISDN mapping
        private static Dictionary<string, string> _imsiToMsisdn = new(StringComparer.Ordinal);

        private static readonly object _filterReloadLock = new();

        private static string _filterFilePath;
        private static FileSystemWatcher _filterWatcher;
        private static Timer _reloadDebounceTimer;

        public static void StartImsiFilterHotReload(string path)
        {
            try
            {
                _filterFilePath = Path.GetFullPath(path);

                LoadImsiFilterNow();

                string directory = Path.GetDirectoryName(_filterFilePath);
                string fileName = Path.GetFileName(_filterFilePath);

                if (string.IsNullOrWhiteSpace(directory))
                    directory = AppContext.BaseDirectory;

                _filterWatcher?.Dispose();

                _filterWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.CreationTime |
                        NotifyFilters.Size
                };

                _reloadDebounceTimer ??= new Timer(_ => SafeReloadFromTimer(), null, Timeout.Infinite, Timeout.Infinite);

                _filterWatcher.Changed += OnFilterFileChanged;
                _filterWatcher.Created += OnFilterFileChanged;
                _filterWatcher.Renamed += OnFilterFileChanged;
                _filterWatcher.EnableRaisingEvents = true;

                SmsLog.Info($"IMSI hot reload watching: {_filterFilePath}");
            }
            catch (Exception ex)
            {
                SmsLog.Error($"Failed to start IMSI hot reload: {ex.Message}");
            }
        }

        public static void LoadSubscribers(string path)
        {
            try
            {
                _filterFilePath = Path.GetFullPath(path);

                LoadImsiFilterNow();

                string directory = Path.GetDirectoryName(_filterFilePath);
                string fileName = Path.GetFileName(_filterFilePath);

                if (string.IsNullOrWhiteSpace(directory))
                    directory = AppContext.BaseDirectory;

                _filterWatcher?.Dispose();

                _filterWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.CreationTime |
                        NotifyFilters.Size
                };

                _reloadDebounceTimer ??= new Timer(_ => SafeReloadFromTimer(), null, Timeout.Infinite, Timeout.Infinite);

                _filterWatcher.Changed += OnFilterFileChanged;
                _filterWatcher.Created += OnFilterFileChanged;
                _filterWatcher.Renamed += OnFilterFileChanged;
                _filterWatcher.EnableRaisingEvents = true;

                SmsLog.Info($"IMSI hot reload watching: {_filterFilePath}");
            }
            catch (Exception ex)
            {
                SmsLog.Error($"Failed to start IMSI hot reload: {ex.Message}");
            }
        }

        public static void StopImsiFilterHotReload()
        {
            try
            {
                _filterWatcher?.Dispose();
                _filterWatcher = null;

                _reloadDebounceTimer?.Dispose();
                _reloadDebounceTimer = null;
            }
            catch
            {
            }
        }

        private static void OnFilterFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                _reloadDebounceTimer?.Change(300, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private static void SafeReloadFromTimer()
        {
            try
            {
                LoadImsiFilterNow();
            }
            catch
            {
            }
        }

        private static void LoadImsiFilterNow()
        {
            lock (_filterReloadLock)
            {
                var parsed = ReadImsiFilterFileWithRetry(_filterFilePath);
                if (parsed == null)
                    return;

                Interlocked.Exchange(ref _filterImsis, parsed.AllowSet);
                Interlocked.Exchange(ref _imsiToMsisdn, parsed.Map);

                SmsLog.Info($"IMSI filter reloaded | Count={parsed.AllowSet.Count} | MappedMsisdn={parsed.Map.Count}");
            }
        }

        private sealed class ImsiFilterData
        {
            public HashSet<string> AllowSet { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Map { get; set; } = new(StringComparer.Ordinal);
        }

        private static ImsiFilterData ReadImsiFilterFileWithRetry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new ImsiFilterData();

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        SmsLog.Warn($"IMSI file not found: {path}");
                        return new ImsiFilterData();
                    }

                    var lines = File.ReadAllLines(path);

                    var allowSet = new HashSet<string>(StringComparer.Ordinal);
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach (var raw in lines)
                    {
                        var line = raw?.Trim();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        if (line.StartsWith("#"))
                            continue;

                        // Supported formats:
                        // IMSI
                        // IMSI=MSISDN
                        // IMSI,MSISDN
                        string imsi;
                        string msisdn = null;

                        int eq = line.IndexOf('=');
                        int comma = line.IndexOf(',');

                        if (eq > 0)
                        {
                            imsi = line.Substring(0, eq).Trim();
                            msisdn = line.Substring(eq + 1).Trim();
                        }
                        else if (comma > 0)
                        {
                            imsi = line.Substring(0, comma).Trim();
                            msisdn = line.Substring(comma + 1).Trim();
                        }
                        else
                        {
                            imsi = line.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(imsi))
                            continue;

                        allowSet.Add(imsi);

                        if (!string.IsNullOrWhiteSpace(msisdn))
                            map[imsi] = msisdn;
                    }

                    return new ImsiFilterData
                    {
                        AllowSet = allowSet,
                        Map = map
                    };
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    SmsLog.Error($"Failed to read IMSI file: {ex.Message}");
                    return null;
                }
            }

            SmsLog.Warn("Failed to reload IMSI file after retries");
            return null;
        }

        public static void ProcessPacket(Packet packet)
        {
            var ip = packet.Extract<IPPacket>();
            if (ip == null)
                return;

            if ((int)ip.Protocol != 132) // SCTP
                return;

            var chunks = SctpParser.Parse(ip.PayloadData);

            foreach (var chunk in chunks)
            {
                if (chunk.Type != 0)
                    continue;

                try
                {
                    if (chunk.Data == null || chunk.Data.Length <= 12)
                        continue;

                    byte[] m3uaBytes = new byte[chunk.Data.Length - 12];
                    Buffer.BlockCopy(chunk.Data, 12, m3uaBytes, 0, m3uaBytes.Length);

                    var m3ua = M3uaMessage.Parse(m3uaBytes);
                    if (m3ua == null || m3ua.UserProtocolData == null || m3ua.UserProtocolData.Length == 0)
                        continue;

                    var tcap = SccpParser.Extract(m3ua.UserProtocolData);
                    if (tcap == null || tcap.Length == 0)
                        continue;

                    if (!TcapDetector.IsTcap(tcap))
                        continue;

                    var tcapInfo = TcapParser.Parse(tcap);
                    if (tcapInfo == null)
                        continue;

                    var map = MapDecoder.Decode(tcap);
                    if (map == null)
                        continue;

                    if (!string.IsNullOrWhiteSpace(tcapInfo.Otid) &&
                        !string.IsNullOrWhiteSpace(tcapInfo.Dtid))
                    {
                        TcapCorrelator.BindDtid(tcapInfo.Otid, tcapInfo.Dtid);
                    }

                    // ACK / RESULT / ERROR path
                    if (tcapInfo.HasReturnResult || tcapInfo.HasReturnError)
                    {
                        string ackMatchSource;
                        var ackInfo = TcapCorrelator.FindWithSource(tcapInfo.Otid, tcapInfo.Dtid, out ackMatchSource);

                        if (ackInfo != null &&
                            !string.IsNullOrWhiteSpace(ackInfo.Text) &&
                            ShouldPrintForImsi(ackInfo.DestinationImsi) &&
                            ShouldPrintAckMessage(ackInfo))
                        {
                            string result;
                            if (tcapInfo.HasReturnError)
                                result = tcapInfo.ReturnErrorCode >= 0 ? "ERROR" : "OK";
                            else
                                result = "OK";

                            SmsLog.Info(
                                $"MAP-ACK-MT | " +
                                $"Result={result} | " +
                                $"ErrorCode={SafeErrorCode(tcapInfo.ReturnErrorCode)} | " +
                                $"InvokeId={SafeInt(tcapInfo.InvokeId)} | " +
                                $"OTID={Safe(tcapInfo.Otid)} | " +
                                $"DTID={Safe(tcapInfo.Dtid)} | " +
                                $"Op={(ackInfo.OperationCode >= 0 ? ackInfo.OperationCode.ToString("X2") : "-")} | " +
                                $"From={Safe(ackInfo.From)} | " +
                                $"To={Safe(ackInfo.To)} | " +
                                $"IMSI={Safe(ackInfo.DestinationImsi)} | " +
                                $"Part=- | " +
                                $"Time={Safe(ackInfo.Timestamp)} | " +
                                $"Text={Safe(ackInfo.Text)}");

                            PublishMapAckSoap(tcapInfo, ackInfo);

                            SmsLog.Warn(
                                $"ACK-DEBUG | " +
                                $"MatchSource={ackMatchSource} | " +
                                $"ACK-OTID={Safe(tcapInfo.Otid)} | " +
                                $"ACK-DTID={Safe(tcapInfo.Dtid)} | " +
                                $"Stored-OTID={Safe(ackInfo.Otid)} | " +
                                $"Stored-DTID={Safe(ackInfo.Dtid)} | " +
                                $"Stored-Time={Safe(ackInfo.Timestamp)} | " +
                                $"IMSI={Safe(ackInfo.DestinationImsi)} | " +
                                $"From={Safe(ackInfo.From)} | " +
                                $"TextLen={GetTextLength(ackInfo.Text)}");

                            if (!string.IsNullOrWhiteSpace(ackInfo.Timestamp))
                            {
                                SmsLog.Warn(
                                    $"ACK-STORED | " +
                                    $"MatchSource={ackMatchSource} | " +
                                    $"Stored-Time={Safe(ackInfo.Timestamp)} | " +
                                    $"Stored-OTID={Safe(ackInfo.Otid)} | " +
                                    $"Stored-DTID={Safe(ackInfo.Dtid)} | " +
                                    $"TotalParts={ackInfo.TotalParts} | " +
                                    $"PartNumber={ackInfo.PartNumber} | " +
                                    $"IMSI={Safe(ackInfo.DestinationImsi)} | " +
                                    $"TextPreview={Preview(ackInfo.Text, 50)}");
                            }
                        }

                        if (ackInfo != null)
                        {
                            bool isMultipart = ackInfo.TotalParts > 1;

                            bool isFullMultipart =
                                ackInfo.TotalParts > 1 &&
                                ackInfo.PartNumber == ackInfo.TotalParts &&
                                !string.IsNullOrWhiteSpace(ackInfo.Text);

                            if (!isMultipart || isFullMultipart)
                                TcapCorrelator.Remove(ackInfo);
                        }

                        continue;
                    }

                    SmsMessage sms = null;

                    if (map.SmRpUi != null && map.SmRpUi.Length > 0)
                        sms = SmsDecoder.Decode(map.SmRpUi);

                    if (sms == null || !HasRealSmsContent(sms))
                        continue;

                    if (IsBinaryMessage(sms))
                        continue;

                    if (!IsWantedMt(sms))
                        continue;

                    TcapCorrelator.AddSms(tcapInfo.Otid, tcapInfo.Dtid, sms, map, tcapInfo.OperationCode);

                    string resolvedImsi = ResolveImsiForSms(tcapInfo, map);
                    if (!ShouldPrintForImsi(resolvedImsi))
                        continue;

                    // Single-part MT: print immediately
                    if (sms.TotalParts <= 1)
                    {
                        SmsLog.Info(
                            $"MAP-MT | " +
                            $"OTID={Safe(tcapInfo.Otid)} | " +
                            $"DTID={Safe(tcapInfo.Dtid)} | " +
                            $"Op={(tcapInfo.OperationCode >= 0 ? tcapInfo.OperationCode.ToString("X2") : "-")} | " +
                            $"From={Safe(sms.Sender)} | " +
                            $"To={Safe(sms.Receiver)} | " +
                            $"IMSI={Safe(resolvedImsi)} | " +
                            $"Part=- | " +
                            $"Time={Safe(sms.Timestamp)} | " +
                            $"Text={Safe(sms.Text)}");

                       // PublishMapMtSoap(tcapInfo, sms, resolvedImsi);
                        continue;
                    }

                    if (sms.TotalParts >= 5 && GetTextLength(sms.Text) <= 40)
                    {
                        SmsLog.Warn(
                            $"MP-SUSPECT-PART | " +
                            $"OTID={Safe(tcapInfo.Otid)} | " +
                            $"DTID={Safe(tcapInfo.Dtid)} | " +
                            $"From={Safe(sms.Sender)} | " +
                            $"To={Safe(sms.Receiver)} | " +
                            $"IMSI={Safe(resolvedImsi)} | " +
                            $"Ref={sms.ReferenceNumber} | " +
                            $"Part={sms.PartNumber}/{sms.TotalParts} | " +
                            $"HasUdh={(sms.HasUdh ? 1 : 0)} | " +
                            $"Dcs=0x{sms.Dcs:X2} | " +
                            $"TextLen={GetTextLength(sms.Text)} | " +
                            $"TextPreview={Preview(sms.Text, 50)}");
                    }

                    string fullText = SmsReassembler.AddPart(sms, resolvedImsi);
                    bool becameFullNow = !string.IsNullOrWhiteSpace(fullText);

                    if (becameFullNow)
                    {
                        sms.Text = fullText;
                        sms.PartNumber = sms.TotalParts;

                        TcapCorrelator.AddSms(tcapInfo.Otid, tcapInfo.Dtid, sms, map, tcapInfo.OperationCode);

                        resolvedImsi = ResolveImsiForSms(tcapInfo, map);

                        if (sms.TotalParts >= 5 && GetTextLength(sms.Text) <= 40)
                        {
                            SmsLog.Warn(
                                $"MP-SUSPECT-FULL | " +
                                $"OTID={Safe(tcapInfo.Otid)} | " +
                                $"DTID={Safe(tcapInfo.Dtid)} | " +
                                $"Op={(tcapInfo.OperationCode >= 0 ? tcapInfo.OperationCode.ToString("X2") : "-")} | " +
                                $"From={Safe(sms.Sender)} | " +
                                $"To={Safe(sms.Receiver)} | " +
                                $"IMSI={Safe(resolvedImsi)} | " +
                                $"Ref={sms.ReferenceNumber} | " +
                                $"Part=FULL {sms.TotalParts}/{sms.TotalParts} | " +
                                $"HasUdh={(sms.HasUdh ? 1 : 0)} | " +
                                $"Dcs=0x{sms.Dcs:X2} | " +
                                $"TextLen={GetTextLength(sms.Text)} | " +
                                $"TextPreview={Preview(sms.Text, 80)}");
                        }

                        if (ShouldPrintForImsi(resolvedImsi))
                        {
                            SmsLog.Info(
                                $"MAP-MT | " +
                                $"OTID={Safe(tcapInfo.Otid)} | " +
                                $"DTID={Safe(tcapInfo.Dtid)} | " +
                                $"Op={(tcapInfo.OperationCode >= 0 ? tcapInfo.OperationCode.ToString("X2") : "-")} | " +
                                $"From={Safe(sms.Sender)} | " +
                                $"To={Safe(sms.Receiver)} | " +
                                $"IMSI={Safe(resolvedImsi)} | " +
                                $"Part=FULL {sms.TotalParts}/{sms.TotalParts} | " +
                                $"Time={Safe(sms.Timestamp)} | " +
                                $"Text={Safe(sms.Text)}");

                            PublishMapMtSoap(tcapInfo, sms, resolvedImsi);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void PublishMapMtSoap(TcapInfo tcapInfo, SmsMessage sms, string resolvedImsi)
        {
            try
            {
                string destMsisdn = ResolveMsisdnForImsi(resolvedImsi);

                var evt = new SmsSoapEvent
                {
                    EventType = "MAP-MT",
                    Orig = Safe(sms?.Sender),
                    Dest = Safe(destMsisdn),
                    OrigSMSCGT = "1",
                    TimeStamp = Safe(sms?.Timestamp),
                    //Dcs = sms != null ? sms.Dcs.ToString("X2") : "",
                    //Udh = sms != null && sms.HasUdh ? "1" : "",
                    Dcs = "UCS2",
                    Udh = "1;1;1",
                    MessageContent = Safe(sms?.Text),

                    Otid = Safe(tcapInfo?.Otid),
                    Dtid = Safe(tcapInfo?.Dtid),
                    Op = tcapInfo != null && tcapInfo.OperationCode >= 0 ? tcapInfo.OperationCode.ToString("X2") : "-",
                    Imsi = Safe(resolvedImsi)
                };

                SmsHttpBridge.Publish(evt);
            }
            catch
            {
            }
        }

        private static void PublishMapAckSoap(TcapInfo tcapInfo, TcapSmsInfo ackInfo)
        {
            try
            {
                string destMsisdn = ResolveMsisdnForImsi(ackInfo?.DestinationImsi);

                var evt = new SmsSoapEvent
                {
                    EventType = "MAP-ACK-MT",
                    Orig = Safe(ackInfo?.From),
                    Dest = Safe(destMsisdn),
                    OrigSMSCGT = "1",
                    TimeStamp = Safe(ackInfo?.Timestamp),
                    Dcs = "UCS2",
                    Udh = "1;1;1",
                    MessageContent = Safe(ackInfo?.Text),

                    Otid = Safe(tcapInfo?.Otid),
                    Dtid = Safe(tcapInfo?.Dtid),
                    Op = ackInfo != null && ackInfo.OperationCode >= 0 ? ackInfo.OperationCode.ToString("X2") : "-",
                    Imsi = Safe(ackInfo?.DestinationImsi)
                };

                SmsHttpBridge.Publish(evt);
            }
            catch
            {
            }
        }

        private static string ResolveMsisdnForImsi(string imsi)
        {
            if (string.IsNullOrWhiteSpace(imsi))
                return null;

            var currentMap = _imsiToMsisdn;
            if (currentMap == null || currentMap.Count == 0)
                return null;

            if (currentMap.TryGetValue(imsi.Trim(), out var msisdn) &&
                !string.IsNullOrWhiteSpace(msisdn))
            {
                return msisdn.Trim();
            }

            return null;
        }

        private static bool ShouldPrintForImsi(string imsi)
        {
           
            if (string.IsNullOrWhiteSpace(imsi))
                return false;

            //425010791885475
           

            var current = _filterImsis;

            if (current == null || current.Count == 0)
                return false;

            return current.Contains(imsi.Trim());
        }

        private static bool ShouldPrintAckMessage(TcapSmsInfo ackInfo)
        {
            if (ackInfo == null)
                return false;

            return ackInfo.TotalParts <= 1;
        }

        private static bool IsWantedMt(SmsMessage sms)
        {
            return DetectDirection(sms) == "MT";
        }

        private static string DetectDirection(SmsMessage sms)
        {
            if (sms == null)
                return "-";

            bool hasFrom = !string.IsNullOrWhiteSpace(sms.Sender);
            bool hasText = !string.IsNullOrWhiteSpace(sms.Text);
            bool hasTimestamp = !string.IsNullOrWhiteSpace(sms.Timestamp);

            if (hasFrom && (hasTimestamp || hasText))
                return "MT";

            return "-";
        }

        private static string ResolveImsiForSms(TcapInfo tcapInfo, MapMessage map)
        {
            string imsi = map?.DestinationImsi;

            if (!string.IsNullOrWhiteSpace(imsi))
                return imsi;

            var correlated = TcapCorrelator.Find(tcapInfo?.Otid, tcapInfo?.Dtid);
            if (!string.IsNullOrWhiteSpace(correlated?.DestinationImsi))
                return correlated.DestinationImsi;

            return null;
        }

        private static bool HasRealSmsContent(SmsMessage sms)
        {
            return sms != null &&
                   (!string.IsNullOrWhiteSpace(sms.Text) ||
                    !string.IsNullOrWhiteSpace(sms.Sender));
        }

        private static string Safe(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "-" : s;
        }

        private static string SafeInt(int value)
        {
            return value >= 0 ? value.ToString() : "-";
        }

        private static string SafeErrorCode(int value)
        {
            return value >= 0 ? value.ToString() : "NONE";
        }

        private static int GetTextLength(string text)
        {
            return string.IsNullOrEmpty(text) ? 0 : text.Length;
        }

        private static string Preview(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text))
                return "-";

            text = text.Replace("\r", " ").Replace("\n", " ");

            if (text.Length <= maxLen)
                return text;

            return text.Substring(0, maxLen) + "...";
        }

        private static bool IsBinaryMessage(SmsMessage sms)
        {
            if (sms == null)
                return false;

            if (sms.DestPort >= 0 || sms.SrcPort >= 0)
                return true;

            if (sms.IsBinary)
                return true;

            return false;
        }

        // Add this public helper to SmsPipeline (place near other public helpers, e.g. after StopImsiFilterHotReload)
        public static void LoadImsiFilterFromDictionary(IDictionary<string, string> rows)
        {
            try
            {
                var allowSet = new HashSet<string>(StringComparer.Ordinal);
                var map = new Dictionary<string, string>(StringComparer.Ordinal);

                if (rows != null)
                {
                    foreach (var kv in rows)
                    {
                        var imsi = kv.Key?.Trim();
                        if (string.IsNullOrWhiteSpace(imsi))
                            continue;

                        allowSet.Add(imsi);

                        var msisdn = kv.Value?.Trim();
                        if (!string.IsNullOrWhiteSpace(msisdn))
                            map[imsi] = msisdn;
                    }
                }

                lock (_filterReloadLock)
                {
                    Interlocked.Exchange(ref _filterImsis, allowSet);
                    Interlocked.Exchange(ref _imsiToMsisdn, map);
                }

                SmsLog.Info($"IMSI filter loaded from dictionary | Count={allowSet.Count} | MappedMsisdn={map.Count}");
            }
            catch (Exception ex)
            {
                SmsLog.Error($"Failed to load IMSI filter from dictionary: {ex.Message}");
            }
        }
    }
}