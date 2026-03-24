namespace TubePilot.Core.Utils;

public static class FileNameSanitizer
{
    /// <summary>
    /// Очищає ім'я файлу від проблемних символів (системні + FFmpeg-специфічні).
    /// </summary>
    public static string Sanitize(string filename)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(ch.ToString(), "");
        }
        
        // FFmpeg-специфічні символи, які ламають CLI-команди
        var ffmpegUnsafe = new[] { '"', '\'', '`', '(', ')', '[', ']', '{', '}' };
        foreach (var ch in ffmpegUnsafe)
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
