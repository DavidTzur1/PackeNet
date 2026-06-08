using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PacketDotNet.SMS
{
    public sealed class SmsSoapSender : BackgroundService
    {
        private readonly ILogger<SmsSoapSender> _logger;
        private readonly HttpClient _httpClient;
        private readonly Channel<SmsSoapEvent> _channel;

        public SmsSoapSender(ILogger<SmsSoapSender> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("SmsSoap");

            _channel = Channel.CreateBounded<SmsSoapEvent>(new BoundedChannelOptions(20000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            SmsHttpBridge.Enqueue = evt =>
            {
                _channel.Writer.TryWrite(evt);
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    string soapString = ConstructSoapRequest( evt.Orig,evt.Dest, evt.OrigSMSCGT,evt.TimeStamp,evt.Dcs,evt.Udh,evt.MessageContent);

                    using var content = new StringContent(soapString, Encoding.UTF8, "text/xml");
                    using var response = await _httpClient.PostAsync("", content, stoppingToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "SOAP send failed | EventType={EventType} | Status={StatusCode} | OTID={OTID} | DTID={DTID}",
                            evt.EventType, (int)response.StatusCode, evt.Otid, evt.Dtid);
                        continue;
                    }

                    await using var soapResponse = await response.Content.ReadAsStreamAsync(stoppingToken);
                    var soap = XElement.Load(soapResponse);

                    XNamespace ns = "TTSMedClient";

                    var resultNode = soap.Descendants(ns + "SendMsgResult").FirstOrDefault();
                    var resultId = resultNode?.Element(ns + "ResultId")?.Value ?? "-";
                    var resultDesc = resultNode?.Element(ns + "ResultDesc")?.Value ?? "-";

                    _logger.LogInformation(
                        "SOAP send ok | EventType={EventType} | ResultId={ResultId} | ResultDesc={ResultDesc} | OTID={OTID} | DTID={DTID}",
                        evt.EventType, resultId, resultDesc, evt.Otid, evt.Dtid);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "SOAP send exception | EventType={EventType} | OTID={OTID} | DTID={DTID}",
                        evt.EventType, evt.Otid, evt.Dtid);
                }
            }
        }





        private static string ConstructSoapRequest(string orig, string dest, string origSMSCGT, string timeStamp, string dcs, string udh, string content)
        {
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
            XNamespace ns = "TTSMedClient";

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(soap + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                    new XAttribute(XNamespace.Xmlns + "xsd", xsd),
                    new XAttribute(XNamespace.Xmlns + "soap", soap),
                    new XElement(soap + "Body",
                        new XElement(ns + "SendMsg",
                            new XElement(ns + "Orig", orig ?? string.Empty),
                            new XElement(ns + "Dest", dest ?? string.Empty),
                            new XElement(ns + "OrigSMSCGT", origSMSCGT ?? string.Empty),
                            new XElement(ns + "TimeStamp", timeStamp ?? string.Empty),
                            new XElement(ns + "DCS", dcs ?? string.Empty),
                            new XElement(ns + "UDH", udh ?? string.Empty),
                            new XElement(ns + "MessageContent", content ?? string.Empty)
                        )
                    )
                )
            );

            return doc.ToString(SaveOptions.DisableFormatting);
        }

    }
}
