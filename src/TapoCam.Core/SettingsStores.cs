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
    void Set(Guid cameraId, string secret);
    void Delete(Guid cameraId);
}
