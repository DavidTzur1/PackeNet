using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;

namespace SMSCapture
{

    public interface IRepository
    {
        public Task AddOrDelProvisioning(string msisdn, string serviceCode, string action, string imsi);
        public Task<IEnumerable<SubscriberModel>> GetSubscribers();
    }
    public class Repository : IRepository
    {
        private readonly DapperContext _context;

        public Repository(DapperContext context)
        {
            _context = context;

        }

        public async Task AddOrDelProvisioning(string msisdn, string serviceCode, string action, string imsi)
        {
            var procedureName = "AddOrDelProvisioning";

            var parameters = new DynamicParameters();
            parameters.Add("MSISDN", msisdn, DbType.String);
            parameters.Add("ServiceCode", serviceCode, DbType.String);
            parameters.Add("Action", action, DbType.String);
            parameters.Add("IMSI", imsi, DbType.String);

            using (var connection = _context.CreateConnection())
            {
                await connection.ExecuteAsync(procedureName, parameters, commandType: CommandType.StoredProcedure);
            }
        }

        public async Task<IEnumerable<SubscriberModel>> GetSubscribers()
        {
            var procedureName = "GetSubscribers"; 
            using (var connection = _context.CreateConnection())
            {
                var myList = await connection.QueryAsync<SubscriberModel>(procedureName, null, commandType: CommandType.StoredProcedure);
                return myList.ToList();
            }
        }

    }
}

