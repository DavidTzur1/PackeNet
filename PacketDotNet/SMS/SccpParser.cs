using System;

namespace PacketDotNet.SMS
{
    public static class SccpParser
    {
        public static byte[] Extract(byte[] data)
        {
            if (data == null || data.Length < 5)
                return null;

            try
            {
                byte messageType = data[0];

                switch (messageType)
                {
                    case 0x09: // UDT
                        return ExtractUdt(data);

                    case 0x11: // XUDT
                        return ExtractXudt(data);

                    default:
                        //Console.WriteLine($"Unsupported SCCP type: {messageType:X2}");
                        return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("SCCP error: " + ex.Message);
                return null;
            }
        }

        private static byte[] ExtractUdt(byte[] data)
        {
            if (data.Length < 5)
                return null;

            byte protocolClass = data[1];
            byte pointerToCalled = data[2];
            byte pointerToCalling = data[3];
            byte pointerToData = data[4];

            // Pointer is relative to its own pointer octet position
            int dataLenIndex = 4 + pointerToData;

            if (dataLenIndex < 0 || dataLenIndex >= data.Length)
                return null;

            int tcapLen = data[dataLenIndex];
            int tcapStart = dataLenIndex + 1;

            if (tcapStart + tcapLen > data.Length)
                return null;

            byte[] tcap = new byte[tcapLen];
            Buffer.BlockCopy(data, tcapStart, tcap, 0, tcapLen);
            return tcap;
        }

        private static byte[] ExtractXudt(byte[] data)
        {
            if (data.Length < 7)
                return null;

            byte protocolClass = data[1];
            byte hopCounter = data[2];
            byte pointerToCalled = data[3];
            byte pointerToCalling = data[4];
            byte pointerToData = data[5];
            byte pointerToOptional = data[6];

            int dataLenIndex = 5 + pointerToData;

            if (dataLenIndex < 0 || dataLenIndex >= data.Length)
                return null;

            int tcapLen = data[dataLenIndex];
            int tcapStart = dataLenIndex + 1;

            if (tcapStart + tcapLen > data.Length)
                return null;

            byte[] tcap = new byte[tcapLen];
            Buffer.BlockCopy(data, tcapStart, tcap, 0, tcapLen);
            return tcap;
        }
    }
}