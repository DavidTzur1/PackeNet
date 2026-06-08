using System;
using System.Collections.Generic;

namespace PacketDotNet.SMS
{
    public class TcapSmsInfo
    {
        public string Otid;
        public string Dtid;
        public string RootKey;

        public string From;
        public string To;
        public string Text;
        public string DestinationImsi;
        public int PartNumber;
        public int TotalParts;
        public int OperationCode;

        public bool IsFallbackStored;
        public string Timestamp;

        // Created once and never changed.
        // Use this for "fresh fallback" decisions.
        public DateTime CreatedUtc;

        // Updated on normal access / reuse.
        public DateTime LastSeenUtc;
    }

    public static class TcapCorrelator
    {
        private static readonly object _lock = new();

        private static readonly Dictionary<string, TcapSmsInfo> byOtid = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, TcapSmsInfo> byDtid = new(StringComparer.Ordinal);

        private static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan AckFallbackFreshWindow = TimeSpan.FromSeconds(15);

        public static void AddSms(string otid, string dtid, SmsMessage sms, MapMessage map, int operationCode)
        {
            if (string.IsNullOrWhiteSpace(otid) && string.IsNullOrWhiteSpace(dtid))
                return;

            lock (_lock)
            {
                TcapSmsInfo existingByOtid = null;
                if (!string.IsNullOrWhiteSpace(otid))
                    byOtid.TryGetValue(otid, out existingByOtid);

                TcapSmsInfo existingByDtid = null;
                if (!string.IsNullOrWhiteSpace(dtid))
                    byDtid.TryGetValue(dtid, out existingByDtid);

                string resolvedImsi =
                    map?.DestinationImsi ??
                    existingByOtid?.DestinationImsi ??
                    existingByDtid?.DestinationImsi;

                string resolvedRootKey =
                    existingByOtid?.RootKey ??
                    existingByDtid?.RootKey ??
                    (!string.IsNullOrWhiteSpace(otid) ? otid : dtid);

                var now = DateTime.UtcNow;

                var incoming = new TcapSmsInfo
                {
                    Otid = otid,
                    Dtid = dtid,
                    RootKey = resolvedRootKey,

                    From = sms?.Sender ?? existingByOtid?.From ?? existingByDtid?.From,
                    To = sms?.Receiver ?? existingByOtid?.To ?? existingByDtid?.To,
                    Text = sms?.Text,
                    DestinationImsi = resolvedImsi,
                    PartNumber = sms?.PartNumber ?? 0,
                    TotalParts = sms?.TotalParts ?? 0,
                    OperationCode = operationCode,
                    IsFallbackStored = sms == null,
                    Timestamp = sms?.Timestamp ?? existingByOtid?.Timestamp ?? existingByDtid?.Timestamp,
                    CreatedUtc = now,
                    LastSeenUtc = now
                };

                if (existingByOtid != null)
                {
                    bool existingIsFullMultipart =
                        existingByOtid.TotalParts > 1 &&
                        existingByOtid.PartNumber == existingByOtid.TotalParts &&
                        !string.IsNullOrWhiteSpace(existingByOtid.Text);

                    bool incomingIsPartialMultipart =
                        incoming.TotalParts > 1 &&
                        incoming.PartNumber > 0 &&
                        incoming.PartNumber < incoming.TotalParts;

                    if (existingIsFullMultipart && incomingIsPartialMultipart)
                    {
                        existingByOtid.LastSeenUtc = now;

                        if (!string.IsNullOrWhiteSpace(dtid))
                            byDtid[dtid] = existingByOtid;

                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(otid))
                    byOtid[otid] = incoming;

                if (!string.IsNullOrWhiteSpace(dtid))
                    byDtid[dtid] = incoming;
            }
        }

        public static void BindDtid(string otid, string dtid)
        {
            if (string.IsNullOrWhiteSpace(otid) || string.IsNullOrWhiteSpace(dtid))
                return;

            lock (_lock)
            {
                if (byOtid.TryGetValue(otid, out var info))
                {
                    info.Dtid = dtid;
                    info.LastSeenUtc = DateTime.UtcNow;

                    if (string.IsNullOrWhiteSpace(info.RootKey))
                        info.RootKey = otid;

                    byDtid[dtid] = info;
                }
            }
        }

        // Normal lookup for MT flow / IMSI resolution.
        // Keep fallback here so MAP-MT messages continue to appear.
        public static TcapSmsInfo Find(string otid, string dtid)
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(otid) && byOtid.TryGetValue(otid, out var a))
                {
                    a.LastSeenUtc = DateTime.UtcNow;
                    return a;
                }

                if (!string.IsNullOrWhiteSpace(dtid) && byDtid.TryGetValue(dtid, out var b))
                {
                    b.LastSeenUtc = DateTime.UtcNow;
                    return b;
                }

                if (!string.IsNullOrWhiteSpace(dtid) && byOtid.TryGetValue(dtid, out var c))
                {
                    c.LastSeenUtc = DateTime.UtcNow;
                    return c;
                }

                if (!string.IsNullOrWhiteSpace(otid) && byDtid.TryGetValue(otid, out var d))
                {
                    d.LastSeenUtc = DateTime.UtcNow;
                    return d;
                }

                return null;
            }
        }

        // ACK lookup: strict first, then guarded fallback only for very fresh entries.
        // IMPORTANT: freshness is based on CreatedUtc, not LastSeenUtc.
        public static TcapSmsInfo FindWithSource(string otid, string dtid, out string source)
        {
            source = "NONE";

            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(otid) && byOtid.TryGetValue(otid, out var a))
                {
                    a.LastSeenUtc = DateTime.UtcNow;
                    source = "OTID";
                    return a;
                }

                if (!string.IsNullOrWhiteSpace(dtid) && byDtid.TryGetValue(dtid, out var b))
                {
                    b.LastSeenUtc = DateTime.UtcNow;
                    source = "DTID";
                    return b;
                }

                if (!string.IsNullOrWhiteSpace(dtid) && byOtid.TryGetValue(dtid, out var c))
                {
                    var age = DateTime.UtcNow - c.CreatedUtc;
                    if (age <= AckFallbackFreshWindow && !string.IsNullOrWhiteSpace(c.Text))
                    {
                        c.LastSeenUtc = DateTime.UtcNow;
                        source = "DTID_AS_OTID_FRESH";
                        return c;
                    }
                }

                if (!string.IsNullOrWhiteSpace(otid) && byDtid.TryGetValue(otid, out var d))
                {
                    var age = DateTime.UtcNow - d.CreatedUtc;
                    if (age <= AckFallbackFreshWindow && !string.IsNullOrWhiteSpace(d.Text))
                    {
                        d.LastSeenUtc = DateTime.UtcNow;
                        source = "OTID_AS_DTID_FRESH";
                        return d;
                    }
                }

                return null;
            }
        }

        public static void Remove(TcapSmsInfo info)
        {
            if (info == null)
                return;

            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(info.Otid))
                {
                    if (byOtid.TryGetValue(info.Otid, out var a) && ReferenceEquals(a, info))
                        byOtid.Remove(info.Otid);

                    if (byDtid.TryGetValue(info.Otid, out var b) && ReferenceEquals(b, info))
                        byDtid.Remove(info.Otid);
                }

                if (!string.IsNullOrWhiteSpace(info.Dtid))
                {
                    if (byDtid.TryGetValue(info.Dtid, out var c) && ReferenceEquals(c, info))
                        byDtid.Remove(info.Dtid);

                    if (byOtid.TryGetValue(info.Dtid, out var d) && ReferenceEquals(d, info))
                        byOtid.Remove(info.Dtid);
                }
            }
        }

        public static void CleanupExpired()
        {
            lock (_lock)
            {
                if (byOtid.Count == 0 && byDtid.Count == 0)
                    return;

                DateTime cutoff = DateTime.UtcNow - EntryTtl;

                List<string> otidKeysToRemove = null;
                foreach (var kv in byOtid)
                {
                    var info = kv.Value;
                    if (info == null || info.LastSeenUtc <= cutoff)
                    {
                        otidKeysToRemove ??= new List<string>();
                        otidKeysToRemove.Add(kv.Key);
                    }
                }

                if (otidKeysToRemove != null)
                {
                    foreach (var key in otidKeysToRemove)
                        byOtid.Remove(key);
                }

                List<string> dtidKeysToRemove = null;
                foreach (var kv in byDtid)
                {
                    var info = kv.Value;
                    if (info == null || info.LastSeenUtc <= cutoff)
                    {
                        dtidKeysToRemove ??= new List<string>();
                        dtidKeysToRemove.Add(kv.Key);
                    }
                }

                if (dtidKeysToRemove != null)
                {
                    foreach (var key in dtidKeysToRemove)
                        byDtid.Remove(key);
                }
            }
        }
    }
}