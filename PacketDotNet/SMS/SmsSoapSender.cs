using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        private readonly HttpClient _httpClientDialer;
        private readonly Channel<SmsSoapEvent> _channel;

        public SmsSoapSender(ILogger<SmsSoapSender> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("SmsSoap");
            _httpClientDialer = httpClientFactory.CreateClient("Dialer");

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

                    var obj = new
                    {
                        text = evt.MessageContent
                    };
                   

                    var content = new StringContent(JsonSerializer.Serialize(obj),Encoding.UTF8,"application/json");


                    using var response = await _httpClient.PostAsync("", content, stoppingToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "TTS send failed | EventType={EventType} | Status={StatusCode} | OTID={OTID} | DTID={DTID}",
                            evt.EventType, (int)response.StatusCode, evt.Otid, evt.Dtid);
                        continue;
                    }

                    var path = await response.Content.ReadAsStringAsync(stoppingToken);
                    _logger.LogInformation("TTS send OK | EventType={EventType} |Path={path} | OTID={OTID} | DTID={DTID}",evt.EventType, path, evt.Otid, evt.Dtid);

                    var uri = await CallDialerAsync(evt.Orig,evt.Dest,"801",path,stoppingToken);
                    var response1 = await _httpClientDialer.GetAsync(uri, stoppingToken);

                    if (!response1.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Dialer send failed | EventType={EventType} | Status={StatusCode} | OTID={OTID} | DTID={DTID}",
                            evt.EventType, (int)response.StatusCode, evt.Otid, evt.Dtid);
                        continue;
                    }

                    _logger.LogInformation(
                            "Dialer send OK | EventType={EventType} | Status={StatusCode} | OTID={OTID} | DTID={DTID}",
                            evt.EventType, (int)response.StatusCode, evt.Otid, evt.Dtid);


                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "SOAP send exception | EventType={EventType} | OTID={OTID} | DTID={DTID}",
                        evt.EventType, evt.Otid, evt.Dtid);
                }
            }
        }


        //


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


        private async Task<string> CallDialerAsync(string aNumber,string bNumber, string prefix, string wavFilePath, CancellationToken stoppingToken)
        {
            var parameters = new Dictionary<string, string>
            {
                ["Method"] = "LoadAndEnterScenario",
                ["ScenFileName"] = @"D:\IVRData\MakeCallDialer\Scenarios\MakeCallDialerRequest.phn",
                ["ScenName"] = "MakeCallDialerRequest",
                ["ANumber"] = aNumber,
                ["BNumber"] = bNumber,
                ["BNumberPrefix"] = prefix,
                ["WavFilePath"] = wavFilePath,
                ["NextAttempt"] = "05/28/2025 12:00",
                ["DepositVMStatus"] = "2",
                ["RetryString"] = "1;1;2;2;12",
                ["Template"] = "2",
                ["OtherInfo"] = "801"
            };

            string queryString = string.Join("&",
                parameters.Select(x =>
                    $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

            string url =
                "PhnCfger/sysasp/OuchIvrPartner.asp?" + queryString;

            //string response = await _httpClientDialer.GetStringAsync(
            //    url,
            //    stoppingToken);

            return url;
        }

    }
}
