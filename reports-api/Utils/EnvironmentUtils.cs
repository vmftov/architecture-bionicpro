namespace ReportsApi.Utils;

public static class EnvironmentUtils {
    public static string GetEnvironmentVariableOrThrow(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Переменная окружения '{name}' не задана.");
        }
        return value;
    }
}