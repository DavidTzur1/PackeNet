namespace SMSCapture
{
    public class ResModel
    {
        //{"ErrorCode":0,"TRID":"20260614052253089","ErrorDescription":"Success"}
        public int ErrorCode { get; set; } = 0;
        public string TRID { get; set; } = "";
        public string ErrorDescription { get; set; } = "Success";
    }
}
