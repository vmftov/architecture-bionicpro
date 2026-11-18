using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using ReportsApi.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ReportsApi.Options;

namespace ReportsApi.Repositories;

public class S3ReportStore {
    private readonly IMinioClient _minio;
    private readonly IMinioClient _cdnPresigner;
    private readonly IOptions<S3Options> _options;

    public S3ReportStore(IMinioClient minio, [FromKeyedServices("cdn-presigner")] IMinioClient cdnPresigner, IOptions<S3Options> options) {
        _minio = minio ?? throw new ArgumentNullException(nameof(minio));
        _cdnPresigner = cdnPresigner ?? throw new ArgumentNullException(nameof(cdnPresigner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string CalcObjectKey(string ownerId, DateTimeOffset date) {
        // из-за того, что данные получаются через LDAP, ownerId может содержать username т.е. персональные данные типа john.doe.
        // чтобы оно не попало в CDN, берём от имени хэш. имя файла станет нечитаемым, зато не будет утечки персональных данных
        var ownerIdHash = Convert.ToHexString(HMACSHA256.HashData(_options.Value.ObjectKeySecretKey, Encoding.UTF8.GetBytes(ownerId)));

        // хэш имени и даты делает ссылку неподбираемой полным перебором и злоумышленник не сможет, зная id пользователя или его хэш, получить его отчёты
        var dateStr = $"{date.Year:0000}-{date.Month:00}-{date.Day:00}";
        var ownerIdAndDate = $"{ownerId}/{dateStr}";
        var ownerIdAndDateHash = Convert.ToHexString(HMACSHA256.HashData(_options.Value.ObjectKeySecretKey, Encoding.UTF8.GetBytes(ownerIdAndDate)));

        // отдельный уровень ownerIdHash в пути позволяет группировать по пользователю в UI
        // и поможет улучшить шардирование (если оно понадобится в будущем)
        return $"{ownerIdHash}/{ownerIdAndDateHash}/report-{dateStr}.json";
    }

    public async Task<bool> ReportExistsAsync(string objectKey) {
        try {
            var statArgs = new StatObjectArgs().WithBucket(_options.Value.Bucket).WithObject(objectKey);
            await _minio.StatObjectAsync(statArgs, CancellationToken.None);
            return true;
        } catch (ObjectNotFoundException) {
            return false;
        } catch (BucketNotFoundException) {
            return false;
        }
    }

    public async Task<string> GetReportDailyStableUriAsync(string objectKey) {
        var fileName = Path.GetFileName(objectKey);
        var reqParams = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["response-content-disposition"] = $"attachment; filename=\"{fileName}\"",
            ["response-content-type"] = "application/json; charset=utf-8"
        };

        var presignedArgs = new PresignedGetObjectArgs().WithBucket(_options.Value.Bucket)     //
                                                        .WithObject(objectKey)                 //
                                                        .WithHeaders(reqParams)                //
                                                        .WithRequestDate(DateTime.UtcNow.Date) //
                                                        .WithExpiry(86400 + 300);

        return await _cdnPresigner.PresignedGetObjectAsync(presignedArgs);
    }

    public async Task PutReportAsync(string objectKey, Report report) {
        var existsArgs = new BucketExistsArgs().WithBucket(_options.Value.Bucket);
        var found = await _minio.BucketExistsAsync(existsArgs, CancellationToken.None);
        if (!found) {
            var makeArgs = new MakeBucketArgs().WithBucket(_options.Value.Bucket).WithLocation(_options.Value.Region);
            await _minio.MakeBucketAsync(makeArgs, CancellationToken.None);
        }

        await using var reportStream = new MemoryStream(SerializeReport(report));

        var putArgs = new PutObjectArgs().WithBucket(_options.Value.Bucket)   //
                                         .WithObject(objectKey)               //
                                         .WithStreamData(reportStream)        //
                                         .WithObjectSize(reportStream.Length) //
                                         .WithContentType("application/json; charset=utf-8");

        await _minio.PutObjectAsync(putArgs, CancellationToken.None);
    }

    private static byte[] SerializeReport(Report report) {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица без перекодировки
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 4
        };
        return JsonSerializer.SerializeToUtf8Bytes(report, options);
    }
}