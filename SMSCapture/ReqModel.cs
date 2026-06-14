namespace SMSCapture
{
    public class ReqModel
    {
        public string MSISDN { get; set; } = "";
        public string IMSI { get; set; } = "";
        public int ServiceCode { get; set; }
        public string Action { get; set; } = "";
    }
}
