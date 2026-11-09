
namespace AirStream.Core.Models;

public sealed class AirPlayDevice
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Hostname { get; init; } = string.Empty;

    public int Port { get; init; }

    public string ServiceType { get; init; } = string.Empty;

    public string DisplayName =>
        $"{Name} ({ServiceType})";

    public bool IsAvailable { get; init; } = true;
}
