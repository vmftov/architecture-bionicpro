namespace ReportsApi.Repositories;

public class ReportRepository {
    private readonly S3ReportStore _s3;
    private readonly ClickhouseReportStore _clickhouse;

    public ReportRepository(S3ReportStore s3, ClickhouseReportStore clickhouse) {
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _clickhouse = clickhouse ?? throw new ArgumentNullException(nameof(clickhouse));
    }

    public async Task<string?> GetReportUriAsync(string ownerId, DateTimeOffset date) {
        // распределённая блокировка здесь будет излишней, т.к.
        // за одним и тем же отчётом будет обращаться только один
        // пользователь и он не сможет создать высокую конкурентную нагрузку

        var objectKey = _s3.CalcObjectKey(ownerId, date);
        var reportExists = await _s3.ReportExistsAsync(objectKey);
        if (reportExists) {
            return await _s3.GetReportDailyStableUriAsync(objectKey);
        }
        var report = await _clickhouse.GetReportAsync(ownerId, date);
        if (report.Prostheses.Count == 0) {
            return null;
        }
        await _s3.PutReportAsync(objectKey, report);
        return await _s3.GetReportDailyStableUriAsync(objectKey);
    }
}