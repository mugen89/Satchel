using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Markup;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Data;
using Satchel.Security;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        // Rendering-cost tests: compare against WPF's software renderer.
        if (e.Args.Contains("--debug-software", StringComparer.OrdinalIgnoreCase))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
#endif

        try
        {
            AppPaths.Initialize(e.Args);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{AppInfo.Name} can't create its data folder next to the app.\n\n{ex.Message}\n\n" +
                            "Make sure the drive isn't write-protected.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Started by an update (or a restore): wait for the previous copy to close, then tidy up after the update.
        UpdateService.PrepareStartup(e.Args);

        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        var services = new ServiceCollection();
        services.AddSingleton<DataStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<SeedService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<WebService>();
        services.AddSingleton<VideoFeedService>();
        services.AddSingleton<PrivacyService>();
        services.AddSingleton<TimerService>();
        services.AddSingleton<MusicService>();
        services.AddSingleton<PodcastService>();
        services.AddSingleton<WeatherService>();
        services.AddSingleton<FeedService>();
        services.AddSingleton<DashboardProfileService>();
        services.AddSingleton<KeyBindingService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MainWindowViewModel>();
        Services = services.BuildServiceProvider();

        try
        {
            DatabaseInitializer.Initialize();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show($"The database at\n{AppPaths.DatabasePath}\ncouldn't be opened.\n\n{ex.Message}", AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settings = Services.GetRequiredService<SettingsService>();
        settings.Load();
        ApplyCulture(settings.Current.Culture, initial: true);
        settings.Changed += name => { if (name == nameof(AppSettings.Culture)) ApplyCulture(settings.Current.Culture, initial: false); };
        var theme = Services.GetRequiredService<ThemeService>();
        theme.Apply(settings.Current);
        settings.Changed += name =>
        {
            if (name is nameof(AppSettings.Theme) or nameof(AppSettings.Accent) or nameof(AppSettings.FontFamily) or
                nameof(AppSettings.CornerRadius) or nameof(AppSettings.AnimationsEnabled) or nameof(AppSettings.CardShadows))
                theme.Apply(settings.Current);
        };

        Services.GetRequiredService<DataStore>().Failed += ex => Log(ex);
        // Timers ring and music plays in the background, even on the lock screen.
        Services.GetRequiredService<TimerService>();
        Services.GetRequiredService<PrivacyService>();

        var window = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
        MainWindow = window;
        window.Show();

        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentCulture;

    /// <summary>Day/month names and date order follow this culture (English by default to match the UI).</summary>
    private static void ApplyCulture(string name, bool initial)
    {
        CultureInfo culture;
        try { culture = name == "system" ? SystemCulture : CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo("en-GB"); }
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        if (initial)
        {
            // XAML StringFormat and spell checking use the element language; can only be set once per process.
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
    }

    private bool AcquireSingleInstance()
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16];
        _instanceMutex = new Mutex(true, $"Satchel-{id}", out _ownsMutex);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Satchel-Activate-{id}");
        if (!_ownsMutex)
        {
            // Another instance is using this data folder: bring it to the front instead.
            _activateSignal.Set();
            return false;
        }
        var signal = _activateSignal;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
                }
                catch { return; }
            }
        }) { IsBackground = true, Name = "Satchel activation listener" };
        thread.Start();
        return true;
    }

    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock) return;
        var settings = Services.GetRequiredService<SettingsService>();
        if (settings.Current.LockOnSessionLock)
            await Dispatcher.InvokeAsync(async () => await Services.GetRequiredService<SessionService>().LockAsync()).Task.Unwrap();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        try
        {
            var message = e.Exception is IOException or SqliteException
                ? "Couldn't read or write the data file. Is the drive still connected?"
                : "Something went wrong: " + e.Exception.Message;
            Services?.GetService<ToastService>()?.Error(message);
        }
        catch
        {
            // ignore secondary failures
        }
    }

    public static void Log(Exception? ex) => Log(null, ex);

    public static void Log(string? context, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var file = Path.Combine(AppPaths.LogDirectory, $"errors-{DateTime.Now:yyyy-MM}.log");
            var prefix = context == null ? "" : context + ": ";
            File.AppendAllText(file, $"[{DateTime.Now:u}] {prefix}{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
            // logging must never crash the app
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        try { Services?.GetService<SettingsService>()?.SaveNow(); } catch { }
        SecureClipboard.ClearIfOurs();
        Crypto.Clear();
        SqliteConnection.ClearAllPools();
        if (_ownsMutex) { try { _instanceMutex?.ReleaseMutex(); } catch { } }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
