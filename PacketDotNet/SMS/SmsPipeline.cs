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
        private static HashSet<string> _filterImsis = new(StringComparer.Ordinal);
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

                Console.WriteLine($"IMSI hot reload watching: {_filterFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start IMSI hot reload: {ex.Message}");
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
                var newSet = ReadImsiFilterFileWithRetry(_filterFilePath);
                if (newSet == null)
                    return;

                Interlocked.Exchange(ref _filterImsis, newSet);

                Console.WriteLine($"IMSI filter reloaded | Count={newSet.Count}");
            }
        }

        private static HashSet<string> ReadImsiFilterFileWithRetry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new HashSet<string>(StringComparer.Ordinal);

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"IMSI file not found: {path}");
                        return new HashSet<string>(StringComparer.Ordinal);
                    }

                    var lines = File.ReadAllLines(path);

                    return new HashSet<string>(
                        lines
                            .Select(x => x.Trim())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Where(x => !x.StartsWith("#")),
                        StringComparer.Ordinal);
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
                    Console.WriteLine($"Failed to read IMSI file: {ex.Message}");
                    return null;
                }
            }

            Console.WriteLine("Failed to reload IMSI file after retries");
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
                        var ackInfo = TcapCorrelator.Find(tcapInfo.Otid, tcapInfo.Dtid);

                        bool printedAck = false;

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

                            Console.WriteLine(
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

                            printedAck = true;
                        }

                        //if (ackInfo != null)
                        //    TcapCorrelator.Remove(ackInfo);

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
                        //Console.WriteLine(
                        //    $"MAP-MT | " +
                        //    $"OTID={Safe(tcapInfo.Otid)} | " +
                        //    $"DTID={Safe(tcapInfo.Dtid)} | " +
                        //    $"Op={(tcapInfo.OperationCode >= 0 ? tcapInfo.OperationCode.ToString("X2") : "-")} | " +
                        //    $"From={Safe(sms.Sender)} | " +
                        //    $"To={Safe(sms.Receiver)} | " +
                        //    $"IMSI={Safe(resolvedImsi)} | " +
                        //    $"Part=- | " +
                        //    $"Time={Safe(sms.Timestamp)} | " +
                        //    $"Text={Safe(sms.Text)}");
                        continue;
                    }

                    // Multipart MT: print only when full text is ready
                    string fullText = SmsReassembler.AddPart(sms, resolvedImsi);
                    bool becameFullNow = !string.IsNullOrWhiteSpace(fullText);

                    if (becameFullNow)
                    {
                        sms.Text = fullText;
                        sms.PartNumber = sms.TotalParts;

                        TcapCorrelator.AddSms(tcapInfo.Otid, tcapInfo.Dtid, sms, map, tcapInfo.OperationCode);

                        resolvedImsi = ResolveImsiForSms(tcapInfo, map);

                        if (ShouldPrintForImsi(resolvedImsi))
                        {
                            Console.WriteLine(
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
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static bool ShouldPrintForImsi(string imsi)
        {
            if (string.IsNullOrWhiteSpace(imsi))
                return false;

            var current = _filterImsis;

            if (current == null || current.Count == 0)
                return true;

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

        private static bool IsBinaryMessage(SmsMessage sms)
        {
            if (sms == null)
                return false;

            // app-port addressed / OTA-like messages are usually binary
            if (sms.DestPort >= 0 || sms.SrcPort >= 0)
                return true;

            if (sms.IsBinary)
                return true;

            return false;
        }
    }
}