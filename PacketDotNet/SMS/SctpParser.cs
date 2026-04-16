using System.Collections.Generic;

namespace PacketDotNet.SMS
{
    public class SctpChunk
    {
        public byte Type;
        public byte Flags;
        public byte[] Data;
    }

    public static class SctpParser
    {
        public static List<SctpChunk> Parse(byte[] data)
        {
            var chunks = new List<SctpChunk>();

            if (data == null || data.Length < 12)
                return chunks;

            int offset = 12; // SCTP common header

            while (offset + 4 <= data.Length)
            {
                byte type = data[offset];
                byte flags = data[offset + 1];
                int length = (data[offset + 2] << 8) | data[offset + 3];

                if (length < 4 || offset + length > data.Length)
                    break;

                byte[] chunkData = new byte[length - 4];
                System.Buffer.BlockCopy(data, offset + 4, chunkData, 0, chunkData.Length);

                chunks.Add(new SctpChunk
                {
                    Type = type,
                    Flags = flags,
                    Data = chunkData
                });

                offset += ((length + 3) / 4) * 4;
            }

            return chunks;
        }
    }
}