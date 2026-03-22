namespace TubePilot.Core.Utils;

public static class FileNameSanitizer
{
    /// <summary>
    /// Очищає ім'я файлу від проблемних символів (пробіли, лапки, дужки).
    /// Це необхідно, оскільки такі символи можуть зламати виконання команд у FFmpeg під час нарізання відео.
    /// </summary>
    public static string Sanitize(string filename)
    {
        var invalidChars = new[] { '"', '\'', '`', '(', ')', '[', ']', '{', '}' };
        foreach (var ch in invalidChars)
        {
            filename = filename.Replace(ch.ToString(), "");
        }
        filename = filename.Replace(" ", "_");
        while (filename.Contains("__"))
        {
            filename = filename.Replace("__", "_");
        }
        return filename;
    }
}