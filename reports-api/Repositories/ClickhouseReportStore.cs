using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;
using ReportsApi.Models;
using ReportsApi.Utils;
using System.Data;

namespace ReportsApi.Repositories;

public class ClickhouseReportStore {
    private readonly ClickhouseConStringBuilder _conStringBuilder;

    public ClickhouseReportStore(ClickhouseConStringBuilder conStringBuilder) {
        _conStringBuilder = conStringBuilder ?? throw new ArgumentNullException(nameof(conStringBuilder));
    }

    public async Task<Report> GetReportAsync(string ownerId, DateTimeOffset date) {
        await using var connection = new ClickHouseConnection(_conStringBuilder.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT prosthesis_id, prosthesis_type, prosthesis_description, daily_avg_response_time_ms, daily_error_count, daily_bending_count FROM user_reports_4 FINAL WHERE owner_id = {owner_id:String} AND report_date = {report_date:Date} ORDER BY report_date";
        command.AddParameter("report_date", date);
        command.AddParameter("owner_id", ownerId);

        var report = new Report {
            Date = date
        };

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            report.Prostheses.Add(new ReportProsthesis {
                Id = (int) reader.GetFieldValue<uint>("prosthesis_id"),
                Type = reader.GetString("prosthesis_type"),
                Description = reader.GetString("prosthesis_description"),
                Stats = {
                    new ReportProsthesisStat {
                        Code = "daily_avg_response_time_ms",
                        Name = "Среднее время отклика за сутки, мс",
                        Value = reader.GetFieldValue<float>("daily_avg_response_time_ms").ToString("F3")
                    },
                    new ReportProsthesisStat {
                        Code = "daily_bending_count",
                        Name = "Количество операций сгибания/разгибания за сутки",
                        Value = reader.GetFieldValue<uint>("daily_bending_count").ToString()
                    },
                    new ReportProsthesisStat {
                        Code = "daily_error_count",
                        Name = "Количество ошибок в работе за сутки",
                        Value = reader.GetFieldValue<uint>("daily_error_count").ToString()
                    }
                }
            });
        }

        return report;
    }
}