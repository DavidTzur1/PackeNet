using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SMSCapture.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProvController : ControllerBase
    {

        private readonly ILogger<ProvController> _logger;
        private readonly IRepository _repository;

        private static long lastTimeStamp = long.Parse(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        public static string TRID
        {
            get
            {
                long original, newValue;
                do
                {

                    original = lastTimeStamp;
                    //long now = DateTime.UtcNow.Ticks;
                    long now = long.Parse(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                    newValue = Math.Max(now, original + 1);
                } while (Interlocked.CompareExchange(ref lastTimeStamp, newValue, original) != original);
                string trid = newValue.ToString() + "@" + Environment.MachineName;
                return trid;
            }
        }

        public ProvController(IRepository repository, ILogger<ProvController> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        [HttpPost("/api/Provision")]
        public async Task<IActionResult> Post(ReqModel req)
        {
            var res = new ResModel
            {
                ErrorCode = 0,
                TRID = TRID,
                ErrorDescription = "Success"
            };

            try
            {
                await _repository.AddOrDelProvisioning(req.MSISDN, req.ServiceCode.ToString(), req.Action, req.IMSI);
                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing provisioning request");
                res.ErrorCode = 999;
                res.ErrorDescription = "Failed to process provisioning request";

                return Ok(res);
            }
        }
    }
}
