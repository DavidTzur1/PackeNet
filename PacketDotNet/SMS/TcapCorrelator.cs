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
        public string RawSmRpUiHex;
        public bool IsFallbackStored;
        public string Timestamp;
        public DateTime LastSeenUtc;
    }

    public static class TcapCorrelator
    {
        private static readonly Dictionary<string, TcapSmsInfo> byOtid = new();
        private static readonly Dictionary<string, TcapSmsInfo> byDtid = new();

        private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(3);

        public static void AddSms(string otid, string dtid, SmsMessage sms, MapMessage map, int operationCode)
        {
            if (string.IsNullOrWhiteSpace(otid) && string.IsNullOrWhiteSpace(dtid))
                return;

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
                RawSmRpUiHex = map?.SmRpUi != null ? BitConverter.ToString(map.SmRpUi) : null,
                IsFallbackStored = sms == null,
                Timestamp = sms?.Timestamp ?? existingByOtid?.Timestamp ?? existingByDtid?.Timestamp,
                LastSeenUtc = DateTime.UtcNow
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
                    existingByOtid.LastSeenUtc = DateTime.UtcNow;

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

        public static void BindDtid(string otid, string dtid)
        {
            if (string.IsNullOrWhiteSpace(otid) || string.IsNullOrWhiteSpace(dtid))
                return;

            if (byOtid.TryGetValue(otid, out var info))
            {
                info.Dtid = dtid;
                info.LastSeenUtc = DateTime.UtcNow;

                if (string.IsNullOrWhiteSpace(info.RootKey))
                    info.RootKey = otid;

                byDtid[dtid] = info;
            }
        }

        public static TcapSmsInfo Find(string otid, string dtid)
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

        public static string ResolveRootKey(string otid, string dtid)
        {
            var info = Find(otid, dtid);
            if (!string.IsNullOrWhiteSpace(info?.RootKey))
                return info.RootKey;

            if (!string.IsNullOrWhiteSpace(otid))
                return otid;

            if (!string.IsNullOrWhiteSpace(dtid))
                return dtid;

            return null;
        }

        public static void Remove(TcapSmsInfo info)
        {
            if (info == null)
                return;

            if (!string.IsNullOrWhiteSpace(info.Otid))
                byOtid.Remove(info.Otid);

            if (!string.IsNullOrWhiteSpace(info.Dtid))
                byDtid.Remove(info.Dtid);
        }

        public static void CleanupExpired()
        {
            if (byOtid.Count == 0 && byDtid.Count == 0)
                return;

            DateTime cutoff = DateTime.UtcNow - EntryTtl;
            List<TcapSmsInfo> expired = null;

            foreach (var kv in byOtid)
            {
                var info = kv.Value;
                if (info == null || info.LastSeenUtc <= cutoff)
                {
                    expired ??= new List<TcapSmsInfo>();
                    expired.Add(info);
                }
            }

            foreach (var kv in byDtid)
            {
                var info = kv.Value;
                if (info == null || info.LastSeenUtc <= cutoff)
                {
                    expired ??= new List<TcapSmsInfo>();
                    expired.Add(info);
                }
            }

            if (expired == null)
                return;

            foreach (var info in expired)
            {
                if (info == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(info.Otid))
                    byOtid.Remove(info.Otid);

                if (!string.IsNullOrWhiteSpace(info.Dtid))
                    byDtid.Remove(info.Dtid);
            }
        }
    }
}