using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PacketDotNet.SMS
{
    public class MapMessage
    {
        public int OperationCode = -1;
        public byte[] SmRpUi;
        public string DestinationImsi;
    }

    public static class MapDecoder
    {
        public static MapMessage Decode(byte[] data)
        {
            var map = new MapMessage();

            try
            {
                int offset = 0;
                var root = Asn1Decoder.Decode(data, ref offset);
                if (root == null)
                    return map;

                var integers = new List<int>();
                var octets = new List<byte[]>();
                var imsiCandidates = new List<ImsiCandidate>();

                Find(root, null, integers, octets, imsiCandidates);

                // Prefer common SMS MAP operations, but keep first integer if none matched
                foreach (var op in integers)
                {
                    if (op == 0x1E || // common in your captures
                        op == 0x2C || op == 0x2D || op == 0x2E || op == 0x2F ||
                        op == 0x30 || op == 0x31 || op == 0x32 || op == 0x33 ||
                        op == 0x25)
                    {
                        map.OperationCode = op;
                        break;
                    }
                }

                if (map.OperationCode == -1 && integers.Count > 0)
                    map.OperationCode = integers[0];

                map.SmRpUi = SelectBestSmRpUi(octets);
                map.DestinationImsi = SelectBestImsi(imsiCandidates);

                return map;
            }
            catch
            {
                return map;
            }
        }

        private static void Find(
            Asn1Object node,
            Asn1Object parent,
            List<int> integers,
            List<byte[]> octets,
            List<ImsiCandidate> imsiCandidates)
        {
            if (node == null)
                return;

            // INTEGERs -> operation codes and similar fields
            if (node.Tag == 0x02 && node.Value != null && node.Value.Length >= 1 && node.Value.Length <= 4)
            {
                int val = 0;
                foreach (var b in node.Value)
                    val = (val << 8) | b;

                integers.Add(val);
            }

            // OCTET STRING / context specific primitive fields
            if ((node.Tag == 0x04 || IsContextSpecificPrimitive(node.Tag)) &&
                node.Value != null &&
                node.Value.Length > 0)
            {
                octets.Add(node.Value);
            }

            // IMSI candidates:
            // Commonly MAP IMSI is TBCD in context-specific primitive tags like 0x80,
            // but some stacks expose it in other primitive fields.
            if (node.Value != null && node.Value.Length >= 3)
            {
                string tbcdDigits = DecodeTBCD(node.Value);

                if (LooksLikeImsi(tbcdDigits))
                {
                    int score = ScoreImsiCandidate(node, parent, tbcdDigits);
                    imsiCandidates.Add(new ImsiCandidate
                    {
                        Value = tbcdDigits,
                        Score = score
                    });
                }
            }

            foreach (var c in node.Children)
                Find(c, node, integers, octets, imsiCandidates);
        }

        private static bool IsContextSpecificPrimitive(byte tag)
        {
            // context-specific primitive: class bits 10xxxxxx and primitive bit clear
            return (tag & 0xC0) == 0x80 && (tag & 0x20) == 0;
        }

        private static byte[] SelectBestSmRpUi(List<byte[]> octets)
        {
            if (octets == null || octets.Count == 0)
                return null;

            byte[] best = null;
            int bestScore = int.MinValue;

            foreach (var o in octets)
            {
                if (o == null || o.Length == 0)
                    continue;

                int score = ScoreSmRpUiCandidate(o);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = o;
                }
            }

            return best;
        }

        private static int ScoreSmRpUiCandidate(byte[] data)
        {
            int score = 0;

            // Direct TPDU: SMS-DELIVER or SMS-SUBMIT
            if (LooksLikeDirectTpdu(data))
                score += 100;

            // RPDU
            if (LooksLikeRpdu(data))
                score += 90;

            // Prefer useful lengths
            if (data.Length >= 10)
                score += 10;

            // De-prioritize likely IMSI blobs
            string tbcd = DecodeTBCD(data);
            if (LooksLikeImsi(tbcd))
                score -= 80;

            return score;
        }

        private static bool LooksLikeDirectTpdu(byte[] data)
        {
            if (data == null || data.Length < 5)
                return false;

            int mti = data[0] & 0x03;
            return mti == 0x00 || mti == 0x01;
        }

        private static bool LooksLikeRpdu(byte[] data)
        {
            if (data == null || data.Length < 3)
                return false;

            byte rpType = data[0];
            return rpType == 0x00 || rpType == 0x01 || rpType == 0x02 || rpType == 0x03 || rpType == 0x04 || rpType == 0x05;
        }

        private static bool LooksLikeImsi(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            // IMSI usually 14-16 digits, commonly 15
            if (s.Length < 14 || s.Length > 16)
                return false;

            // all digits
            if (!s.All(char.IsDigit))
                return false;

            return true;
        }

        private static int ScoreImsiCandidate(Asn1Object node, Asn1Object parent, string value)
        {
            int score = 0;

            // strongest hint: common MAP context tag
            if (node.Tag == 0x80)
                score += 50;

            // other context-specific primitive tags
            if (IsContextSpecificPrimitive(node.Tag))
                score += 20;

            // prefer standard IMSI length
            if (value.Length == 15)
                score += 20;
            else if (value.Length == 14 || value.Length == 16)
                score += 10;

            // common MCC/MNC starts seen in real IMSIs; mild boost only
            if (value.StartsWith("425"))
                score += 10;

            // if parent is context-specific / constructed, likely structured MAP arg field
            if (parent != null && (parent.Tag & 0xC0) == 0x80)
                score += 5;

            // de-prioritize obvious MSISDN-like long numbers that start with international prefix patterns
            if (value.StartsWith("972"))
                score -= 15;

            return score;
        }

        private static string SelectBestImsi(List<ImsiCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            return candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => Math.Abs(c.Value.Length - 15))
                .Select(c => c.Value)
                .FirstOrDefault();
        }

        private static string DecodeTBCD(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            var sb = new StringBuilder();

            foreach (byte b in data)
            {
                int low = b & 0x0F;
                int high = (b >> 4) & 0x0F;

                if (IsTbcdDigit(low))
                    sb.Append(low);
                else if (low == 0xF)
                    break;
                else
                    return null;

                if (high == 0xF)
                    break;

                if (IsTbcdDigit(high))
                    sb.Append(high);
                else
                    return null;
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static bool IsTbcdDigit(int nibble)
        {
            return nibble >= 0 && nibble <= 9;
        }

        private class ImsiCandidate
        {
            public string Value;
            public int Score;
        }
    }
}