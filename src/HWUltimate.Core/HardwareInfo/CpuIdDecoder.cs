namespace HWUltimate.Core.HardwareInfo;

public readonly record struct CpuIdRegisters(uint Eax, uint Ebx, uint Ecx, uint Edx);

/// <summary>Executes CPUID(leaf, subleaf). Abstracted so decoding can be unit-tested with recorded register dumps.</summary>
public delegate CpuIdRegisters CpuIdFunction(uint leaf, uint subleaf);

public enum CacheType { Data = 1, Instruction = 2, Unified = 3 }

/// <summary>One cache descriptor as reported by CPUID (size is per instance).</summary>
public sealed record CpuCacheDescriptor(int Level, CacheType Type, long SizeBytes, int Ways, int LineSize, int MaxThreadsSharing)
{
    public string Label => Type switch
    {
        CacheType.Data => $"L{Level} Data",
        CacheType.Instruction => $"L{Level} Instruction",
        _ => $"L{Level}",
    };
}

public sealed record CpuIdInfo
{
    public required string Vendor { get; init; }
    public string? Brand { get; init; }
    public int Family { get; init; }
    public int Model { get; init; }
    public int Stepping { get; init; }
    public uint Signature { get; init; }
    public uint MaxLeaf { get; init; }
    public uint MaxExtendedLeaf { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<CpuCacheDescriptor> Caches { get; init; } = [];
    public bool IsHybrid { get; init; }
    public bool HardwareVirtualization { get; init; }
    public string? VirtualizationTechnology { get; init; }
    public bool HypervisorPresent { get; init; }
    public string? HypervisorVendor { get; init; }
    public int? BaseFrequencyMhz { get; init; }
    public int? MaxFrequencyMhz { get; init; }
    public int? BusFrequencyMhz { get; init; }
    public string? Microarchitecture { get; init; }
    public string? Codename { get; init; }
    public bool Is64Bit { get; init; }

    public bool Has(string feature) => Features.Contains(feature, StringComparer.Ordinal);
}

/// <summary>Decodes raw CPUID leaves into identification, feature flags and cache descriptors.</summary>
public static class CpuIdDecoder
{
    private static readonly (int Bit, string Name)[] Leaf1Edx = [(0, "FPU"), (23, "MMX"), (25, "SSE"), (26, "SSE2"), (28, "HTT")];
    private static readonly (int Bit, string Name)[] Leaf1Ecx =
        [(0, "SSE3"), (1, "PCLMULQDQ"), (9, "SSSE3"), (12, "FMA3"), (19, "SSE4.1"), (20, "SSE4.2"), (22, "MOVBE"), (23, "POPCNT"), (25, "AES-NI"), (28, "AVX"), (29, "F16C"), (30, "RDRAND")];
    private static readonly (int Bit, string Name)[] Leaf7Ebx =
        [(2, "SGX"), (3, "BMI1"), (5, "AVX2"), (8, "BMI2"), (16, "AVX-512F"), (17, "AVX-512DQ"), (18, "RDSEED"), (19, "ADX"), (21, "AVX-512IFMA"), (28, "AVX-512CD"), (29, "SHA"), (30, "AVX-512BW"), (31, "AVX-512VL")];
    private static readonly (int Bit, string Name)[] Leaf7Ecx =
        [(1, "AVX-512VBMI"), (6, "AVX-512VBMI2"), (8, "GFNI"), (9, "VAES"), (10, "VPCLMULQDQ"), (11, "AVX-512VNNI"), (12, "AVX-512BITALG"), (14, "AVX-512VPOPCNTDQ")];
    private static readonly (int Bit, string Name)[] Leaf7Edx = [(22, "AMX-BF16"), (23, "AVX-512FP16"), (24, "AMX-TILE"), (25, "AMX-INT8")];
    private static readonly (int Bit, string Name)[] Leaf7S1Eax = [(4, "AVX-VNNI"), (5, "AVX-512BF16")];
    private static readonly (int Bit, string Name)[] Ext1Ecx = [(5, "LZCNT"), (6, "SSE4a"), (16, "FMA4")];
    private static readonly (int Bit, string Name)[] Ext1Edx = [(20, "NX"), (29, "x86-64")];

    public static CpuIdInfo Decode(CpuIdFunction cpuid)
    {
        var l0 = cpuid(0, 0);
        var vendor = Ascii(l0.Ebx, l0.Edx, l0.Ecx);
        var maxLeaf = l0.Eax;
        var maxExt = cpuid(0x80000000, 0).Eax;
        if (maxExt < 0x80000000) maxExt = 0;

        var features = new List<string>();
        int family = 0, model = 0, stepping = 0;
        uint signature = 0;
        bool hybrid = false, vmx = false, hypervisor = false;

        if (maxLeaf >= 1)
        {
            var l1 = cpuid(1, 0);
            signature = l1.Eax;
            stepping = (int)(l1.Eax & 0xF);
            var baseModel = (int)((l1.Eax >> 4) & 0xF);
            var baseFamily = (int)((l1.Eax >> 8) & 0xF);
            var extModel = (int)((l1.Eax >> 16) & 0xF);
            var extFamily = (int)((l1.Eax >> 20) & 0xFF);
            family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
            model = baseFamily is 0x6 or 0xF ? (extModel << 4) + baseModel : baseModel;
            Add(features, l1.Edx, Leaf1Edx);
            Add(features, l1.Ecx, Leaf1Ecx);
            vmx = Bit(l1.Ecx, 5);
            hypervisor = Bit(l1.Ecx, 31);
        }

        if (maxLeaf >= 7)
        {
            var l7 = cpuid(7, 0);
            Add(features, l7.Ebx, Leaf7Ebx);
            Add(features, l7.Ecx, Leaf7Ecx);
            Add(features, l7.Edx, Leaf7Edx);
            hybrid = Bit(l7.Edx, 15);
            if (l7.Eax >= 1)
            {
                Add(features, cpuid(7, 1).Eax, Leaf7S1Eax);
            }
        }

        var svm = false;
        var is64 = false;
        if (maxExt >= 0x80000001)
        {
            var e1 = cpuid(0x80000001, 0);
            Add(features, e1.Ecx, Ext1Ecx);
            Add(features, e1.Edx, Ext1Edx);
            svm = Bit(e1.Ecx, 2);
            is64 = Bit(e1.Edx, 29);
        }

        string? brand = null;
        if (maxExt >= 0x80000004)
        {
            var parts = new[] { cpuid(0x80000002, 0), cpuid(0x80000003, 0), cpuid(0x80000004, 0) };
            brand = string.Concat(parts.Select(p => Ascii(p.Eax, p.Ebx, p.Ecx, p.Edx))).Trim('\0', ' ');
            brand = string.Join(' ', brand.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        int? baseMhz = null, maxMhz = null, busMhz = null;
        if (maxLeaf >= 0x16)
        {
            var l16 = cpuid(0x16, 0);
            baseMhz = NonZero(l16.Eax & 0xFFFF);
            maxMhz = NonZero(l16.Ebx & 0xFFFF);
            busMhz = NonZero(l16.Ecx & 0xFFFF);
        }

        string? hvVendor = null;
        if (hypervisor)
        {
            var hv = cpuid(0x40000000, 0);
            hvVendor = Ascii(hv.Ebx, hv.Ecx, hv.Edx).Trim('\0', ' ');
            if (hvVendor.Length == 0) hvVendor = null;
        }

        var isAmd = vendor is "AuthenticAMD" or "HygonGenuine";
        var cacheLeaf = isAmd ? (maxExt >= 0x8000001D ? 0x8000001Du : 0u) : (maxLeaf >= 4 ? 4u : 0u);
        var caches = cacheLeaf == 0 ? [] : DecodeCaches(cpuid, cacheLeaf);
        var (arch, codename) = CpuMicroarchitecture.Lookup(vendor, family, model);

        return new CpuIdInfo
        {
            Vendor = vendor,
            Brand = string.IsNullOrWhiteSpace(brand) ? null : brand,
            Family = family,
            Model = model,
            Stepping = stepping,
            Signature = signature,
            MaxLeaf = maxLeaf,
            MaxExtendedLeaf = maxExt,
            Features = features,
            Caches = caches,
            IsHybrid = hybrid,
            HardwareVirtualization = vmx || svm,
            VirtualizationTechnology = vmx ? "Intel VT-x" : svm ? "AMD-V (SVM)" : null,
            HypervisorPresent = hypervisor,
            HypervisorVendor = hvVendor,
            BaseFrequencyMhz = baseMhz,
            MaxFrequencyMhz = maxMhz,
            BusFrequencyMhz = busMhz,
            Microarchitecture = arch,
            Codename = codename,
            Is64Bit = is64,
        };
    }

    internal static List<CpuCacheDescriptor> DecodeCaches(CpuIdFunction cpuid, uint leaf)
    {
        var list = new List<CpuCacheDescriptor>();
        for (uint i = 0; i < 16; i++)
        {
            var r = cpuid(leaf, i);
            var type = (int)(r.Eax & 0x1F);
            if (type == 0) break;
            if (type > 3) continue;
            var level = (int)((r.Eax >> 5) & 0x7);
            var sharing = (int)((r.Eax >> 14) & 0xFFF) + 1;
            var line = (int)(r.Ebx & 0xFFF) + 1;
            var partitions = (int)((r.Ebx >> 12) & 0x3FF) + 1;
            var ways = (int)((r.Ebx >> 22) & 0x3FF) + 1;
            var sets = (long)r.Ecx + 1;
            list.Add(new CpuCacheDescriptor(level, (CacheType)type, ways * partitions * line * sets, ways, line, sharing));
        }

        return list;
    }

    private static void Add(List<string> features, uint reg, (int Bit, string Name)[] map)
    {
        foreach (var (bit, name) in map)
        {
            if (Bit(reg, bit) && !features.Contains(name)) features.Add(name);
        }
    }

    private static bool Bit(uint reg, int bit) => ((reg >> bit) & 1) != 0;

    private static int? NonZero(uint v) => v == 0 ? null : (int)v;

    private static string Ascii(params uint[] regs)
    {
        var bytes = regs.SelectMany(BitConverter.GetBytes).ToArray();
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}

/// <summary>
/// Maps CPUID display family / model to microarchitecture and codename for common desktop and mobile parts.
/// Unknown combinations return nulls (shown as "Not Available"), never a guess.
/// </summary>
public static class CpuMicroarchitecture
{
    public static (string? Architecture, string? Codename) Lookup(string vendor, int family, int model)
    {
        if (vendor == "GenuineIntel" && family == 6)
        {
            return model switch
            {
                0x2A or 0x2D => ("Sandy Bridge", "Sandy Bridge"),
                0x3A or 0x3E => ("Ivy Bridge", "Ivy Bridge"),
                0x3C or 0x3F or 0x45 or 0x46 => ("Haswell", "Haswell"),
                0x3D or 0x47 or 0x4F or 0x56 => ("Broadwell", "Broadwell"),
                0x4E or 0x5E => ("Skylake", "Skylake"),
                0x55 => ("Skylake", "Skylake-SP / Cascade Lake"),
                0x8E => ("Skylake", "Kaby Lake / Whiskey Lake / Comet Lake (mobile)"),
                0x9E => ("Skylake", "Kaby Lake / Coffee Lake"),
                0xA5 or 0xA6 => ("Skylake", "Comet Lake"),
                0x7D or 0x7E => ("Sunny Cove", "Ice Lake"),
                0x6A or 0x6C => ("Sunny Cove", "Ice Lake-SP"),
                0xA7 => ("Cypress Cove", "Rocket Lake"),
                0x8C or 0x8D => ("Willow Cove", "Tiger Lake"),
                0x97 or 0x9A => ("Golden Cove + Gracemont", "Alder Lake"),
                0xBE => ("Gracemont", "Alder Lake-N"),
                0xB7 or 0xBA or 0xBF => ("Raptor Cove + Gracemont", "Raptor Lake"),
                0x8F => ("Golden Cove", "Sapphire Rapids"),
                0xCF => ("Raptor Cove", "Emerald Rapids"),
                0xAA or 0xAC => ("Redwood Cove + Crestmont", "Meteor Lake"),
                0xBD => ("Lion Cove + Skymont", "Lunar Lake"),
                0xC5 or 0xC6 => ("Lion Cove + Skymont", "Arrow Lake"),
                _ => (null, null),
            };
        }

        if (vendor == "AuthenticAMD")
        {
            return (family, model) switch
            {
                (0x17, 0x01) => ("Zen", "Summit Ridge / Naples"),
                (0x17, 0x11) => ("Zen", "Raven Ridge"),
                (0x17, 0x08) => ("Zen+", "Pinnacle Ridge"),
                (0x17, 0x18) => ("Zen+", "Picasso"),
                (0x17, 0x31) => ("Zen 2", "Rome / Castle Peak"),
                (0x17, 0x60) => ("Zen 2", "Renoir"),
                (0x17, 0x68) => ("Zen 2", "Lucienne"),
                (0x17, 0x71) => ("Zen 2", "Matisse"),
                (0x17, 0x90) => ("Zen 2", "Van Gogh"),
                (0x17, 0xA0) => ("Zen 2", "Mendocino"),
                (0x19, >= 0x00 and <= 0x0F) => ("Zen 3", "Milan"),
                (0x19, >= 0x20 and <= 0x2F) => ("Zen 3", "Vermeer"),
                (0x19, >= 0x40 and <= 0x4F) => ("Zen 3+", "Rembrandt"),
                (0x19, >= 0x50 and <= 0x5F) => ("Zen 3", "Cezanne"),
                (0x19, >= 0x10 and <= 0x1F) => ("Zen 4", "Genoa"),
                (0x19, >= 0x60 and <= 0x6F) => ("Zen 4", "Raphael"),
                (0x19, >= 0x70 and <= 0x7F) => ("Zen 4", "Phoenix / Hawk Point"),
                (0x19, >= 0xA0 and <= 0xAF) => ("Zen 4c", "Bergamo / Siena"),
                (0x1A, >= 0x00 and <= 0x1F) => ("Zen 5", "Turin"),
                (0x1A, >= 0x20 and <= 0x2F) => ("Zen 5", "Strix Point"),
                (0x1A, >= 0x40 and <= 0x4F) => ("Zen 5", "Granite Ridge"),
                (0x1A, >= 0x70 and <= 0x7F) => ("Zen 5", "Strix Halo"),
                _ => (null, null),
            };
        }

        return (null, null);
    }
}
