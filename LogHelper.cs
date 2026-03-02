namespace TS4;

/// <summary>Убирает лишнее экранирование кавычек в строках для логов (\" → ").</summary>
static class LogHelper
{
    public static string Sanitize(string? s) => (s ?? "").Replace("\\\"", "\"");
}
