using System;
using System.Collections.Generic;
using System.Linq;

namespace PacketDotNet.SMS
{
    public static class SmsReassembler
    {
        private class MultipartBucket
        {
            public string BaseKey;
            public int Generation;
            public string FullKey;

            public HashSet<string> Imsis = new(StringComparer.Ordinal);
            public HashSet<int> ReferenceNumbers = new();

            public string Sender;
            public string Receiver;
            public string TimeBucket;
            public int TotalParts;

            public Dictionary<int, SmsMessage> Parts = new();

            public DateTime FirstSeenUtc = DateTime.UtcNow;
            public DateTime LastUpdateUtc = DateTime.UtcNow;
        }

        private static readonly Dictionary<string, List<MultipartBucket>> store = new(StringComparer.Ordinal);
        private static readonly TimeSpan BucketKeepTime = TimeSpan.FromSeconds(45);

        public static string AddPart(SmsMessage sms, string imsi = null, string rootKey = null)
        {
            if (sms == null)
                return null;

            if (sms.TotalParts <= 1)
                return sms.Text;

            if (sms.TotalParts <= 0 || sms.PartNumber <= 0)
                return null;

            CleanupOld();

            string baseKey = BuildBaseKey(sms);
            if (string.IsNullOrWhiteSpace(baseKey))
                return null;

            if (!store.TryGetValue(baseKey, out var buckets))
            {
                buckets = new List<MultipartBucket>();
                store[baseKey] = buckets;
            }

            var bucket = SelectOrCreateBucket(buckets, baseKey, sms, imsi);
            if (bucket == null)
                return null;

            bucket.LastUpdateUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(imsi))
                bucket.Imsis.Add(imsi.Trim());

            if (sms.ReferenceNumber >= 0)
                bucket.ReferenceNumbers.Add(sms.ReferenceNumber);

            if (string.IsNullOrWhiteSpace(bucket.Sender))
                bucket.Sender = NormalizeAddressKey(sms.Sender);

            if (string.IsNullOrWhiteSpace(bucket.Receiver))
                bucket.Receiver = NormalizeAddressKey(sms.Receiver);

            if (string.IsNullOrWhiteSpace(bucket.TimeBucket))
                bucket.TimeBucket = NormalizeTimestampBucket(sms.Timestamp);

            if (bucket.TotalParts <= 0)
                bucket.TotalParts = sms.TotalParts;

            if (!bucket.Parts.TryGetValue(sms.PartNumber, out var existing))
            {
                bucket.Parts[sms.PartNumber] = sms;
            }
            else if (IsSamePart(existing, sms))
            {
                if (IsBetterPart(existing, sms))
                    bucket.Parts[sms.PartNumber] = sms;
            }
            else
            {
                bucket = CreateNewGenerationBucket(buckets, baseKey, sms, imsi);
                bucket.Parts[sms.PartNumber] = sms;
            }

            if (bucket.TotalParts > 0 &&
                bucket.Parts.Count == bucket.TotalParts &&
                Enumerable.Range(1, bucket.TotalParts).All(p => bucket.Parts.ContainsKey(p)))
            {
                string full = string.Concat(
                    bucket.Parts
                          .OrderBy(kv => kv.Key)
                          .Select(kv => kv.Value.Text ?? string.Empty));

                buckets.Remove(bucket);
                if (buckets.Count == 0)
                    store.Remove(baseKey);

                return full;
            }

            return null;
        }

        private static MultipartBucket SelectOrCreateBucket(
            List<MultipartBucket> buckets,
            string baseKey,
            SmsMessage sms,
            string imsi)
        {
            if (buckets == null)
                return null;

            // 1) Prefer a bucket that does not yet have this part number
            foreach (var b in buckets.OrderByDescending(x => x.LastUpdateUtc))
            {
                if (!b.Parts.ContainsKey(sms.PartNumber))
                    return b;
            }

            // 2) If same part already exists and looks like same content, reuse that bucket
            foreach (var b in buckets.OrderByDescending(x => x.LastUpdateUtc))
            {
                if (b.Parts.TryGetValue(sms.PartNumber, out var existing) && IsSamePart(existing, sms))
                    return b;
            }

            // 3) Otherwise create a new generation
            return CreateNewGenerationBucket(buckets, baseKey, sms, imsi);
        }

        private static MultipartBucket CreateNewGenerationBucket(
            List<MultipartBucket> buckets,
            string baseKey,
            SmsMessage sms,
            string imsi)
        {
            int generation = 1;
            if (buckets.Count > 0)
                generation = buckets.Max(b => b.Generation) + 1;

            var bucket = new MultipartBucket
            {
                BaseKey = baseKey,
                Generation = generation,
                FullKey = $"{baseKey}|GEN={generation}",
                TotalParts = sms.TotalParts,
                Sender = NormalizeAddressKey(sms.Sender),
                Receiver = NormalizeAddressKey(sms.Receiver),
                TimeBucket = NormalizeTimestampBucket(sms.Timestamp),
                FirstSeenUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(imsi))
                bucket.Imsis.Add(imsi.Trim());

            if (sms.ReferenceNumber >= 0)
                bucket.ReferenceNumbers.Add(sms.ReferenceNumber);

            buckets.Add(bucket);
            return bucket;
        }

        private static string BuildBaseKey(SmsMessage sms)
        {
            if (sms == null)
                return null;

            string sender = NormalizeAddressKey(sms.Sender);
            string receiver = NormalizeAddressKey(sms.Receiver);
            string total = sms.TotalParts > 0 ? sms.TotalParts.ToString() : "-";
            string timeBucket = NormalizeTimestampBucket(sms.Timestamp);

            if (sms.ReferenceNumber >= 0)
                return $"{sender}|{receiver}|REF={sms.ReferenceNumber}|TOTAL={total}|TB={timeBucket}";

            return $"{sender}|{receiver}|REF=NOREF|TOTAL={total}|TB={timeBucket}";
        }

        private static string NormalizeAddressKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;

            s = s.Trim();

            bool hasLetter = s.Any(char.IsLetter);
            if (hasLetter)
                return s.ToUpperInvariant();

            var chars = s.Where(c => char.IsDigit(c) || c == '+').ToArray();
            return chars.Length == 0 ? s.ToUpperInvariant() : new string(chars);
        }

        private static bool IsSamePart(SmsMessage a, SmsMessage b)
        {
            if (a == null || b == null)
                return false;

            string ta = NormalizeText(a.Text);
            string tb = NormalizeText(b.Text);

            if (string.IsNullOrWhiteSpace(ta) || string.IsNullOrWhiteSpace(tb))
                return false;

            return string.Equals(ta, tb, StringComparison.Ordinal);
        }

        private static bool IsBetterPart(SmsMessage existing, SmsMessage incoming)
        {
            if (incoming == null)
                return false;

            if (existing == null)
                return true;

            bool existingHasText = !string.IsNullOrWhiteSpace(existing.Text);
            bool incomingHasText = !string.IsNullOrWhiteSpace(incoming.Text);

            if (incomingHasText != existingHasText)
                return incomingHasText;

            bool existingHasSender = !string.IsNullOrWhiteSpace(existing.Sender);
            bool incomingHasSender = !string.IsNullOrWhiteSpace(incoming.Sender);

            if (incomingHasSender != existingHasSender)
                return incomingHasSender;

            bool existingHasReceiver = !string.IsNullOrWhiteSpace(existing.Receiver);
            bool incomingHasReceiver = !string.IsNullOrWhiteSpace(incoming.Receiver);

            if (incomingHasReceiver != existingHasReceiver)
                return incomingHasReceiver;

            return false;
        }

        private static string NormalizeTimestampBucket(string timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
                return "-";

            timestamp = timestamp.Trim();

            if (DateTime.TryParse(timestamp, out var dt))
            {
                var bucket = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0);
                return bucket.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return timestamp;
        }



        private static string NormalizeText(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;

            return s.Trim();
        }

        private static void CleanupOld()
        {
            if (store.Count == 0)
                return;

            var cutoff = DateTime.UtcNow - BucketKeepTime;
            var emptyBaseKeys = new List<string>();

            foreach (var kv in store)
            {
                kv.Value.RemoveAll(b => b == null || b.LastUpdateUtc < cutoff);

                if (kv.Value.Count == 0)
                    emptyBaseKeys.Add(kv.Key);
            }

            foreach (var key in emptyBaseKeys)
                store.Remove(key);
        }
    }
}