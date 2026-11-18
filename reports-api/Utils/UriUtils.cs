namespace ReportsApi.Utils;

public static class UriUtils {
    public static string CombineUriPath(string uri, string path) {
        return new UriBuilder(uri) { Path = path }.Uri.ToString();
    }
}