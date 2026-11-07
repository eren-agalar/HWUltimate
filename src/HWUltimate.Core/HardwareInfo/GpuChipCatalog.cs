namespace HWUltimate.Core.HardwareInfo;

/// <summary>Chip-level data published by the GPU vendor (die, process, transistor count).</summary>
public sealed record GpuChipSpec(string Chip, string Architecture, string Process, double DieSizeMm2, double TransistorsBillion, string? Note = null);

/// <summary>
/// Small, curated lookup from PCI vendor/device id to the GPU chip and its officially published silicon figures.
/// These values are <b>externally reported</b> (public vendor specifications), not read from the hardware, and are
/// labelled as such in the UI. Unknown device ids return <c>null</c> → "Not Available".
/// Board-level parameters (memory type, clocks) are deliberately not included because they vary by SKU and partner card.
/// </summary>
public static class GpuChipCatalog
{
    public const string SourceLabel = "HWUltimate chip catalog (public vendor specifications)";

    private static readonly GpuChipSpec AD102 = new("AD102", "Ada Lovelace", "TSMC 4N", 608.5, 76.3);
    private static readonly GpuChipSpec AD103 = new("AD103", "Ada Lovelace", "TSMC 4N", 378.6, 45.9);
    private static readonly GpuChipSpec AD104 = new("AD104", "Ada Lovelace", "TSMC 4N", 294.5, 35.8);
    private static readonly GpuChipSpec AD106 = new("AD106", "Ada Lovelace", "TSMC 4N", 188.0, 22.9);
    private static readonly GpuChipSpec AD107 = new("AD107", "Ada Lovelace", "TSMC 4N", 159.0, 18.9);
    private static readonly GpuChipSpec GA102 = new("GA102", "Ampere", "Samsung 8N", 628.4, 28.3);
    private static readonly GpuChipSpec GA104 = new("GA104", "Ampere", "Samsung 8N", 392.5, 17.4);
    private static readonly GpuChipSpec GA106 = new("GA106", "Ampere", "Samsung 8N", 276.0, 12.0);
    private static readonly GpuChipSpec TU102 = new("TU102", "Turing", "TSMC 12FFN", 754.0, 18.6);
    private static readonly GpuChipSpec TU104 = new("TU104", "Turing", "TSMC 12FFN", 545.0, 13.6);
    private static readonly GpuChipSpec TU106 = new("TU106", "Turing", "TSMC 12FFN", 445.0, 10.8);
    private static readonly GpuChipSpec GP102 = new("GP102", "Pascal", "TSMC 16FF", 471.0, 11.8);
    private static readonly GpuChipSpec GP104 = new("GP104", "Pascal", "TSMC 16FF", 314.0, 7.2);
    private static readonly GpuChipSpec GP106 = new("GP106", "Pascal", "TSMC 16FF", 200.0, 4.4);
    private static readonly GpuChipSpec Navi31 = new("Navi 31", "RDNA 3", "TSMC N5 (GCD) + N6 (MCD)", 529.0, 57.7, "Chiplet design: 304 mm² GCD + 6 × 37.5 mm² MCD");
    private static readonly GpuChipSpec Navi32 = new("Navi 32", "RDNA 3", "TSMC N5 (GCD) + N6 (MCD)", 346.0, 28.1, "Chiplet design: 200 mm² GCD + 4 MCD");
    private static readonly GpuChipSpec Navi33 = new("Navi 33", "RDNA 3", "TSMC N6", 204.0, 13.3);
    private static readonly GpuChipSpec Navi21 = new("Navi 21", "RDNA 2", "TSMC N7", 519.8, 26.8);
    private static readonly GpuChipSpec Navi22 = new("Navi 22", "RDNA 2", "TSMC N7", 335.0, 17.2);
    private static readonly GpuChipSpec Navi23 = new("Navi 23", "RDNA 2", "TSMC N7", 237.0, 11.06);
    private static readonly GpuChipSpec Navi10 = new("Navi 10", "RDNA", "TSMC N7", 251.0, 10.3);

    private static readonly Dictionary<(ushort, ushort), GpuChipSpec> Map = new()
    {
        [(0x10DE, 0x2684)] = AD102,
        [(0x10DE, 0x2704)] = AD103,
        [(0x10DE, 0x2782)] = AD104,
        [(0x10DE, 0x2786)] = AD104,
        [(0x10DE, 0x2803)] = AD106,
        [(0x10DE, 0x2882)] = AD107,
        [(0x10DE, 0x2204)] = GA102,
        [(0x10DE, 0x2206)] = GA102,
        [(0x10DE, 0x2208)] = GA102,
        [(0x10DE, 0x2216)] = GA102,
        [(0x10DE, 0x2482)] = GA104,
        [(0x10DE, 0x2484)] = GA104,
        [(0x10DE, 0x2488)] = GA104,
        [(0x10DE, 0x2503)] = GA106,
        [(0x10DE, 0x2504)] = GA106,
        [(0x10DE, 0x1E07)] = TU102,
        [(0x10DE, 0x1E84)] = TU104,
        [(0x10DE, 0x1F02)] = TU106,
        [(0x10DE, 0x1F07)] = TU106,
        [(0x10DE, 0x1B06)] = GP102,
        [(0x10DE, 0x1B80)] = GP104,
        [(0x10DE, 0x1C03)] = GP106,
        [(0x1002, 0x744C)] = Navi31,
        [(0x1002, 0x747E)] = Navi32,
        [(0x1002, 0x7480)] = Navi33,
        [(0x1002, 0x73BF)] = Navi21,
        [(0x1002, 0x73DF)] = Navi22,
        [(0x1002, 0x73FF)] = Navi23,
        [(0x1002, 0x731F)] = Navi10,
    };

    public static GpuChipSpec? Find(ushort vendorId, ushort deviceId) => Map.GetValueOrDefault((vendorId, deviceId));

    public static int Count => Map.Count;
}
