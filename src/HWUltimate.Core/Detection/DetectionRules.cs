using System.Text.RegularExpressions;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Core.Detection;

/// <summary>What a provider can tell about one network adapter (all optional: providers fill in what they know).</summary>
public sealed record NetworkAdapterFacts(
    string Name,
    string? Description = null,
    bool? IsUp = null,
    bool? IsLoopbackOrTunnel = null,
    /// <summary>NCF_PHYSICAL from the adapter's driver registration; null when unknown.</summary>
    bool? IsPhysical = null,
    /// <summary>NCF_VIRTUAL or NCF_HIDDEN from the adapter's driver registration; null when unknown.</summary>
    bool? IsVirtualOrHidden = null);

/// <summary>Generic, vendor-independent rules used by hardware providers. No model names, no per-machine lists.</summary>
public static partial class DetectionRules
{
    // Software adapters Windows and common tools create. Used only when the driver registration does not say.
    [GeneratedRegex(@"hyper-v|vethernet|virtual|vmware|virtualbox|vbox|wsl|loopback|tunnel|teredo|isatap|6to4|wan miniport|\btap\b|tap-|tun\b|wireguard|wintun|openvpn|zerotier|tailscale|npcap|pseudo|kernel debug|bluetooth|ras async|microsoft wi-fi direct|wi-fi direct virtual|vpn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAdapterName();

    /// <summary>
    /// Classifies a network adapter for the default view: connected physical adapters are primary; software adapters are
    /// virtual; physical adapters that are not connected are inactive. Unknown facts never hide an adapter on their own.
    /// </summary>
    public static DeviceRelevance ClassifyNetworkAdapter(NetworkAdapterFacts f)
    {
        var text = $"{f.Name} {f.Description}";
        var looksVirtual = f.IsLoopbackOrTunnel == true || f.IsVirtualOrHidden == true
                           || (f.IsPhysical != true && VirtualAdapterName().IsMatch(text));
        if (looksVirtual && f.IsPhysical != true) return DeviceRelevance.Virtual;
        if (f.IsPhysical == false && f.IsUp != true) return DeviceRelevance.Virtual;
        return f.IsUp == false ? DeviceRelevance.Inactive : DeviceRelevance.Primary;
    }

    /// <summary>Counts describing one detection pass, used to judge whether it was complete.</summary>
    public sealed record DetectionSummary(
        int HardwareNodes,
        int Sensors,
        bool IsElevated,
        bool LowLevelDriverAvailable,
        bool HasMotherboard,
        bool HasMotherboardChip,
        bool HasCpu,
        bool CpuHasTemperature);

    /// <summary>
    /// True when a pass looks incomplete in a way a later pass can fix: with administrator rights and the low-level driver
    /// present, a desktop motherboard normally exposes a Super I/O / EC chip and the CPU a temperature. Laptops and boards
    /// without such chips also match, which only costs one extra (bounded) re-scan.
    /// </summary>
    public static bool LooksIncomplete(DetectionSummary s) =>
        s.HardwareNodes == 0
        || (s.IsElevated && s.LowLevelDriverAvailable && ((s.HasMotherboard && !s.HasMotherboardChip) || (s.HasCpu && !s.CpuHasTemperature)));

    /// <summary>A new pass replaces the current one only if it found more hardware, or the same hardware with more sensors.</summary>
    public static bool IsBetter(DetectionSummary candidate, DetectionSummary current) =>
        candidate.HardwareNodes > current.HardwareNodes
        || (candidate.HardwareNodes == current.HardwareNodes && candidate.Sensors > current.Sensors);
}
