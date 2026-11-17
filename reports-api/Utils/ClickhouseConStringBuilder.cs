namespace ReportsApi.Utils;

public class ClickhouseConStringBuilder {
    public ClickhouseConStringBuilder(string clHost, string clPort, string clUser, string clPassword, string clDb) {
        ConnectionString = $"Host={clHost};Protocol=http;Port={clPort};Username={clUser};Password={clPassword};Database={clDb}";
    }

    public string ConnectionString { get; }
}