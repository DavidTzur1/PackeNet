using System;
using System.Linq;
using System.Text;

namespace PacketDotNet.SMS
{
    public class SmsMessage
    {
        public string Sender;
        public string Receiver;
        public string Text;
        public string Timestamp;

        public int ReferenceNumber = -1;
        public int TotalParts;
        public int PartNumber;

        public int RpMessageReference = -1;
        public bool IsRpAck;

        // UDH National Language Identifier IEs
        public int NationalLanguageSingleShift = -1;   // IEI 0x24
        public int NationalLanguageLockingShift = -1; // IEI 0x25

        // Binary / OTA-related helpers
        public byte Dcs;
        public bool HasUdh;
        public bool IsBinary;
        public int DestPort = -1;
        public int SrcPort = -1;
        public byte[] RawUserData;
    }

    public static class SmsDecoder
    {
        public static SmsMessage Decode(byte[] smRpUi)
        {
            if (smRpUi == null || smRpUi.Length == 0)
                return null;

            var sms = ParseRpdu(smRpUi);

            // Return only if RPDU parsing actually produced useful SMS content.
            // Do NOT return just because IsRpAck=true, because some direct TPDUs
            // begin with 0x04 and get misclassified as RP-ACK.
            if (sms != null &&
                (!string.IsNullOrWhiteSpace(sms.Sender) ||
                 !string.IsNullOrWhiteSpace(sms.Receiver) ||
                 !string.IsNullOrWhiteSpace(sms.Text)))
            {
                return sms;
            }

            // Fallback: treat SM-RP-UI as direct TPDU
            var direct = new SmsMessage();
            ParseTpdu(smRpUi, direct);

            if (!string.IsNullOrWhiteSpace(direct.Sender) ||
                !string.IsNullOrWhiteSpace(direct.Receiver) ||
                !string.IsNullOrWhiteSpace(direct.Text))
            {
                return direct;
            }

            // If RPDU really was just ACK with no embedded text, keep that result.
            if (sms != null && sms.IsRpAck)
                return sms;

            return null;
        }

        private static SmsMessage ParseRpdu(byte[] data)
        {
            if (data == null || data.Length < 2)
                return null;

            int i = 0;
            byte rpMessageType = data[i++];
            byte rpMessageReference = data[i++];

            var sms = new SmsMessage
            {
                RpMessageReference = rpMessageReference
            };

            // RP-DATA MO / MT
            if (rpMessageType == 0x00 || rpMessageType == 0x01)
            {
                if (i >= data.Length)
                    return sms;

                int originatorLen = data[i++];
                if (i + originatorLen > data.Length)
                    return sms;
                i += originatorLen;

                if (i >= data.Length)
                    return sms;

                int destinationLen = data[i++];
                if (i + destinationLen > data.Length)
                    return sms;
                i += destinationLen;

                if (i >= data.Length)
                    return sms;

                int userDataLen = data[i++];
                if (userDataLen <= 0 || i + userDataLen > data.Length)
                    return sms;

                byte[] tpdu = new byte[userDataLen];
                Buffer.BlockCopy(data, i, tpdu, 0, userDataLen);

                ParseTpdu(tpdu, sms);
                return sms;
            }

            // RP-ACK / RP-ERROR
            if (rpMessageType == 0x02 || rpMessageType == 0x03 || rpMessageType == 0x04 || rpMessageType == 0x05)
            {
                sms.IsRpAck = true;

                if (i < data.Length)
                {
                    int remaining = data.Length - i;

                    // Some traces may carry extra payload after RP-ACK/RP-ERROR.
                    // First try remaining bytes directly as TPDU.
                    if (remaining > 0)
                    {
                        byte[] tpdu = new byte[remaining];
                        Buffer.BlockCopy(data, i, tpdu, 0, remaining);

                        ParseTpdu(tpdu, sms);

                        if (!string.IsNullOrWhiteSpace(sms.Sender) ||
                            !string.IsNullOrWhiteSpace(sms.Receiver) ||
                            !string.IsNullOrWhiteSpace(sms.Text))
                        {
                            return sms;
                        }
                    }
                }

                return sms;
            }

            return sms;
        }

        private static void ParseTpdu(byte[] data, SmsMessage sms)
        {
            if (data == null || data.Length < 2 || sms == null)
                return;

            int i = 0;
            byte firstOctet = data[i++];
            int mti = firstOctet & 0x03;
            bool hasUdh = (firstOctet & 0x40) != 0;

            sms.HasUdh = hasUdh;

            switch (mti)
            {
                case 0x00: // SMS-DELIVER
                    ParseSmsDeliver(data, ref i, hasUdh, sms);
                    break;

                case 0x01: // SMS-SUBMIT
                    ParseSmsSubmit(data, ref i, firstOctet, hasUdh, sms);
                    break;

                default:
                    sms.Text = null;
                    break;
            }
        }

        private static void ParseSmsDeliver(byte[] data, ref int i, bool hasUdh, SmsMessage sms)
        {
            if (i + 2 > data.Length)
                return;

            int senderLenDigits = data[i++];
            byte senderToa = data[i++];

            int senderBytes = (senderLenDigits + 1) / 2;
            if (i + senderBytes > data.Length)
                return;

            byte[] senderRaw = new byte[senderBytes];
            Buffer.BlockCopy(data, i, senderRaw, 0, senderBytes);
            i += senderBytes;

            sms.Sender = DecodeAddressField(senderRaw, senderLenDigits, senderToa);

            if (i + 2 > data.Length)
                return;

            i++; // PID
            byte dcs = data[i++];
            sms.Dcs = dcs;
            sms.HasUdh = hasUdh;

            if (i + 7 > data.Length)
                return;

            sms.Timestamp = DecodeTimestamp(data, i);
            i += 7;

            if (i >= data.Length)
                return;

            int userDataLen = data[i++];
            DecodeUserData(data, ref i, userDataLen, dcs, hasUdh, sms);
        }

        private static void ParseSmsSubmit(byte[] data, ref int i, byte firstOctet, bool hasUdh, SmsMessage sms)
        {
            if (i >= data.Length)
                return;

            i++; // TP-MR

            if (i + 2 > data.Length)
                return;

            int destLenDigits = data[i++];
            byte destToa = data[i++];

            int destBytes = (destLenDigits + 1) / 2;
            if (i + destBytes > data.Length)
                return;

            byte[] destRaw = new byte[destBytes];
            Buffer.BlockCopy(data, i, destRaw, 0, destRaw.Length);
            i += destBytes;

            sms.Receiver = DecodeAddressField(destRaw, destLenDigits, destToa);

            if (i + 2 > data.Length)
                return;

            i++; // PID
            byte dcs = data[i++];
            sms.Dcs = dcs;
            sms.HasUdh = hasUdh;

            int vpf = (firstOctet >> 3) & 0x03;
            if (vpf == 0x02)
            {
                if (i >= data.Length)
                    return;
                i++;
            }
            else if (vpf == 0x01 || vpf == 0x03)
            {
                if (i + 7 > data.Length)
                    return;
                i += 7;
            }

            if (i >= data.Length)
                return;

            int userDataLen = data[i++];
            DecodeUserData(data, ref i, userDataLen, dcs, hasUdh, sms);
        }

        private static void DecodeUserData(byte[] data, ref int i, int userDataLen, byte dcs, bool hasUdh, SmsMessage sms)
        {
            int udStart = i;
            int udhBytes = 0;

            if (hasUdh)
            {
                if (i >= data.Length)
                    return;

                int udhLength = data[i++];
                udhBytes = udhLength + 1;

                if (i + udhLength > data.Length)
                    return;

                ParseUdh(data, i, udhLength, sms);
                i += udhLength;
            }

            int payloadStart = i;
            int payloadAvailable = data.Length - payloadStart;
            if (payloadAvailable < 0)
                payloadAvailable = 0;

            if (IsUcs2Dcs(dcs))
            {
                int payloadBytes = userDataLen - udhBytes;
                if (payloadBytes < 0)
                    payloadBytes = 0;

                if (payloadBytes > payloadAvailable)
                    payloadBytes = payloadAvailable;

                if ((payloadBytes & 1) != 0)
                    payloadBytes--;

                if (payloadBytes <= 0)
                {
                    sms.Text = string.Empty;
                    return;
                }

                byte[] payload = new byte[payloadBytes];
                Buffer.BlockCopy(data, payloadStart, payload, 0, payloadBytes);

                sms.RawUserData = payload;

                string text = TryDecodeUcs2(payload);
                sms.Text = text ?? BitConverter.ToString(payload);
                sms.IsBinary = text == null && LooksBinaryPayload(payload, sms);
                return;
            }

            if (IsGsm7Dcs(dcs))
            {
                int packedAvailableFromUd = data.Length - udStart;
                if (packedAvailableFromUd < 0)
                    packedAvailableFromUd = 0;

                int totalPackedBytes = packedAvailableFromUd;
                if (totalPackedBytes <= 0)
                {
                    sms.Text = string.Empty;
                    return;
                }

                byte[] packedUd = new byte[totalPackedBytes];
                Buffer.BlockCopy(data, udStart, packedUd, 0, totalPackedBytes);

                int headerBits = hasUdh ? (udhBytes * 8) : 0;
                int paddingBits = (7 - (headerBits % 7)) % 7;
                int skipBits = headerBits + paddingBits;

                int textSeptets = userDataLen - ((headerBits + paddingBits) / 7);
                if (textSeptets < 0)
                    textSeptets = 0;

                string gsmText = Decode7BitPackedWithNationalLanguage(
                    packedUd,
                    textSeptets,
                    skipBits,
                    sms.NationalLanguageLockingShift,
                    sms.NationalLanguageSingleShift);

                if (LooksReasonableText(gsmText))
                {
                    sms.Text = gsmText.TrimEnd('\0', '@');
                    sms.IsBinary = false;
                    return;
                }

                int payloadBytes = payloadAvailable;
                if (payloadBytes <= 0)
                {
                    sms.Text = string.Empty;
                    return;
                }

                byte[] payload = new byte[payloadBytes];
                Buffer.BlockCopy(data, payloadStart, payload, 0, payloadBytes);

                sms.RawUserData = payload;
                sms.Text = Decode8BitSafe(payload);
                sms.IsBinary = LooksBinaryPayload(payload, sms);
                return;
            }

            {
                int payloadBytes = payloadAvailable;
                if (payloadBytes <= 0)
                {
                    sms.Text = string.Empty;
                    return;
                }

                byte[] payload = new byte[payloadBytes];
                Buffer.BlockCopy(data, payloadStart, payload, 0, payloadBytes);

                sms.RawUserData = payload;

                string ucs2Guess = TryDecodeUcs2Heuristic(payload);
                if (!string.IsNullOrWhiteSpace(ucs2Guess) && LooksReasonableText(ucs2Guess))
                {
                    sms.Text = ucs2Guess;
                    sms.IsBinary = false;
                    return;
                }

                sms.Text = Decode8BitSafe(payload);
                sms.IsBinary = true;
            }
        }

        private static bool IsUcs2Dcs(byte dcs)
        {
            return (dcs & 0x0C) == 0x08;
        }

        private static bool IsGsm7Dcs(byte dcs)
        {
            return (dcs & 0x0C) == 0x00;
        }

        private static void ParseUdh(byte[] data, int index, int udhLength, SmsMessage sms)
        {
            int i = index;
            int end = index + udhLength;

            while (i + 2 <= end && i + 2 <= data.Length)
            {
                byte iei = data[i++];
                byte len = data[i++];

                if (i + len > end || i + len > data.Length)
                    break;

                if (iei == 0x00 && len >= 3)
                {
                    sms.ReferenceNumber = data[i];
                    sms.TotalParts = data[i + 1];
                    sms.PartNumber = data[i + 2];
                }
                else if (iei == 0x08 && len >= 4)
                {
                    sms.ReferenceNumber = (data[i] << 8) | data[i + 1];
                    sms.TotalParts = data[i + 2];
                    sms.PartNumber = data[i + 3];
                }
                else if (iei == 0x04 && len >= 2)
                {
                    sms.DestPort = data[i];
                    sms.SrcPort = data[i + 1];
                }
                else if (iei == 0x05 && len >= 4)
                {
                    sms.DestPort = (data[i] << 8) | data[i + 1];
                    sms.SrcPort = (data[i + 2] << 8) | data[i + 3];
                }
                else if (iei == 0x24 && len >= 1)
                {
                    sms.NationalLanguageSingleShift = data[i];
                }
                else if (iei == 0x25 && len >= 1)
                {
                    sms.NationalLanguageLockingShift = data[i];
                }

                i += len;
            }
        }

        private static bool LooksBinaryPayload(byte[] payload, SmsMessage sms)
        {
            if (payload == null || payload.Length == 0)
                return false;

            if ((sms?.Dcs & 0x0C) == 0x04)
                return true;

            if (sms != null && (sms.DestPort >= 0 || sms.SrcPort >= 0))
                return true;

            int zeroes = 0;
            int control = 0;

            foreach (byte b in payload)
            {
                if (b == 0x00)
                    zeroes++;

                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                    control++;
            }

            return zeroes > payload.Length / 6 || control > payload.Length / 5;
        }

        private static string DecodeTimestamp(byte[] data, int index)
        {
            if (index + 7 > data.Length)
                return null;

            string yy = SwapDigits(data[index]);
            string mm = SwapDigits(data[index + 1]);
            string dd = SwapDigits(data[index + 2]);
            string hh = SwapDigits(data[index + 3]);
            string mi = SwapDigits(data[index + 4]);
            string ss = SwapDigits(data[index + 5]);

            return $"20{yy}-{mm}-{dd} {hh}:{mi}:{ss}";
        }

        private static string SwapDigits(byte b)
        {
            int low = b & 0x0F;
            int high = (b >> 4) & 0x0F;
            return $"{low}{high}";
        }

        private static string TryDecodeUcs2(byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0)
                    return string.Empty;

                if ((data.Length & 1) != 0)
                    return null;

                string s = Encoding.BigEndianUnicode.GetString(data);
                return LooksReasonableText(s) ? s : null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryDecodeUcs2Heuristic(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 2)
                    return null;

                int len = data.Length;
                if ((len & 1) != 0)
                    len--;

                if (len <= 0)
                    return null;

                byte[] even = new byte[len];
                Buffer.BlockCopy(data, 0, even, 0, len);

                return Encoding.BigEndianUnicode.GetString(even);
            }
            catch
            {
                return null;
            }
        }

        private static string Decode8BitSafe(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            string latin1 = Encoding.GetEncoding("ISO-8859-1").GetString(data);

            if (LooksReasonableText(latin1))
                return latin1;

            return BitConverter.ToString(data);
        }

        private static string DecodeAddressField(byte[] data, int addressLength, byte toa)
        {
            if (data == null || data.Length == 0)
                return null;

            int ton = (toa >> 4) & 0x07;

            if (ton == 5)
            {
                int septetCount = (addressLength * 4) / 7;
                string alpha = Decode7BitPacked(data, septetCount);
                return alpha?.TrimEnd('@', '\0');
            }

            string number = DecodeSemiOctet(data, addressLength);

            if (ton == 1 && !string.IsNullOrWhiteSpace(number))
                return "+" + number;

            return number;
        }

        private static string DecodeSemiOctet(byte[] data, int digitCount)
        {
            var sb = new StringBuilder();

            foreach (var b in data)
            {
                int low = b & 0x0F;
                int high = (b >> 4) & 0x0F;

                if (sb.Length < digitCount && low <= 9)
                    sb.Append(low);

                if (sb.Length < digitCount && high <= 9)
                    sb.Append(high);
            }

            return sb.ToString();
        }

        private static string Decode7BitPacked(byte[] data, int septetCount)
        {
            if (data == null || data.Length == 0 || septetCount <= 0)
                return string.Empty;

            var result = new StringBuilder();
            bool escape = false;

            for (int s = 0; s < septetCount; s++)
            {
                int bitIndex = s * 7;
                int byteIndex = bitIndex / 8;
                int bitOffset = bitIndex % 8;

                if (byteIndex >= data.Length)
                    break;

                int value = (data[byteIndex] >> bitOffset) & 0x7F;

                if (bitOffset > 1 && byteIndex + 1 < data.Length)
                    value |= (data[byteIndex + 1] << (8 - bitOffset)) & 0x7F;

                AppendDecodedChar(result, value & 0x7F, ref escape, -1, -1);
            }

            return result.ToString();
        }

        private static string Decode7BitPackedWithNationalLanguage(
            byte[] data,
            int septetCount,
            int skipBits,
            int lockingShiftNli,
            int singleShiftNli)
        {
            if (data == null || data.Length == 0 || septetCount <= 0)
                return string.Empty;

            var result = new StringBuilder();
            bool escape = false;

            for (int s = 0; s < septetCount; s++)
            {
                int bitIndex = skipBits + (s * 7);
                int byteIndex = bitIndex / 8;
                int bitOffset = bitIndex % 8;

                if (byteIndex >= data.Length)
                    break;

                int value = (data[byteIndex] >> bitOffset) & 0x7F;

                if (bitOffset != 0 && byteIndex + 1 < data.Length)
                    value |= (data[byteIndex + 1] << (8 - bitOffset)) & 0x7F;

                AppendDecodedChar(result, value & 0x7F, ref escape, lockingShiftNli, singleShiftNli);
            }

            return result.ToString();
        }

        private static void AppendDecodedChar(
            StringBuilder result,
            int value,
            ref bool escape,
            int lockingShiftNli,
            int singleShiftNli)
        {
            if (result == null)
                return;

            if (escape)
            {
                if (TryMapNationalSingleShift(singleShiftNli, value, out char singleShiftChar))
                {
                    result.Append(singleShiftChar);
                }
                else if (TryMapGsmDefaultExtension(value, out char extChar))
                {
                    result.Append(extChar);
                }
                else
                {
                    result.Append(' ');
                }

                escape = false;
                return;
            }

            if (value == 0x1B)
            {
                escape = true;
                return;
            }

            if (TryMapNationalLockingShift(lockingShiftNli, value, out char lockingChar))
            {
                result.Append(lockingChar);
                return;
            }

            result.Append(GsmToChar(value));
        }

        private static bool TryMapNationalLockingShift(int nli, int value, out char c)
        {
            c = '\0';

            if (nli == 0x0D)
            {
                switch (value)
                {
                    case 0x00: c = '@'; return true;
                    case 0x01: c = '£'; return true;
                    case 0x02: c = '$'; return true;
                    case 0x03: c = '¥'; return true;
                    case 0x04: c = 'è'; return true;
                    case 0x05: c = 'é'; return true;
                    case 0x06: c = 'ù'; return true;
                    case 0x07: c = 'ì'; return true;
                    case 0x08: c = 'ò'; return true;
                    case 0x09: c = 'Ç'; return true;
                    case 0x0A: c = '\n'; return true;
                    case 0x0B: c = 'Ø'; return true;
                    case 0x0C: c = 'ø'; return true;
                    case 0x0D: c = '\r'; return true;
                    case 0x0E: c = 'Å'; return true;
                    case 0x0F: c = 'å'; return true;
                    case 0x10: c = 'Δ'; return true;
                    case 0x11: c = '_'; return true;
                    case 0x12: c = 'Φ'; return true;
                    case 0x13: c = 'Γ'; return true;
                    case 0x14: c = 'Λ'; return true;
                    case 0x15: c = 'Ω'; return true;
                    case 0x16: c = 'Π'; return true;
                    case 0x17: c = 'Ψ'; return true;
                    case 0x18: c = 'Σ'; return true;
                    case 0x19: c = 'Θ'; return true;
                    case 0x1A: c = 'Ξ'; return true;
                    case 0x1C: c = 'Æ'; return true;
                    case 0x1D: c = 'æ'; return true;
                    case 0x1E: c = 'ß'; return true;
                    case 0x1F: c = 'É'; return true;
                    case 0x20: c = ' '; return true;
                    case 0x21: c = '!'; return true;
                    case 0x22: c = '"'; return true;
                    case 0x23: c = '#'; return true;
                    case 0x24: c = '¤'; return true;
                    case 0x25: c = '%'; return true;
                    case 0x26: c = '&'; return true;
                    case 0x27: c = '\''; return true;
                    case 0x28: c = '('; return true;
                    case 0x29: c = ')'; return true;
                    case 0x2A: c = '*'; return true;
                    case 0x2B: c = '+'; return true;
                    case 0x2C: c = ','; return true;
                    case 0x2D: c = '-'; return true;
                    case 0x2E: c = '.'; return true;
                    case 0x2F: c = '/'; return true;
                    case 0x30: c = '0'; return true;
                    case 0x31: c = '1'; return true;
                    case 0x32: c = '2'; return true;
                    case 0x33: c = '3'; return true;
                    case 0x34: c = '4'; return true;
                    case 0x35: c = '5'; return true;
                    case 0x36: c = '6'; return true;
                    case 0x37: c = '7'; return true;
                    case 0x38: c = '8'; return true;
                    case 0x39: c = '9'; return true;
                    case 0x3A: c = ':'; return true;
                    case 0x3B: c = ';'; return true;
                    case 0x3C: c = '<'; return true;
                    case 0x3D: c = '='; return true;
                    case 0x3E: c = '>'; return true;
                    case 0x3F: c = '?'; return true;
                    case 0x40: c = 'א'; return true;
                    case 0x41: c = 'ב'; return true;
                    case 0x42: c = 'ג'; return true;
                    case 0x43: c = 'ד'; return true;
                    case 0x44: c = 'ה'; return true;
                    case 0x45: c = 'ו'; return true;
                    case 0x46: c = 'ז'; return true;
                    case 0x47: c = 'ח'; return true;
                    case 0x48: c = 'ט'; return true;
                    case 0x49: c = 'י'; return true;
                    case 0x4A: c = 'ך'; return true;
                    case 0x4B: c = 'כ'; return true;
                    case 0x4C: c = 'ל'; return true;
                    case 0x4D: c = 'ם'; return true;
                    case 0x4E: c = 'מ'; return true;
                    case 0x4F: c = 'ן'; return true;
                    case 0x50: c = 'נ'; return true;
                    case 0x51: c = 'ס'; return true;
                    case 0x52: c = 'ע'; return true;
                    case 0x53: c = 'ף'; return true;
                    case 0x54: c = 'פ'; return true;
                    case 0x55: c = 'ץ'; return true;
                    case 0x56: c = 'צ'; return true;
                    case 0x57: c = 'ק'; return true;
                    case 0x58: c = 'ר'; return true;
                    case 0x59: c = 'ש'; return true;
                    case 0x5A: c = 'ת'; return true;
                }
            }

            return false;
        }

        private static bool TryMapNationalSingleShift(int nli, int value, out char c)
        {
            c = '\0';

            if (nli == 0x0D)
                return TryMapGsmDefaultExtension(value, out c);

            return false;
        }

        private static bool TryMapGsmDefaultExtension(int value, out char c)
        {
            switch (value)
            {
                case 0x0A: c = '\f'; return true;
                case 0x14: c = '^'; return true;
                case 0x28: c = '{'; return true;
                case 0x29: c = '}'; return true;
                case 0x2F: c = '\\'; return true;
                case 0x3C: c = '['; return true;
                case 0x3D: c = '~'; return true;
                case 0x3E: c = ']'; return true;
                case 0x40: c = '|'; return true;
                case 0x65: c = '€'; return true;
                default:
                    c = '\0';
                    return false;
            }
        }

        private static bool LooksReasonableText(string s)
        {
            if (string.IsNullOrEmpty(s))
                return true;

            int printable = 0;
            int weird = 0;

            foreach (char c in s)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    printable++;
                    continue;
                }

                if (!char.IsControl(c))
                    printable++;
                else
                    weird++;

                if (c == '\uFFFD')
                    weird++;
            }

            if (printable == 0)
                return false;

            return weird <= Math.Max(2, s.Length / 8);
        }

        private static char GsmToChar(int val)
        {
            switch (val)
            {
                case 0x00: return '@';
                case 0x01: return '£';
                case 0x02: return '$';
                case 0x03: return '¥';
                case 0x04: return 'è';
                case 0x05: return 'é';
                case 0x06: return 'ù';
                case 0x07: return 'ì';
                case 0x08: return 'ò';
                case 0x09: return 'Ç';
                case 0x0A: return '\n';
                case 0x0B: return 'Ø';
                case 0x0C: return 'ø';
                case 0x0D: return '\r';
                case 0x0E: return 'Å';
                case 0x0F: return 'å';
                case 0x10: return 'Δ';
                case 0x11: return '_';
                case 0x12: return 'Φ';
                case 0x13: return 'Γ';
                case 0x14: return 'Λ';
                case 0x15: return 'Ω';
                case 0x16: return 'Π';
                case 0x17: return 'Ψ';
                case 0x18: return 'Σ';
                case 0x19: return 'Θ';
                case 0x1A: return 'Ξ';
                case 0x1B: return ' ';
                case 0x1C: return 'Æ';
                case 0x1D: return 'æ';
                case 0x1E: return 'ß';
                case 0x1F: return 'É';
                case 0x20: return ' ';
                case 0x21: return '!';
                case 0x22: return '"';
                case 0x23: return '#';
                case 0x24: return '¤';
                case 0x25: return '%';
                case 0x26: return '&';
                case 0x27: return '\'';
                case 0x28: return '(';
                case 0x29: return ')';
                case 0x2A: return '*';
                case 0x2B: return '+';
                case 0x2C: return ',';
                case 0x2D: return '-';
                case 0x2E: return '.';
                case 0x2F: return '/';
                case 0x30: return '0';
                case 0x31: return '1';
                case 0x32: return '2';
                case 0x33: return '3';
                case 0x34: return '4';
                case 0x35: return '5';
                case 0x36: return '6';
                case 0x37: return '7';
                case 0x38: return '8';
                case 0x39: return '9';
                case 0x3A: return ':';
                case 0x3B: return ';';
                case 0x3C: return '<';
                case 0x3D: return '=';
                case 0x3E: return '>';
                case 0x3F: return '?';
                case 0x40: return '¡';
                case 0x41: return 'A';
                case 0x42: return 'B';
                case 0x43: return 'C';
                case 0x44: return 'D';
                case 0x45: return 'E';
                case 0x46: return 'F';
                case 0x47: return 'G';
                case 0x48: return 'H';
                case 0x49: return 'I';
                case 0x4A: return 'J';
                case 0x4B: return 'K';
                case 0x4C: return 'L';
                case 0x4D: return 'M';
                case 0x4E: return 'N';
                case 0x4F: return 'O';
                case 0x50: return 'P';
                case 0x51: return 'Q';
                case 0x52: return 'R';
                case 0x53: return 'S';
                case 0x54: return 'T';
                case 0x55: return 'U';
                case 0x56: return 'V';
                case 0x57: return 'W';
                case 0x58: return 'X';
                case 0x59: return 'Y';
                case 0x5A: return 'Z';
                case 0x5B: return 'Ä';
                case 0x5C: return 'Ö';
                case 0x5D: return 'Ñ';
                case 0x5E: return 'Ü';
                case 0x5F: return '§';
                case 0x60: return '¿';
                case 0x61: return 'a';
                case 0x62: return 'b';
                case 0x63: return 'c';
                case 0x64: return 'd';
                case 0x65: return 'e';
                case 0x66: return 'f';
                case 0x67: return 'g';
                case 0x68: return 'h';
                case 0x69: return 'i';
                case 0x6A: return 'j';
                case 0x6B: return 'k';
                case 0x6C: return 'l';
                case 0x6D: return 'm';
                case 0x6E: return 'n';
                case 0x6F: return 'o';
                case 0x70: return 'p';
                case 0x71: return 'q';
                case 0x72: return 'r';
                case 0x73: return 's';
                case 0x74: return 't';
                case 0x75: return 'u';
                case 0x76: return 'v';
                case 0x77: return 'w';
                case 0x78: return 'x';
                case 0x79: return 'y';
                case 0x7A: return 'z';
                case 0x7B: return 'ä';
                case 0x7C: return 'ö';
                case 0x7D: return 'ñ';
                case 0x7E: return 'ü';
                case 0x7F: return 'à';
                default: return ' ';
            }
        }
    }
}