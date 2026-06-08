using Microsoft.Extensions.Logging;

namespace PacketDotNet.SMS
{
    public static class SmsLog
    {
        public static ILogger? Logger { get; set; }

        public static void Info(string message)
        {
            if (Logger != null)
                Logger.LogInformation(message);
            else
                System.Console.WriteLine(message);
        }

        public static void Warn(string message)
        {
            if (Logger != null)
                Logger.LogWarning(message);
            else
                System.Console.WriteLine(message);
        }

        public static void Error(string message)
        {
            if (Logger != null)
                Logger.LogError(message);
            else
                System.Console.WriteLine(message);
        }
    }
}