namespace ReportsApi.Options;

public class S3Options {
    public string MinioUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public byte[] ObjectKeySecretKey { get; set; } = Array.Empty<byte>();
}