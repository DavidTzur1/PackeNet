using System;
using System.Collections.Generic;

namespace PacketDotNet.SMS
{
    public class SmsAckInfo
    {
        public int RpMessageReference;
        public string From;
        public string To;
        public string Text;
        public string DestinationImsi;
        public int PartNumber;
        public int TotalParts;
        public DateTime LastSeenUtc;
    }

    public static class AckCorrelator
    {
        private static readonly Dictionary<int, SmsAckInfo> store = new();
        private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(3);

        public static void AddSms(SmsMessage sms, MapMessage map)
        {
            if (sms == null || sms.RpMessageReference < 0)
                return;

            store[sms.RpMessageReference] = new SmsAckInfo
            {
                RpMessageReference = sms.RpMessageReference,
                From = sms.Sender,
                To = sms.Receiver,
                Text = sms.Text,
                DestinationImsi = map?.DestinationImsi,
                PartNumber = sms.PartNumber,
                TotalParts = sms.TotalParts,
                LastSeenUtc = DateTime.UtcNow
            };
        }

        public static SmsAckInfo FindAckMatch(SmsMessage sms)
        {
            if (sms == null || sms.RpMessageReference < 0)
                return null;

            if (store.TryGetValue(sms.RpMessageReference, out var match))
            {
                match.LastSeenUtc = DateTime.UtcNow;
                return match;
            }

            return null;
        }

        public static SmsAckInfo FindLast()
        {
            if (store.Count == 0)
                return null;

            SmsAckInfo last = null;
            foreach (var kv in store)
                last = kv.Value;

            if (last != null)
                last.LastSeenUtc = DateTime.UtcNow;

            return last;
        }

        public static void RemoveByReference(int rpMessageReference)
        {
            if (rpMessageReference < 0)
                return;

            store.Remove(rpMessageReference);
        }

        public static void CleanupExpired()
        {
            if (store.Count == 0)
                return;

            DateTime cutoff = DateTime.UtcNow - EntryTtl;
            List<int> expiredKeys = null;

            foreach (var kv in store)
            {
                var info = kv.Value;
                if (info == null || info.LastSeenUtc <= cutoff)
                {
                    expiredKeys ??= new List<int>();
                    expiredKeys.Add(kv.Key);
                }
            }

            if (expiredKeys == null)
                return;

            foreach (var key in expiredKeys)
                store.Remove(key);
        }
    }
}