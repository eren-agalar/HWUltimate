using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HWUltimate.App.Services;
using HWUltimate.Core.Settings;
using HWUltimate.Core.Updates;
using HWUltimate.UI.Controls;
using HWUltimate.UI.Localization;

namespace HWUltimate.App.ViewModels;

/// <summary>
/// In-app updates from GitHub Releases: checks shortly after start-up and every few hours (unless turned off), shows a
/// banner and flashes the taskbar when a new version is published, and downloads, verifies and installs it on request.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly ILogger<UpdateViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private bool _loading;

    public UpdateViewModel(IUpdateService updates, ISettingsService settings, ILogger<UpdateViewModel> logger)
    {
        _updates = updates;
        _settings = settings;
        _logger = logger;
        _loading = true;
        AutoCheck = settings.Current.General.CheckForUpdates;
        _loading = false;
        CurrentVersion = UpdateSource.Format(updates.CurrentVersion);
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (AutoCheck) await CheckAsync(userInitiated: false);
        };
    }

    public string CurrentVersion { get; }
    public string RepositoryUrl => UpdateSource.RepositoryUrl;

    [ObservableProperty] private UpdateState _state;
    [ObservableProperty] private ReleaseInfo? _available;
    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private string _bannerText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoCheck;

    /// <summary>Starts the background schedule. <paramref name="args"/> carries the result of an update that just ran.</summary>
    public void Start(IReadOnlyList<string> args)
    {
        if (args.Contains("--updated"))
        {
            BannerText = Loc.Format("Update_Installed", CurrentVersion);
            IsBannerVisible = true;
        }
        else if (args.Contains("--update-failed"))
        {
            BannerText = Loc.Get("Update_InstallFailed");
            IsBannerVisible = true;
        }

        _timer.Start();
    }

    partial void OnAutoCheckChanged(bool value)
    {
        if (_loading) return;
        _ = _settings.SaveAsync(_settings.Current with { General = _settings.Current.General with { CheckForUpdates = value } });
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(userInitiated: true);

    private async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy) return;
        IsBusy = true;
        State = UpdateState.Checking;
        StatusText = Loc.Get("Update_Checking");
        try
        {
            var release = await Task.Run(() => _updates.CheckAsync());
            if (release is null)
            {
                State = UpdateState.UpToDate;
                StatusText = Loc.Format("Update_UpToDate", CurrentVersion);
                return;
            }

            Available = release;
            State = UpdateState.Available;
            var version = UpdateSource.Format(release.Version);
            StatusText = Loc.Format("Update_AvailableStatus", version);
            // A skipped version notifies again only when the user checks by hand.
            if (userInitiated || _settings.Current.General.SkippedUpdateVersion != version)
            {
                BannerText = Loc.Format("Update_Banner", version);
                IsBannerVisible = true;
                FlashTaskbar();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            _logger.LogInformation(ex, "Update check failed");
            State = UpdateState.Failed;
            StatusText = Loc.Format("Update_CheckFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowNotes()
    {
        if (Available is not { } r) return;
        var notes = string.IsNullOrWhiteSpace(r.Notes) ? Loc.Get("Update_NoNotes") : r.Notes.Trim();
        var title = Loc.Format("Update_NotesTitle", UpdateSource.Format(r.Version));
        if (HwuDialog.Show(Application.Current.MainWindow, title, notes, Loc.Get("Update_Install"), Loc.Get("Common_Close")))
        {
            InstallCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } r || IsBusy) return;
        if (r.Package is null)
        {
            OpenReleasePage();
            return;
        }

        IsBusy = true;
        State = UpdateState.Downloading;
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                StatusText = Loc.Format("Update_Downloading", (int)(p * 100));
            });
            var staging = await Task.Run(() => _updates.DownloadAsync(r, progress));
            State = UpdateState.Installing;
            StatusText = Loc.Get("Update_Ready");
            HwuDialog.Show(Application.Current.MainWindow, Loc.Get("Update_ReadyTitle"), Loc.Format("Update_ReadyText", UpdateSource.Format(r.Version)));
            _updates.ScheduleInstall(staging);
            Application.Current.Shutdown();
        }
        catch (UnauthorizedAccessException ex)
        {
            Fail(ex, Loc.Get("Update_NoWriteAccess"));
            OpenReleasePage();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            Fail(ex, Loc.Format("Update_Failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(Exception ex, string message)
    {
        _logger.LogWarning(ex, "Update failed");
        State = UpdateState.Failed;
        StatusText = message;
        BannerText = message;
        IsBannerVisible = true;
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (Available is { } r)
        {
            await _settings.SaveAsync(_settings.Current with { General = _settings.Current.General with { SkippedUpdateVersion = UpdateSource.Format(r.Version) } });
        }

        IsBannerVisible = false;
    }

    [RelayCommand]
    private void Dismiss() => IsBannerVisible = false;

    [RelayCommand]
    private void OpenReleasePage() => Ui.OpenUrl(Available?.PageUrl ?? UpdateSource.ReleasesUrl);

    [RelayCommand]
    private void OpenRepository() => Ui.OpenUrl(UpdateSource.RepositoryUrl);

    // ------------------------------------------------------------------ taskbar attention
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Flashes the taskbar button until the window is brought to the front (only when it is not already active).</summary>
    private static void FlashTaskbar()
    {
        if (Application.Current.MainWindow is not { IsActive: false } window) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 0x3 | 0xC, Count = 0, Timeout = 0 }; // FLASHW_ALL | FLASHW_TIMERNOFG
        FlashWindowEx(ref info);
    }
}
