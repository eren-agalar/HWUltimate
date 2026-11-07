using System.Text;

namespace HWUltimate.Infrastructure.Smbios;

public sealed record SmbiosBios(string? Vendor, string? Version, string? ReleaseDate, int? RomSizeKb);
public sealed record SmbiosSystem(string? Manufacturer, string? Product, string? Version, string? SerialNumber, Guid? Uuid, string? Sku, string? Family);
public sealed record SmbiosBaseboard(string? Manufacturer, string? Product, string? Version, string? SerialNumber);
public sealed record SmbiosProcessor(string? Socket, string? Manufacturer, string? Version, int? ExternalClockMhz, int? MaxSpeedMhz, int? CurrentSpeedMhz, int? Cores, int? Threads);
public sealed record SmbiosMemoryDevice(
    string? DeviceLocator, string? BankLocator, long SizeMegabytes, string MemoryType, int? SpeedMts, int? ConfiguredSpeedMts,
    string? Manufacturer, string? SerialNumber, string? PartNumber, int? ConfiguredVoltageMv, string FormFactor,
    int? TotalWidthBits = null, int? DataWidthBits = null, int? Rank = null)
{
    /// <summary>A module whose total width exceeds its data width carries ECC check bits (e.g. 72 vs 64).</summary>
    public bool? HasEccBits => TotalWidthBits is { } t && DataWidthBits is { } d ? t > d : null;
}

/// <summary>SMBIOS type 16 (Physical Memory Array).</summary>
public sealed record SmbiosMemoryArray(string ErrorCorrection, long? MaxCapacityBytes, int? Slots, string Use);

public sealed record SmbiosData(
    Version Version,
    SmbiosBios? Bios,
    SmbiosSystem? System,
    SmbiosBaseboard? Baseboard,
    IReadOnlyList<SmbiosProcessor> Processors,
    IReadOnlyList<SmbiosMemoryDevice> MemoryDevices,
    int StructureCount,
    IReadOnlyList<SmbiosMemoryArray>? MemoryArrays = null)
{
    public IReadOnlyList<SmbiosMemoryArray> Arrays => MemoryArrays ?? [];
}

/// <summary>
/// Parser for the raw SMBIOS table returned by <c>GetSystemFirmwareTable('RSMB')</c>
/// (a <c>RawSMBIOSData</c> header followed by the structure table). Pure and platform-independent.
/// </summary>
public static class SmbiosParser
{
    private const int RawHeaderSize = 8;

    public static SmbiosData Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < RawHeaderSize)
        {
            throw new FormatException("SMBIOS buffer is too small.");
        }

        var version = new Version(raw[1], raw[2]);
        var length = (int)BitConverter.ToUInt32(raw[4..8]);
        var table = raw.Slice(RawHeaderSize, Math.Min(length, raw.Length - RawHeaderSize));
        return ParseTable(table, version);
    }

    public static SmbiosData ParseTable(ReadOnlySpan<byte> table, Version version)
    {
        SmbiosBios? bios = null;
        SmbiosSystem? system = null;
        SmbiosBaseboard? board = null;
        var cpus = new List<SmbiosProcessor>();
        var memory = new List<SmbiosMemoryDevice>();
        var arrays = new List<SmbiosMemoryArray>();
        var count = 0;
        var offset = 0;

        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var len = table[offset + 1];
            if (len < 4 || offset + len > table.Length)
            {
                break;
            }

            var formatted = table.Slice(offset, len);
            var stringsStart = offset + len;
            var strings = ReadStrings(table, stringsStart, out var next);
            count++;

            switch (type)
            {
                case 0:
                    bios ??= new SmbiosBios(Str(formatted, strings, 0x04), Str(formatted, strings, 0x05), Str(formatted, strings, 0x08),
                        Byte(formatted, 0x09) is { } rom ? (rom + 1) * 64 : null);
                    break;
                case 1:
                    system ??= new SmbiosSystem(Str(formatted, strings, 0x04), Str(formatted, strings, 0x05), Str(formatted, strings, 0x06),
                        Str(formatted, strings, 0x07), Uuid(formatted, version), Str(formatted, strings, 0x19), Str(formatted, strings, 0x1A));
                    break;
                case 2:
                    board ??= new SmbiosBaseboard(Str(formatted, strings, 0x04), Str(formatted, strings, 0x05), Str(formatted, strings, 0x06), Str(formatted, strings, 0x07));
                    break;
                case 4:
                    cpus.Add(new SmbiosProcessor(Str(formatted, strings, 0x04), Str(formatted, strings, 0x07), Str(formatted, strings, 0x10),
                        NonZero(Word(formatted, 0x12)), NonZero(Word(formatted, 0x14)), NonZero(Word(formatted, 0x16)),
                        CoreCount(formatted), ThreadCount(formatted)));
                    break;
                case 17:
                    var size = MemorySizeMb(formatted);
                    if (size > 0)
                    {
                        memory.Add(new SmbiosMemoryDevice(
                            Str(formatted, strings, 0x10), Str(formatted, strings, 0x11), size,
                            MemoryTypeName(Byte(formatted, 0x12) ?? 0),
                            NonZero(Word(formatted, 0x15)), NonZero(Word(formatted, 0x20)),
                            Str(formatted, strings, 0x17), Str(formatted, strings, 0x18), Str(formatted, strings, 0x1A),
                            NonZero(Word(formatted, 0x26)), FormFactorName(Byte(formatted, 0x0E) ?? 0),
                            NonZero(Word(formatted, 0x08)), NonZero(Word(formatted, 0x0A)),
                            Byte(formatted, 0x1B) is { } attr && (attr & 0xF) != 0 ? attr & 0xF : null));
                    }

                    break;
                case 16:
                    arrays.Add(ParseArray(formatted));
                    break;
                case 127:
                    return new SmbiosData(version, bios, system, board, cpus, memory, count, arrays);
            }

            offset = next;
        }

        return new SmbiosData(version, bios, system, board, cpus, memory, count, arrays);
    }

    private static SmbiosMemoryArray ParseArray(ReadOnlySpan<byte> f)
    {
        var ecc = (Byte(f, 0x06) ?? 0) switch
        {
            3 => "None",
            4 => "Parity",
            5 => "Single-bit ECC",
            6 => "Multi-bit ECC",
            7 => "CRC",
            1 => "Other",
            _ => "Unknown",
        };
        long? max = DWord(f, 0x07) switch
        {
            0x80000000 when f.Length >= 0x17 => (long)BitConverter.ToUInt64(f.Slice(0x0F, 8)),
            { } kb and > 0 and < 0x80000000 => (long)kb * 1024,
            _ => null,
        };
        var use = (Byte(f, 0x05) ?? 0) switch { 3 => "System memory", 4 => "Video memory", 5 => "Flash memory", 7 => "Cache memory", _ => "Other" };
        return new SmbiosMemoryArray(ecc, max, NonZero(Word(f, 0x0D)), use);
    }

    private static List<string> ReadStrings(ReadOnlySpan<byte> table, int start, out int next)
    {
        var list = new List<string>();
        var i = start;
        // An empty string-set is terminated by a double null.
        if (i + 1 < table.Length && table[i] == 0 && table[i + 1] == 0)
        {
            next = i + 2;
            return list;
        }

        while (i < table.Length)
        {
            var end = table[i..].IndexOf((byte)0);
            if (end < 0)
            {
                next = table.Length;
                return list;
            }

            if (end == 0)
            {
                next = i + 1;
                return list;
            }

            list.Add(Encoding.ASCII.GetString(table.Slice(i, end)).Trim());
            i += end + 1;
        }

        next = table.Length;
        return list;
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.", "To be filled by O.E.M.", "Default string", "System Product Name", "System manufacturer",
        "System Serial Number", "Not Specified", "None", "N/A", "0", "Unknown", "Undefined", "O.E.M.", "OEM",
    };

    private static string? Str(ReadOnlySpan<byte> f, List<string> strings, int at)
    {
        if (at >= f.Length)
        {
            return null;
        }

        var idx = f[at];
        if (idx == 0 || idx > strings.Count)
        {
            return null;
        }

        var s = strings[idx - 1];
        return IsPlaceholder(s) ? null : s;
    }

    /// <summary>OEM filler strings and serials made only of 0 / F / spaces carry no information.</summary>
    internal static bool IsPlaceholder(string s) =>
        string.IsNullOrWhiteSpace(s) || Placeholders.Contains(s) || s.All(c => c is '0' or ' ' or '-')
        || (s.Length >= 4 && s.All(c => c is 'F' or 'f'));

    private static byte? Byte(ReadOnlySpan<byte> f, int at) => at < f.Length ? f[at] : null;

    private static int? Word(ReadOnlySpan<byte> f, int at) => at + 1 < f.Length ? BitConverter.ToUInt16(f.Slice(at, 2)) : null;

    private static uint? DWord(ReadOnlySpan<byte> f, int at) => at + 3 < f.Length ? BitConverter.ToUInt32(f.Slice(at, 4)) : null;

    private static int? NonZero(int? v) => v is null or 0 or 0xFFFF ? null : v;

    private static int? CoreCount(ReadOnlySpan<byte> f)
    {
        var c = Byte(f, 0x23);
        return c == 0xFF ? NonZero(Word(f, 0x2A)) : c is null or 0 ? null : c;
    }

    private static int? ThreadCount(ReadOnlySpan<byte> f)
    {
        var t = Byte(f, 0x25);
        return t == 0xFF ? NonZero(Word(f, 0x2E)) : t is null or 0 ? null : t;
    }

    private static Guid? Uuid(ReadOnlySpan<byte> f, Version version)
    {
        if (f.Length < 0x18)
        {
            return null;
        }

        var bytes = f.Slice(0x08, 16);
        if (bytes.IndexOfAnyExcept((byte)0) < 0 || bytes.IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return null;
        }

        // SMBIOS ≥ 2.6 encodes the first three fields little-endian, which matches System.Guid's byte layout.
        var buffer = bytes.ToArray();
        if (version < new Version(2, 6))
        {
            Array.Reverse(buffer, 0, 4);
            Array.Reverse(buffer, 4, 2);
            Array.Reverse(buffer, 6, 2);
        }

        return new Guid(buffer);
    }

    internal static long MemorySizeMb(ReadOnlySpan<byte> f)
    {
        var size = Word(f, 0x0C);
        if (size is null or 0 or 0xFFFF)
        {
            return 0;
        }

        if (size == 0x7FFF)
        {
            return DWord(f, 0x1C) is { } ext ? ext & 0x7FFFFFFF : 0;
        }

        // Bit 15 set → value is in KB.
        return (size.Value & 0x8000) != 0 ? (size.Value & 0x7FFF) / 1024 : size.Value;
    }

    internal static string MemoryTypeName(byte type) => type switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => "Unknown",
    };

    private static string FormFactorName(byte ff) => ff switch
    {
        0x09 => "DIMM",
        0x0D => "SODIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        0x11 => "CAMM",
        _ => "Other",
    };
}
