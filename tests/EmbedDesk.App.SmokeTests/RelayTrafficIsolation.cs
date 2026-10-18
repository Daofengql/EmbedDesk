using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.SmokeTests;

/// <summary>Keeps UI and packaging checks from registering devices on catalog relays.</summary>
internal sealed class RelayTrafficIsolation : IAsyncDisposable
{
    private readonly MainWindowViewModel _model;
    private readonly Socket _closedProxy = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly string[] _initialRelayUrls;

    internal RelayTrafficIsolation(MainWindowViewModel model)
    {
        _model = model;
        _initialRelayUrls = model.Connections.Preferences.RelayServers.Select(profile => profile.Url).ToArray();
        // Reserve an unused local port without listening; neither HTTP nor WebSocket can get through.
        _closedProxy.ExclusiveAddressUse = true;
        _closedProxy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)_closedProxy.LocalEndPoint!;
        model.AppSettings.Update(settings => settings.ConnectionPreferences = settings.ConnectionPreferences with
        {
            ProxyMode = "Custom", ProxyUrl = "http://127.0.0.1:" + endpoint.Port
        });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _model.Connections.StopAsync();
            foreach (var url in _initialRelayUrls.Concat(_model.Connections.Preferences.RelayServers.Select(profile => profile.Url)).Distinct())
            {
                var identity = "identity-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(url).AbsoluteUri.TrimEnd('/'))))[..24];
                _model.Connections.SaveSecret(identity, string.Empty);
            }
        }
        finally { _closedProxy.Dispose(); }
    }
}
