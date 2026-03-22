namespace TubePilot.Infrastructure.Drive.State;

internal interface IKnownFilesStore
{
    bool Contains(string fileId);
    void Add(string fileId);
}