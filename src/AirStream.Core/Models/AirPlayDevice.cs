
namespace AirStream.Core.Models;

public sealed class AirPlayDevice
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Hostname { get; init; } = string.Empty;

    public int? AirPlayPort { get; init; }

    public int? RaopPort { get; init; }

    public bool HasAirPlay { get; init; }

    public bool HasRaop { get; init; }

    public bool IsAvailable { get; init; } = true;

    public string DisplayName => Name;

    public string ConnectionInfo =>
        HasAirPlay && HasRaop
            ? "AirPlay and RAOP available"
            : HasAirPlay
                ? "AirPlay available"
                : "RAOP available";
}
