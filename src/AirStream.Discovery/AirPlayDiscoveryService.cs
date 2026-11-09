
using System.Collections.Concurrent;
using AirStream.Core.Models;
using Haukcode.Mdns;

namespace AirStream.Discovery;

public sealed class AirPlayDiscoveryService : IDisposable
{
    private readonly MdnsBrowser _airPlayBrowser;
    private readonly MdnsBrowser _raopBrowser;

    private readonly ConcurrentDictionary<string, AirPlayDevice>
        _devices = new();

    public event Action<AirPlayDevice>? DeviceFound;
    public event Action<string>? DeviceLost;

    public AirPlayDiscoveryService()
    {
        _airPlayBrowser = new MdnsBrowser("_airplay._tcp");
        _raopBrowser = new MdnsBrowser("_raop._tcp");

        _airPlayBrowser.ServiceFound +=
            service => AddDevice(service, "_airplay._tcp");

        _raopBrowser.ServiceFound +=
            service => AddDevice(service, "_raop._tcp");

        _airPlayBrowser.ServiceLost +=
            service => RemoveDevice(service, "_airplay._tcp");

        _raopBrowser.ServiceLost +=
            service => RemoveDevice(service, "_raop._tcp");
    }

    public void Start()
    {
        _airPlayBrowser.Start();
        _raopBrowser.Start();
    }

    private void AddDevice(
        ServiceProfile service,
        string serviceType)
    {
        var key = $"{serviceType}|{service.FullInstanceName}";

        var device = new AirPlayDevice
        {
            Id = key,
            Name = service.InstanceName,
            Hostname = service.Hostname,
            Port = service.Port,
            ServiceType = serviceType
        };

        if (_devices.TryAdd(key, device))
        {
            DeviceFound?.Invoke(device);
        }
    }

    private void RemoveDevice(
        ServiceProfile service,
        string serviceType)
    {
        var key = $"{serviceType}|{service.FullInstanceName}";

        if (_devices.TryRemove(key, out _))
        {
            DeviceLost?.Invoke(key);
        }
    }

    public IReadOnlyCollection<AirPlayDevice> GetDevices()
    {
        return _devices.Values.ToArray();
    }

    public void Dispose()
    {
        _airPlayBrowser.Dispose();
        _raopBrowser.Dispose();
    }
}
