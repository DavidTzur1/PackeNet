namespace PacketDotNet.SMS
{
    public static class TcapDetector
    {
        public static bool IsTcap(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;

            return data[0] == 0x61 || // Unidirectional
                   data[0] == 0x62 || // Begin
                   data[0] == 0x64 || // End
                   data[0] == 0x65 || // Continue
                   data[0] == 0x67;   // Abort
        }
    }
}