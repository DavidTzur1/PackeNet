using System;

namespace PacketDotNet.SMS
{
    public class M3uaMessage
    {
        public byte[] ProtocolDataRaw;
        public byte[] UserProtocolData;

        public static M3uaMessage Parse(byte[] data)
        {
            if (data == null || data.Length < 8)
                return null;

            try
            {
                int version = data[0];
                int messageClass = data[2];
                int messageType = data[3];

                // Basic M3UA sanity
                if (version != 1)
                    return null;

                int offset = 8; // skip M3UA common header

                while (offset + 4 <= data.Length)
                {
                    ushort tag = (ushort)((data[offset] << 8) | data[offset + 1]);
                    ushort length = (ushort)((data[offset + 2] << 8) | data[offset + 3]);

                    if (length < 4 || offset + length > data.Length)
                        break;

                    if (tag == 0x0210) // Protocol Data
                    {
                        int valueLen = length - 4;
                        byte[] protocolData = new byte[valueLen];
                        Buffer.BlockCopy(data, offset + 4, protocolData, 0, valueLen);

                        if (protocolData.Length < 12)
                            return null;

                        byte[] userProtocolData = new byte[protocolData.Length - 12];
                        Buffer.BlockCopy(protocolData, 12, userProtocolData, 0, userProtocolData.Length);

                        return new M3uaMessage
                        {
                            ProtocolDataRaw = protocolData,
                            UserProtocolData = userProtocolData
                        };
                    }

                    offset += ((length + 3) / 4) * 4; // 4-byte aligned
                }
            }
            catch
            {
            }

            return null;
        }
    }
}