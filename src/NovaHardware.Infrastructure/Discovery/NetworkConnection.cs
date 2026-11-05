using System.Net.NetworkInformation;
using System.Net.Sockets;
using NovaHardware.Domain.Hardware;
using S = NovaHardware.Domain.Hardware.PropertySections;

namespace NovaHardware.Infrastructure.Discovery;

/// <summary>Connection details for a physical adapter from System.Net.NetworkInformation, matched by MAC address.</summary>
internal static class NetworkConnection
{
    public static IEnumerable<HardwareProperty> Describe(string? adapterName, string? mac)
    {
        const string src = "NetworkInterface (IP Helper)";
        NetworkInterface? nic;
        try
        {
            var normalized = mac?.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
            nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                (normalized is not null && string.Equals(n.GetPhysicalAddress().ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                || string.Equals(n.Description, adapterName, StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        if (nic is null) yield break;
        yield return new HardwareProperty("Interface status", nic.OperationalStatus.ToString(), S.Overview, src);
        yield return new HardwareProperty("Interface type", nic.NetworkInterfaceType.ToString(), S.Specifications, src);
        if (nic.Speed > 0) yield return new HardwareProperty("Negotiated speed", nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1e9:0.#} Gbps" : $"{nic.Speed / 1e6:0} Mbps", S.Performance, src);

        IPInterfaceProperties? ip = null;
        try { ip = nic.GetIPProperties(); } catch (NetworkInformationException) { }
        if (ip is null) yield break;
        var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}").ToList();
        var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.Address.ToString()).ToList();
        if (v4.Count > 0) yield return new HardwareProperty("IPv4 address", string.Join(", ", v4), S.Overview, src);
        if (v6.Count > 0) yield return new HardwareProperty("IPv6 address", string.Join(", ", v6), S.Advanced, src);
        var gw = ip.GatewayAddresses.Select(g => g.Address.ToString()).Where(g => g != "0.0.0.0").ToList();
        if (gw.Count > 0) yield return new HardwareProperty("Default gateway", string.Join(", ", gw), S.Advanced, src);
        var dns = ip.DnsAddresses.Select(d => d.ToString()).ToList();
        if (dns.Count > 0) yield return new HardwareProperty("DNS servers", string.Join(", ", dns), S.Advanced, src);
        if (!string.IsNullOrEmpty(ip.DnsSuffix)) yield return new HardwareProperty("DNS suffix", ip.DnsSuffix, S.Advanced, src);
        int? mtu = null;
        try { mtu = ip.GetIPv4Properties()?.Mtu; } catch (NetworkInformationException) { }
        if (mtu is { } m) yield return new HardwareProperty("MTU", m.ToString(System.Globalization.CultureInfo.InvariantCulture), S.Specifications, src);
    }
}
