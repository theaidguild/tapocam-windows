namespace TapoCam.Core.Models;

public sealed class CameraConnections
{
    private readonly Dictionary<Guid, bool> _overrides = [];

    public bool ConnectByDefault { get; private set; }

    public bool WantsConnection(Guid id) =>
        _overrides.TryGetValue(id, out var value) ? value : ConnectByDefault;

    public bool AllowsSchedule(Guid id) =>
        !_overrides.TryGetValue(id, out var value) || value;

    public void Set(bool connected, Guid id) => _overrides[id] = connected;

    public void SetAll(bool connected)
    {
        ConnectByDefault = connected;
        _overrides.Clear();
    }

    public void Remove(Guid id) => _overrides.Remove(id);
}
