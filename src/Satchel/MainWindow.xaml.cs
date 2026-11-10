using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DnD.Root = DragRoot;
        Title = AppInfo.Name;
        RestorePlacement();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            var music = App.Services.GetRequiredService<MusicService>();
            music.Attach(MusicPlayer);
            MusicHost = MusicPlayer;
            App.Services.GetRequiredService<PodcastService>().Attach(PodcastPlayer);
        };
#if DEBUG
        Loaded += (_, _) => DebugAutomation.Run(this);
#endif
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext;

    /// <summary>The background music player (shown over the Music page's video slot when asked).</summary>
    public static Controls.YouTubePlayer? MusicHost { get; private set; }
    private AppSettings Settings => App.Services.GetRequiredService<SettingsService>().Current;

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnStateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize;
        if (WindowState == WindowState.Minimized && Settings.LockOnMinimize && Vm.IsUnlocked)
            await Vm.LockAsync();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.IsUnlocked || Vm.Dialogs.Count > 0 || Vm.Palette != null) return;
        if (e.ChangedButton == MouseButton.XButton1) { _ = Vm.Shell?.GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { _ = Vm.Shell?.GoForward(); e.Handled = true; }
    }

    /// <summary>Focus is in a text box — or in a spreadsheet, which uses Ctrl+Shift+arrows and Alt+digits itself.</summary>
    private static bool IsTyping => Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or Controls.SheetGrid;

    /// <summary>Ctrl+0, Ctrl+1 and Ctrl+2 in a rich text editor: normal text, heading, subheading.</summary>
    private static bool IsEditorKey(Key key, ModifierKeys mods) =>
        mods == ModifierKeys.Control && key is Key.D0 or Key.D1 or Key.D2 && Keyboard.FocusedElement is System.Windows.Controls.RichTextBox;

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A box in Settings → Key bindings is listening: every key goes to it.
        if (KeyBindingService.Recorder is { } recorder)
        {
            if (ReferenceEquals(Keyboard.FocusedElement, recorder)) return;
            KeyBindingService.Recorder = null;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        var music = Vm.Music;

        if (key == Key.Escape)
        {
            if (Vm.Palette != null) { Vm.Palette = null; e.Handled = true; return; }
            if (App.Services.GetRequiredService<DialogService>().CloseTop()) { e.Handled = true; return; }
            if (Vm.IsUnlocked && !DnD.IsDragging && Vm.Shell?.CurrentPage?.OnEscape() == true) e.Handled = true;
            return;
        }

        // Media keys and music shortcuts work everywhere (even on the lock screen).
        switch (key)
        {
            case Key.MediaPlayPause when music.HasTrack:
            case Key.Space when mods == (ModifierKeys.Control | ModifierKeys.Shift) && music.HasTrack:
                music.PlayPause();
                e.Handled = true;
                return;
            case Key.MediaNextTrack when music.HasTrack:
            case Key.Right when mods == (ModifierKeys.Control | ModifierKeys.Shift) && music.HasTrack && !IsTyping:
                music.Next();
                e.Handled = true;
                return;
            case Key.MediaPreviousTrack when music.HasTrack:
            case Key.Left when mods == (ModifierKeys.Control | ModifierKeys.Shift) && music.HasTrack && !IsTyping:
                music.Previous();
                e.Handled = true;
                return;
            case Key.Up when mods == (ModifierKeys.Control | ModifierKeys.Shift) && music.HasTrack && !IsTyping:
                music.ChangeVolume(+5);
                e.Handled = true;
                return;
            case Key.Down when mods == (ModifierKeys.Control | ModifierKeys.Shift) && music.HasTrack && !IsTyping:
                music.ChangeVolume(-5);
                e.Handled = true;
                return;
            case Key.MediaStop when music.HasTrack:
                music.Stop();
                e.Handled = true;
                return;
        }

        if (!Vm.IsUnlocked) return;
        bool dialogOpen = Vm.Dialogs.Count > 0;
        var shell = Vm.Shell;

        // Your own key bindings (Settings → Key bindings): a key for any tab, and for showing / hiding private items.
        // In a note editor, Ctrl+0 / 1 / 2 stay with the text (normal, heading, subheading).
        if (!dialogOpen && shell != null && !KeyChord.IsModifierKey(key) && !IsEditorKey(key, mods) && shell.TryRunKeyBinding(KeyChord.From(mods, key)))
        {
            e.Handled = true;
            Vm.Palette = null;
            return;
        }

        // Alt+Left / Alt+Right: back and forward.
        if (mods == ModifierKeys.Alt && !dialogOpen && Vm.Palette == null && shell != null && key is Key.Left or Key.Right)
        {
            e.Handled = true;
            if (key == Key.Left) await shell.GoBack();
            else await shell.GoForward();
            return;
        }

        // Alt+1…9: dashboard profiles (digits on the main row only — Alt+numpad digits type special characters).
        if (mods == ModifierKeys.Alt && !dialogOpen && Vm.Palette == null && shell != null && key is >= Key.D1 and <= Key.D9 && !IsTyping)
        {
            if (shell.OpenDashboardProfileAt(key - Key.D1)) e.Handled = true;
            return;
        }

        // F1: keyboard cheat sheet.
        if (key == Key.F1 && mods == ModifierKeys.None && !dialogOpen)
        {
            e.Handled = true;
            Vm.Palette = null;
            await App.Services.GetRequiredService<DialogService>().ShowAsync(new ViewModels.Dialogs.ShortcutsDialogViewModel());
            return;
        }

        if (!ctrl) return;

        // Ctrl+Shift combinations.
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.D when !dialogOpen:
                    e.Handled = true;
                    Vm.Palette = null;
                    shell?.NavigateTo("Today");
                    return;
                case Key.T when !dialogOpen:
                    e.Handled = true;
                    Vm.Palette = null;
                    await Vm.StartTimerPromptAsync();
                    return;
                case Key.Tab when !dialogOpen:
                    e.Handled = true;
                    Vm.Palette = null;
                    shell?.NavigateRelative(-1);
                    return;
            }
        }

        switch (key)
        {
            case Key.OemQuestion when !dialogOpen:
                e.Handled = true;
                Vm.Palette = null;
                await App.Services.GetRequiredService<DialogService>().ShowAsync(new ViewModels.Dialogs.ShortcutsDialogViewModel());
                return;
            case Key.Tab when mods == ModifierKeys.Control && !dialogOpen:
                e.Handled = true;
                Vm.Palette = null;
                shell?.NavigateRelative(+1);
                return;
            case Key.T when mods == ModifierKeys.Control && !dialogOpen:
                e.Handled = true;
                Vm.Palette = null;
                shell?.NavigateTo("Clock");
                return;
            case Key.Oem5 or Key.OemBackslash when mods == ModifierKeys.Control:
                e.Handled = true;
                if (shell != null) shell.IsSidebarCollapsed = !shell.IsSidebarCollapsed;
                return;
            case Key.OemPlus or Key.Add when mods == ModifierKeys.Control:
                e.Handled = true;
                Settings.UiScale = Math.Min(1.4, Math.Round(Settings.UiScale + 0.05, 2));
                return;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.Control:
                e.Handled = true;
                Settings.UiScale = Math.Max(0.8, Math.Round(Settings.UiScale - 0.05, 2));
                return;
            // In a rich text editor Ctrl+0 means "normal text" (like Ctrl+1 / Ctrl+2 for headings).
            case Key.D0 or Key.NumPad0 when mods == ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.RichTextBox:
                e.Handled = true;
                Settings.UiScale = 1.0;
                return;
        }

        switch (key)
        {
            case Key.K:
            case Key.P when !dialogOpen:
                if (!dialogOpen) Vm.OpenPalette();
                e.Handled = true;
                break;
            case Key.L:
                e.Handled = true;
                await Vm.LockAsync();
                break;
            case >= Key.D1 and <= Key.D9 when !dialogOpen && Keyboard.Modifiers == ModifierKeys.Control && !IsEditorKey(key, mods):
                Vm.Palette = null;
                Vm.Shell?.NavigateByIndex(key - Key.D1);
                e.Handled = true;
                break;
            case Key.OemComma when !dialogOpen:
                Vm.Shell?.NavigateTo("Settings");
                e.Handled = true;
                break;
            case Key.N when !dialogOpen && Vm.Palette == null:
                Vm.Shell?.CurrentPage?.OnNewShortcut();
                e.Handled = true;
                break;
            case Key.F when !dialogOpen && Vm.Palette == null:
                Vm.Shell?.CurrentPage?.OnSearchShortcut();
                e.Handled = true;
                break;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        try
        {
            await Vm.FlushAsync();
        }
        catch
        {
            // closing must not be blocked by a failed save
        }
        SavePlacement();
        _closeConfirmed = true;
        // Close again outside of this Closing event (re-entrant Close() isn't allowed).
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void RestorePlacement()
    {
        var p = Settings.Window;
        if (p == null || p.Width < MinWidth || p.Height < MinHeight) return;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
        if (!virtualScreen.IntersectsWith(rect) || rect.Left + 100 > virtualScreen.Right || rect.Top + 50 > virtualScreen.Bottom) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        Settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized
        };
        App.Services.GetRequiredService<SettingsService>().SaveNow();
    }

    #region Maximize to the work area (custom chrome would otherwise overflow the screen)

    private const int WmGetMinMaxInfo = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    #endregion
}
