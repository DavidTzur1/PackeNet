using System;

namespace PacketDotNet.SMS
{
    public static class SmsHttpBridge
    {
        public static Action<SmsSoapEvent>? Enqueue { get; set; }

        public static void Publish(SmsSoapEvent evt)
        {
            try
            {
                Enqueue?.Invoke(evt);
            }
            catch
            {
            }
        }
    }
}
