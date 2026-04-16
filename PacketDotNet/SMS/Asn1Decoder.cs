using System;
using System.Collections.Generic;

namespace PacketDotNet.SMS
{
    public class Asn1Object
    {
        public byte Tag;
        public int Length;
        public byte[] Value;
        public List<Asn1Object> Children = new();

        public bool Constructed => (Tag & 0x20) != 0;
    }

    public static class Asn1Decoder
    {
        public static Asn1Object Decode(byte[] data, ref int offset)
        {
            if (data == null || offset >= data.Length)
                return null;

            var obj = new Asn1Object();

            obj.Tag = data[offset++];

            if (offset >= data.Length)
                return null;

            int length = data[offset++];

            if ((length & 0x80) != 0)
            {
                int count = length & 0x7F;
                length = 0;

                if (offset + count > data.Length)
                    return null;

                for (int i = 0; i < count; i++)
                    length = (length << 8) | data[offset++];
            }

            if (length < 0 || offset + length > data.Length)
                return null;

            obj.Length = length;
            obj.Value = new byte[length];
            Buffer.BlockCopy(data, offset, obj.Value, 0, length);

            if (obj.Constructed)
            {
                int inner = 0;
                while (inner < obj.Value.Length)
                {
                    var child = Decode(obj.Value, ref inner);
                    if (child == null)
                        break;

                    obj.Children.Add(child);
                }
            }

            offset += length;
            return obj;
        }
    }
}