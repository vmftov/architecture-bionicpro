namespace ReportsApi.Utils;

public static class EnvironmentUtils {
    public static string GetEnvironmentVariableOrThrow(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Переменная окружения '{name}' не задана.");
        }
        return value;
    }

    public static int GetIntEnvironmentVariableOrThrow(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Переменная окружения '{name}' не задана.");
        }
        if (!int.TryParse(value, out var number)) {
            throw new InvalidOperationException($"Значением переменной окружения '{name}' должно быть число.");
        }
        return number;
    }
}