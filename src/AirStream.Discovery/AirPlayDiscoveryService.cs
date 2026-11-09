
using System.Collections.Concurrent;
using AirStream.Core.Models;
using Haukcode.Mdns;

namespace AirStream.Discovery;

public sealed class AirPlayDiscoveryService : IDisposable
{
    private readonly MdnsBrowser _airPlayBrowser;
    private readonly MdnsBrowser _raopBrowser;

    private readonly ConcurrentDictionary<string, ServiceEntry>
        _services = new();

    private readonly object _sync = new();

    public event Action<AirPlayDevice>? DeviceFound;
    public event Action<AirPlayDevice>? DeviceUpdated;
    public event Action<string>? DeviceLost;

    public AirPlayDiscoveryService()
    {
        _airPlayBrowser = new MdnsBrowser("_airplay._tcp");
        _raopBrowser = new MdnsBrowser("_raop._tcp");

        _airPlayBrowser.ServiceFound +=
            service => AddService(service, "_airplay._tcp");

        _raopBrowser.ServiceFound +=
            service => AddService(service, "_raop._tcp");

        _airPlayBrowser.ServiceLost +=
            service => RemoveService(service, "_airplay._tcp");

        _raopBrowser.ServiceLost +=
            service => RemoveService(service, "_raop._tcp");
    }

    public void Start()
    {
        _airPlayBrowser.Start();
        _raopBrowser.Start();
    }

    private void AddService(
        ServiceProfile service,
        string serviceType)
    {
        var serviceKey =
            $"{serviceType}|{service.FullInstanceName}";

        var hostname = service.Hostname.TrimEnd('.');

        var name = GetFriendlyName(service.InstanceName, serviceType);

        var deviceId = GetDeviceId(hostname, name);

        var entry = new ServiceEntry(
            serviceKey,
            deviceId,
            name,
            hostname,
            service.Port,
            serviceType);

        lock (_sync)
        {
            var alreadyExists = _services.ContainsKey(serviceKey);

            _services[serviceKey] = entry;

            var device = BuildDevice(deviceId);

            if (device == null)
                return;

            if (alreadyExists)
                DeviceUpdated?.Invoke(device);
            else if (_services.Values.Count(s => s.DeviceId == deviceId) == 1)
                DeviceFound?.Invoke(device);
            else
                DeviceUpdated?.Invoke(device);
        }
    }

    private void RemoveService(
        ServiceProfile service,
        string serviceType)
    {
        var serviceKey =
            $"{serviceType}|{service.FullInstanceName}";

        lock (_sync)
        {
            if (!_services.TryRemove(serviceKey, out var removed))
                return;

            var remaining = BuildDevice(removed.DeviceId);

            if (remaining == null)
            {
                DeviceLost?.Invoke(removed.DeviceId);
            }
            else
            {
                DeviceUpdated?.Invoke(remaining);
            }
        }
    }

    private AirPlayDevice? BuildDevice(string deviceId)
    {
        var entries = _services.Values
            .Where(s => s.DeviceId == deviceId)
            .ToArray();

        if (entries.Length == 0)
            return null;

        var airPlay = entries.FirstOrDefault(
            s => s.ServiceType == "_airplay._tcp");

        var raop = entries.FirstOrDefault(
            s => s.ServiceType == "_raop._tcp");

        var preferred = airPlay ?? raop!;

        return new AirPlayDevice
        {
            Id = deviceId,
            Name = preferred.Name,
            Hostname = preferred.Hostname,
            AirPlayPort = airPlay?.Port,
            RaopPort = raop?.Port,
            HasAirPlay = airPlay != null,
            HasRaop = raop != null
        };
    }

    private static string GetFriendlyName(
        string instanceName,
        string serviceType)
    {
        var name = instanceName;

        // RAOP names often start with a hardware identifier:
        // 1EA0FEDF6486@Bedroom

        if (serviceType == "_raop._tcp")
        {
            var separator = name.IndexOf('@');

            if (separator >= 0 && separator < name.Length - 1)
            {
                name = name[(separator + 1)..];
            }
        }

        return name.Trim();
    }

    private static string GetDeviceId(
        string hostname,
        string name)
    {
        return name.Trim().ToLowerInvariant();
    }

    public IReadOnlyCollection<AirPlayDevice> GetDevices()
    {
        lock (_sync)
        {
            return _services.Values
                .Select(s => s.DeviceId)
                .Distinct()
                .Select(BuildDevice)
                .Where(d => d != null)
                .Cast<AirPlayDevice>()
                .ToArray();
        }
    }

    public void Dispose()
    {
        _airPlayBrowser.Dispose();
        _raopBrowser.Dispose();
    }

    private sealed record ServiceEntry(
        string ServiceKey,
        string DeviceId,
        string Name,
        string Hostname,
        int Port,
        string ServiceType);
}
