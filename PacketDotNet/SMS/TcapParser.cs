using System;
using System.Collections.Generic;

namespace PacketDotNet.SMS
{
    public class TcapInfo
    {
        public byte MessageType;
        public string Otid;
        public string Dtid;
        public int OperationCode = -1;
        public bool HasReturnResult;
        public bool HasReturnError;
        public int ReturnErrorCode = -1;
        public int InvokeId = -1;

        // Clean summary only
        public string ReturnErrorSummary;

        // Raw A3 bytes as hex, for cases where no ErrorCode was extracted
        public string ReturnErrorRawHex;

        public bool IsBegin => MessageType == 0x62;
        public bool IsEnd => MessageType == 0x64;
        public bool IsContinue => MessageType == 0x65;
        public bool IsAbort => MessageType == 0x67;
    }

    public static class TcapParser
    {
        public static TcapInfo Parse(byte[] data)
        {
            if (data == null || data.Length < 2)
                return null;

            try
            {
                int offset = 0;
                var root = Asn1Decoder.Decode(data, ref offset);
                if (root == null)
                    return null;

                var info = new TcapInfo
                {
                    MessageType = root.Tag
                };

                Walk(root, info);
                return info;
            }
            catch
            {
                return null;
            }
        }

        private static void Walk(Asn1Object node, TcapInfo info)
        {
            if (node == null || info == null)
                return;

            if (node.Tag == 0x48 && node.Value != null && node.Value.Length > 0)
                info.Otid = ToHex(node.Value);

            if (node.Tag == 0x49 && node.Value != null && node.Value.Length > 0)
                info.Dtid = ToHex(node.Value);

            if (node.Tag == 0xA2)
                info.HasReturnResult = true;

            if (node.Tag == 0xA3)
            {
                info.HasReturnError = true;
                ParseReturnError(node, info);
            }

            if (node.Tag == 0x02 && node.Value != null && node.Value.Length >= 1 && node.Value.Length <= 4)
            {
                int val = DecodeInteger(node.Value);

                if (info.OperationCode == -1)
                    info.OperationCode = val;
            }

            foreach (var c in node.Children)
                Walk(c, info);
        }

        private static void ParseReturnError(Asn1Object returnErrorNode, TcapInfo info)
        {
            if (returnErrorNode == null || info == null)
                return;

            var scalars = new List<int>();
            CollectScalarErrorValues(returnErrorNode, scalars);

            if (scalars.Count >= 1 && info.InvokeId == -1)
                info.InvokeId = scalars[0];

            if (scalars.Count >= 2 && info.ReturnErrorCode == -1)
                info.ReturnErrorCode = scalars[1];

            info.ReturnErrorSummary =
                $"TCAP-RETURN-ERROR | " +
                $"InvokeId={SafeInt(info.InvokeId)} | " +
                $"ErrorCode={SafeInt(info.ReturnErrorCode)} | " +
                $"Scalars={FormatScalars(scalars)}";

            info.ReturnErrorRawHex = BuildTlvHex(returnErrorNode);
        }

        private static void CollectScalarErrorValues(Asn1Object node, List<int> values)
        {
            if (node == null)
                return;

            if (node.Tag == 0x02 && node.Value != null && node.Value.Length >= 1 && node.Value.Length <= 4)
            {
                values.Add(DecodeInteger(node.Value));
            }
            else if (node.Tag == 0x0A && node.Value != null && node.Value.Length >= 1 && node.Value.Length <= 4)
            {
                values.Add(DecodeInteger(node.Value));
            }

            foreach (var child in node.Children)
                CollectScalarErrorValues(child, values);
        }

        private static int DecodeInteger(byte[] data)
        {
            int val = 0;
            foreach (var b in data)
                val = (val << 8) | b;

            return val;
        }

        private static string FormatScalars(List<int> values)
        {
            if (values == null || values.Count == 0)
                return "-";

            return string.Join(",", values);
        }

        private static string SafeInt(int value)
        {
            return value >= 0 ? value.ToString() : "-";
        }

        private static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0)
                return "-";

            return BitConverter.ToString(data).Replace("-", "");
        }

        private static string BuildTlvHex(Asn1Object node)
        {
            if (node == null)
                return "-";

            try
            {
                var bytes = new List<byte>();

                bytes.Add(node.Tag);
                EncodeLength(bytes, node.Length);

                if (node.Value != null && node.Value.Length > 0)
                    bytes.AddRange(node.Value);

                return ToHex(bytes.ToArray());
            }
            catch
            {
                return "-";
            }
        }

        private static void EncodeLength(List<byte> output, int length)
        {
            if (output == null)
                return;

            if (length < 0x80)
            {
                output.Add((byte)length);
                return;
            }

            var lenBytes = new List<byte>();
            int temp = length;

            while (temp > 0)
            {
                lenBytes.Insert(0, (byte)(temp & 0xFF));
                temp >>= 8;
            }

            output.Add((byte)(0x80 | lenBytes.Count));
            output.AddRange(lenBytes);
        }
    }
}