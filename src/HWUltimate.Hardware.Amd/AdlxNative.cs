using System.Runtime.InteropServices;

namespace HWUltimate.Hardware.Amd;

/// <summary>
/// Loader for the AMD Device Library eXtra (ADLX) runtime that ships with AMD Software: Adrenalin Edition.
/// Uses only the stable C entry points (<c>ADLXQueryFullVersion</c>, <c>ADLXQueryVersion</c>,
/// <c>ADLXInitialize</c>, <c>ADLXTerminate</c>).
/// </summary>
internal sealed unsafe class AdlxNative : IDisposable
{
    public const int Ok = 0;
    private const string LibraryName = "amdadlx64.dll";

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<ulong, nint*, int> _initialize;
    private readonly delegate* unmanaged[Cdecl]<int> _terminate;
    private bool _initialized;

    private AdlxNative(nint library)
    {
        _library = library;
        _initialize = NativeLibrary.TryGetExport(library, "ADLXInitialize", out var init) ? (delegate* unmanaged[Cdecl]<ulong, nint*, int>)init : null;
        _terminate = NativeLibrary.TryGetExport(library, "ADLXTerminate", out var term) ? (delegate* unmanaged[Cdecl]<int>)term : null;

        if (NativeLibrary.TryGetExport(library, "ADLXQueryFullVersion", out var qfv))
        {
            ulong full;
            if (((delegate* unmanaged[Cdecl]<ulong*, int>)qfv)(&full) == Ok)
            {
                FullVersion = full;
            }
        }

        if (NativeLibrary.TryGetExport(library, "ADLXQueryVersion", out var qv))
        {
            byte* text;
            if (((delegate* unmanaged[Cdecl]<byte**, int>)qv)(&text) == Ok && text != null)
            {
                Version = Marshal.PtrToStringAnsi((nint)text);
            }
        }
    }

    public ulong? FullVersion { get; }
    public string? Version { get; }

    /// <summary>Pointer to the IADLXSystem singleton once <see cref="Initialize"/> succeeds.</summary>
    public nint System { get; private set; }

    public static AdlxNative? TryLoad(out string reason)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            reason = "ADLX requires 64-bit Windows.";
            return null;
        }

        if (!NativeLibrary.TryLoad(LibraryName, out var lib))
        {
            reason = "AMD Software (amdadlx64.dll) not found.";
            return null;
        }

        reason = string.Empty;
        return new AdlxNative(lib);
    }

    public int Initialize()
    {
        if (_initialize == null || FullVersion is null)
        {
            return -1;
        }

        nint system;
        var result = _initialize(FullVersion.Value, &system);
        if (result == Ok)
        {
            System = system;
            _initialized = true;
        }

        return result;
    }

    public void Dispose()
    {
        if (_initialized && _terminate != null)
        {
            _terminate();
        }

        NativeLibrary.Free(_library);
    }
}
