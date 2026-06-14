using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Linq;

public interface ISubscriberRepository
{
    IDictionary<string, string> GetSubscribers();
}

public class SubscriberRepository : ISubscriberRepository
{
    private readonly string _connectionString;

    public SubscriberRepository(string connectionString) => _connectionString = connectionString;

    public IDictionary<string, string> GetSubscribers()
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        // adjust SQL to your schema
        var rows = conn.Query<(string imsi, string msisdn)>(
            "SELECT imsi, msisdn FROM subscribers WHERE is_active = 1");

        return rows.Where(r => !string.IsNullOrWhiteSpace(r.imsi))
                   .ToDictionary(r => r.imsi.Trim(), r => r.msisdn?.Trim() ?? "");
    }
}