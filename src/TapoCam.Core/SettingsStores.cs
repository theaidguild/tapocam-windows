using TapoCam.Core.Models;

namespace TapoCam.Core;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface ISecretStore
{
    string? Get(Guid cameraId);
    void Set(Guid cameraId, string password);
    void Delete(Guid cameraId);
}
