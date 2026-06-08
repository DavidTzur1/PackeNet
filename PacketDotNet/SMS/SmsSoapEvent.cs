using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PacketDotNet.SMS
{
    public class SmsSoapEvent
    {
        public string EventType { get; set; } = "";   // MAP-MT / MAP-ACK-MT

        public string Orig { get; set; } = "";
        public string Dest { get; set; } = "";
        public string OrigSMSCGT { get; set; } = "";
        public string TimeStamp { get; set; } = "";
        public string Dcs { get; set; } = "";
        public string Udh { get; set; } = "";
        public string MessageContent { get; set; } = "";

        // Optional debug fields
        public string Otid { get; set; } = "";
        public string Dtid { get; set; } = "";
        public string Op { get; set; } = "";
        public string Imsi { get; set; } = "";
    }
}
