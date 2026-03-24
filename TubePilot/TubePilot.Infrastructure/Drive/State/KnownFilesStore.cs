using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.Drive.State;

internal sealed class KnownFilesStore(ILogger<KnownFilesStore> logger) : IKnownFilesStore
{
    private const string LocalStateFileName = "known_files.json";
    private readonly HashSet<string> _processedIds = [];
    private bool _isLoaded;

    private void EnsureLoaded()
    {
        if (_isLoaded)
        {
            return;
        }
        
        _isLoaded = true;

        if (!File.Exists(LocalStateFileName))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(LocalStateFileName);
            
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }
            
            var ids = JsonSerializer.Deserialize<string[]>(json);
            if (ids != null && ids.Length != 0)
            {
                foreach (var id in ids)
                {
                    _processedIds.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load known files from {FileName}", LocalStateFileName);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_processedIds.OrderBy(x => x).ToArray());
            File.WriteAllText(LocalStateFileName, json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save known files to {FileName}", LocalStateFileName);
        }
    }

    public bool Contains(string fileId)
    {
        EnsureLoaded();
        return _processedIds.Contains(fileId);
    }

    public void Add(string fileId)
    {
        EnsureLoaded();
        if (_processedIds.Add(fileId))
        {
            Save();
        }
    }
}
