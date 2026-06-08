namespace SMSCapture
{
    public class CaptureOptions
    {
        public int DeviceIndex { get; set; } = 1;

        public bool UseFileMode { get; set; }

        public string CaptureFile { get; set; } = "";

        public string ImsiFilePath { get; set; } = "";

        public int QueueCapacity { get; set; } = 50000;

        public int LiveReadTimeoutMs { get; set; } = 1000;

        public string BpfFilter { get; set; } = "";
    }
}