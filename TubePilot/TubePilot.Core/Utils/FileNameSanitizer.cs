using System.Text;

namespace TubePilot.Core.Utils;

public static class FileNameSanitizer
{
    private static readonly HashSet<char> UnsafeChars = BuildUnsafeChars();

    /// <summary>
    /// Очищає ім'я файлу від проблемних символів (системні + FFmpeg-специфічні).
    /// </summary>
    public static string Sanitize(string filename)
    {
        var sb = new StringBuilder(filename.Length);
        var lastWasUnderscore = false;

        foreach (var ch in filename)
        {
            if (UnsafeChars.Contains(ch))
            {
                continue;
            }

            if (ch == ' ')
            {
                if (!lastWasUnderscore)
                {
                    sb.Append('_');
                    lastWasUnderscore = true;
                }
                continue;
            }

            if (ch == '_')
            {
                if (!lastWasUnderscore)
                {
                    sb.Append('_');
                    lastWasUnderscore = true;
                }
                continue;
            }

            sb.Append(ch);
            lastWasUnderscore = false;
        }

        return sb.ToString();
    }

    private static HashSet<char> BuildUnsafeChars()
    {
        var set = new HashSet<char>(Path.GetInvalidFileNameChars());
        // FFmpeg-специфічні символи, які ламають CLI-команди
        foreach (var ch in new[] { '"', '\'', '`', '(', ')', '[', ']', '{', '}' })
        {
            set.Add(ch);
        }
        return set;
    }
}
