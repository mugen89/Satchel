#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Satchel.Services;
using Satchel.ViewModels;
using KeyChord = Satchel.Core.KeyChord;
using WKey = System.Windows.Input.Key;
using WKeyboard = System.Windows.Input.Keyboard;
using WKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WMods = System.Windows.Input.ModifierKeys;

namespace Satchel;

/// <summary>
/// Debug-build-only automation used to verify the UI with screenshots, e.g.:
///   Satchel.exe --data .\testdata --debug-password "pw" --debug-shots "Dashboard=a.png;Notes=b.png" --debug-exit
/// Never compiled into Release builds.
/// </summary>
internal static class DebugAutomation
{
    public static async void Run(MainWindow window)
    {
        var args = Environment.GetCommandLineArgs();
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        if (Arg("--debug-schema-log") is { } schemaLog)
            File.WriteAllLines(schemaLog, Data.DatabaseInitializer.AppliedChanges.DefaultIfEmpty("(no schema changes)"));

        var password = Arg("--debug-password");
        if (password == null) return;

        if (Arg("--debug-bindings") is { } bindingLog)
        {
            // WPF only emits binding traces with a debugger attached unless tracing is refreshed explicitly.
            System.Diagnostics.PresentationTraceSources.Refresh();
            var listener = new StackTraceListener(bindingLog);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.Trace.AutoFlush = true;
            System.Diagnostics.PresentationTraceSources.DataBindingSource.TraceEvent(System.Diagnostics.TraceEventType.Warning, 0, "binding trace listener attached");
            listener.Flush();
        }

        try
        {
            var vm = (MainWindowViewModel)window.DataContext;
            var settings = App.Services.GetRequiredService<SettingsService>();
            if (Arg("--debug-theme") is { } theme) settings.Current.Theme = theme;
            if (Arg("--debug-accent") is { } accent) settings.Current.Accent = accent;
            if (Arg("--debug-scale") is { } scale) settings.Current.UiScale = double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture);

            await Task.Delay(700);
            if (Arg("--debug-lockshot") is { } lockShot)
            {
                await Task.Delay(900);
                Capture(window, lockShot);
            }

            if (vm.CurrentView is LockViewModel lockVm)
            {
                if (lockVm.Mode == LockMode.Setup)
                {
                    lockVm.Password = password;
                    lockVm.Confirm = password;
                    await lockVm.CreateCommand.ExecuteAsync(null);
                    if (Arg("--debug-recoveryshot") is { } recShot)
                    {
                        await Task.Delay(900);
                        Capture(window, recShot);
                    }
                    lockVm.RecoverySaved = true;
                    await lockVm.FinishSetupCommand.ExecuteAsync(null);
                }
                else
                {
                    lockVm.Password = password;
                    await lockVm.UnlockCommand.ExecuteAsync(null);
                }
            }

            await Task.Delay(1200);
            // A fresh vault (key derivation, database creation) can take longer than that on a busy PC.
            for (int i = 0; i < 150 && vm.Shell == null; i++) await Task.Delay(200);
            if (Has("--debug-seed-vault")) await SeedVaultAsync();
            if (Has("--debug-seed-contacts")) await SeedContactsAsync();
            if (Has("--debug-seed-boards")) await SeedBoardsAsync();
            if (Has("--debug-seed-modules")) await SeedModulesAsync(vm);
            if (Has("--debug-seed-new")) await SeedNewModulesAsync();
            if (Has("--debug-seed-feeds"))
            {
                var feedService = App.Services.GetRequiredService<FeedService>();
                var feedStore = App.Services.GetRequiredService<Data.DataStore>();
                var log = new System.Text.StringBuilder();
                foreach (var url in new[] { "https://feeds.bbci.co.uk/news/world/rss.xml", "https://feeds.npr.org/510289/podcast.xml" })
                {
                    var (feed, parsed, error) = await feedService.ResolveAsync(url);
                    log.AppendLine($"{url}: {(feed == null ? "ERROR " + error : $"{feed.Title} kind={feed.Kind} items={parsed?.Items.Count}")}");
                    if (feed == null) continue;
                    await feedStore.AddAsync(feed);
                    var n = await feedService.RefreshAsync(feed, [], firstImport: true, parsed);
                    log.AppendLine($"  imported unread={n}");
                }
                if (Arg("--debug-shots") is { } shotsArg && shotsArg.Contains('\\'))
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(shotsArg.Split(';')[0].Split('=')[1])!, "feeds.txt"), log.ToString());
            }
            if (Has("--debug-seed-library"))
            {
                var store = App.Services.GetRequiredService<Data.DataStore>();
                if (await store.CountAsync<Models.LibraryDoc>() == 0)
                    await store.AddAsync(new Models.LibraryDoc { Title = "Welcome to your Library", Markdown = SampleDocs.Guide, Collection = "Guides", LastOpenedAt = DateTime.Now });
            }
            if (Arg("--debug-shots") is { } shots)
            {
                foreach (var spec in shots.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = spec.IndexOf('=');
                    var target = spec[..eq];
                    var file = spec[(eq + 1)..];
                    string page = target;
                    object? parameter = null;
                    var colon = target.IndexOf(':');
                    if (colon > 0)
                    {
                        page = target[..colon];
                        var p = target[(colon + 1)..];
                        parameter = int.TryParse(p, out var n) ? n : p;
                    }
                    vm.Palette = null;
                    var openDialogs = App.Services.GetRequiredService<DialogService>().Dialogs.ToList();
                    foreach (var dialog in openDialogs) dialog.Cancel();
                    if (openDialogs.Count > 0) await Task.Delay(500);
                    if (page == "Palette")
                    {
                        vm.OpenPalette();
                        if (parameter is string q && vm.Palette is { } pal) pal.GetType().GetProperty("Query")?.SetValue(pal, q);
                    }
                    else if (page is "ProjectDetail" or "ReadingSearch" or "GamesSearch" or "ReadingDetail" or "ReadingQuotes" or "GamesDetail" or "GamesLibrary" or "SketchEdit" or "VideoPlay" or "SettingsTabs")
                    {
                        await ModuleShotAsync(window, vm, page, parameter as string, file);
                        continue;
                    }
                    else if (page.StartsWith("Library") && page != "Library")
                    {
                        var reader = App.Services.GetRequiredService<SettingsService>().Current;
                        vm.Shell?.NavigateTo("Library", parameter);
                        await Task.Delay(1200);
                        var library = vm.Shell?.GetPage<LibraryViewModel>("Library");
                        switch (page)
                        {
                            case "LibrarySepia": reader.ReaderTheme = "sepia"; reader.ReaderFont = "book"; break;
                            case "LibraryNight": reader.ReaderTheme = "night"; reader.ReaderFont = "sans"; break;
                            case "LibraryEdit": reader.ReaderTheme = "app"; reader.ReaderFont = "serif"; library?.ToggleEditCommand.Execute(null); break;
                            case "LibraryPages": reader.ReaderTheme = "paper"; reader.ReaderLayout = "pages"; break;
                            case "LibrarySepiaPages": reader.ReaderTheme = "sepia"; reader.ReaderLayout = "pages"; reader.ReaderPageSize = "Book"; break;
                            case "LibraryFind": reader.ReaderTheme = "paper"; if (library != null) { library.OpenFindCommand.Execute(null); library.FindText = "table"; library.FindNextCommand.Execute(null); } break;
                        }
                        await Task.Delay(1500);
                        Capture(window, file);
                        if (page == "LibraryEdit") library?.ToggleEditCommand.Execute(null);
                        if (page.EndsWith("Pages")) { reader.ReaderLayout = "scroll"; reader.ReaderPageSize = "A4"; }
                        continue;
                    }
                    else if (page is "BoardFocus" or "BoardSwing" or "BoardLink")
                    {
                        // Board states that need interaction: an editor in focus, a string mid-swing, a selected connection.
                        vm.Shell?.NavigateTo("Board", parameter);
                        await Task.Delay(1800);
                        var board = vm.Shell?.GetPage<BoardViewModel>("Board");
                        if (page == "BoardFocus" && FindVisuals<Controls.RichTextEditor>(window).FirstOrDefault(r => r.IsVisible) is { } editor)
                        {
                            editor.FocusEditor();
                            editor.Editor.SelectAll();
                        }
                        else if (page == "BoardSwing" && board?.Stickies.FirstOrDefault(s => s.IsImage) is { } moving)
                        {
                            for (int i = 0; i < 12; i++) { moving.X += 14; await Task.Delay(16); }
                            await Task.Delay(120);
                        }
                        else if (page == "BoardLink" && board?.Links.FirstOrDefault() is { } link) board.SelectedLink = link;
                        await Task.Delay(page == "BoardSwing" ? 60 : 700);
                        Capture(window, file);
                        ReportPathErrors(window, target);
                        continue;
                    }
                    else vm.Shell?.NavigateTo(page, parameter);
                    await Task.Delay(1800);
                    Capture(window, file);
                    ReportPathErrors(window, target);
                }
            }
            if (Arg("--debug-privacy-test") is { } privacyFile) await PrivacyTestAsync(privacyFile);
            if (Arg("--debug-modules-test") is { } modulesFile) await ModulesTestAsync(vm, modulesFile);
            if (Arg("--debug-music") is { } musicDir) await MusicTestAsync(window, vm, musicDir);
            if (Arg("--debug-board-new") is { } boardDir) await BoardNewTestAsync(window, vm, boardDir);
            if (Arg("--debug-steam") is { } steamFile)
            {
                var (games, error) = await Task.Run(SteamService.ReadLocal);
                // Parser check with Steam's real file shapes (libraryfolders.vdf, appmanifest, localconfig).
                var lib = Vdf.Parse("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"620\"\t\t\"12345\"\n\t\t}\n\t}\n}\n");
                var manifest = Vdf.Parse("\"AppState\"\n{\n\t\"appid\"\t\t\"620\"\n\t\"name\"\t\t\"Portal 2\"\n\t\"StateFlags\"\t\t\"4\"\n}\n");
                var local = Vdf.Parse("\"UserLocalConfigStore\"\n{\n\"Software\"\n{\n\"Valve\"\n{\n\"Steam\"\n{\n\"apps\"\n{\n\"620\"\n{\n\"LastPlayed\"\t\"1731000000\"\n\"Playtime\"\t\"754\"\n}\n}\n}\n}\n}\n}\n");
                var libPath = ((lib.Get("libraryfolders") as Dictionary<string, object>)?.Get("0") as Dictionary<string, object>)?.GetString("path");
                var state = manifest.Get("AppState") as Dictionary<string, object>;
                var app = ((((((local.Get("UserLocalConfigStore") as Dictionary<string, object>)?.Get("Software") as Dictionary<string, object>)?.Get("Valve") as Dictionary<string, object>)
                    ?.Get("Steam") as Dictionary<string, object>)?.Get("apps") as Dictionary<string, object>)?.Get("620")) as Dictionary<string, object>;
                File.WriteAllLines(steamFile, new[]
                {
                    $"folder={SteamService.FindSteamFolder()} error={error} count={games.Count}",
                    $"parser: path='{libPath}' appid={state?.GetString("appid")} name='{state?.GetString("name")}' playtime={app?.GetString("Playtime")} last={app?.GetString("LastPlayed")}"
                }.Concat(games.Take(8).Select(g => $"{g.AppId} {g.Name} {g.Minutes} min last={g.LastPlayed}")));
            }
            if (Arg("--debug-drag-test") is { } dragDir) await DragTestAsync(window, (MainWindowViewModel)window.DataContext, dragDir);
            if (Arg("--debug-dash-test") is { } dashDir) await DashTestAsync(window, vm, dashDir);
            if (Arg("--debug-board-upgrade") is { } upgradeDir) await BoardUpgradeTestAsync(window, vm, upgradeDir);
            if (Arg("--debug-keybindings") is { } keysDir) await KeyBindingsTestAsync(window, vm, keysDir);
            if (Arg("--debug-sheets") is { } sheetsDir) await SheetsTestAsync(window, vm, sheetsDir, Arg("--debug-fixtures"));
            if (Arg("--debug-library-perf") is { } perfDir) await LibraryPerfTestAsync(window, vm, perfDir);
            if (Arg("--debug-library-folders") is { } foldersDir) await LibraryFoldersTestAsync(window, vm, foldersDir);
            if (Arg("--debug-update-test") is { } updateDir && await UpdateTestAsync(window, vm, updateDir, Arg("--debug-update-feed")!)) return; // closing for the restart
            if (Arg("--debug-after-update") is { } afterDir) await AfterUpdateTestAsync(window, vm, afterDir);

            if (Arg("--debug-richtext-test") is { } rtDir)
            {
                var marker = $"Marker-{DateTime.Now:HHmmss}";
                vm.Shell?.NavigateTo("Notes", 1);
                await Task.Delay(1500);
                var rtb = FindVisuals<System.Windows.Controls.RichTextBox>(window).FirstOrDefault(r => r.IsVisible);
                string result;
                if (rtb != null)
                {
                    rtb.Focus();
                    rtb.CaretPosition = rtb.Document.ContentEnd;
                    rtb.CaretPosition.InsertTextInRun(" " + marker);
                    await Task.Delay(100); // well under the debounce вЂ” the flush-on-close path must catch it
                    var notes = vm.Shell?.GetPage<NotesViewModel>("Notes");
                    if (notes != null) await notes.CloseEditorCommand.ExecuteAsync(null);
                    await Task.Delay(800);
                    var store = App.Services.GetRequiredService<Data.DataStore>();
                    var saved = await store.FindAsync<Models.Note>(1);
                    result = $"rich text saved = {saved?.PlainText.Contains(marker)}; content bytes = {saved?.Content.Length}";
                }
                else result = "rich text box not found";
                File.WriteAllText(Path.Combine(rtDir, "richtext.txt"), result);
            }

            if (Arg("--debug-lock-cycle") is { } cycleDir)
            {
                var log = new System.Text.StringBuilder();
                vm.Shell?.NavigateTo("Notes", 1);
                await Task.Delay(1200);
                await vm.LockAsync();
                await Task.Delay(1000);
                log.AppendLine($"after lock: view={vm.CurrentView?.GetType().Name}, unlocked={Security.Crypto.IsUnlocked}");
                Capture(window, Path.Combine(cycleDir, "cycle-locked.png"));
                if (vm.CurrentView is LockViewModel again)
                {
                    again.Password = "wrong-password";
                    await again.UnlockCommand.ExecuteAsync(null);
                    log.AppendLine($"wrong password: error='{again.Error}', unlocked={Security.Crypto.IsUnlocked}");
                    again.Password = password;
                    await again.UnlockCommand.ExecuteAsync(null);
                    await Task.Delay(1500);
                    log.AppendLine($"after unlock: view={vm.CurrentView?.GetType().Name}, unlocked={Security.Crypto.IsUnlocked}");
                    vm.Shell?.NavigateTo("Vault");
                    await Task.Delay(1500);
                    Capture(window, Path.Combine(cycleDir, "cycle-unlocked.png"));
                }
                File.WriteAllText(Path.Combine(cycleDir, "cycle.txt"), log.ToString());
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Core.AppPaths.DataDirectory, "debug-error.txt"), ex.ToString());
        }

        if (Has("--debug-exit")) window.Close();
    }

    // ----- dashboard: customize-mode smoothness, profiles, resizing, drag placement -----

    /// <summary>Frames per second while something animates (CompositionTarget.Rendering keeps the render loop running).</summary>
    private static async Task<string> MeasureFramesAsync(string label, int ms = 3000)
    {
        int frames = 0;
        double worst = 0;
        var last = TimeSpan.Zero;
        void OnRender(object? sender, EventArgs e)
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == last) return; // raised more than once per frame sometimes
            if (last != TimeSpan.Zero) worst = Math.Max(worst, (t - last).TotalMilliseconds);
            frames++;
            last = t;
        }
        CompositionTarget.Rendering += OnRender;
        await Task.Delay(ms);
        CompositionTarget.Rendering -= OnRender;
        return $"{label}: {frames * 1000.0 / ms:0.0} fps, worst frame gap {worst:0} ms";
    }

    private static async Task DashTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        vm.Shell?.NavigateTo("Dashboard");
        await Task.Delay(2500);
        var dash = vm.Shell?.GetPage<DashboardViewModel>("Dashboard");
        if (dash == null) { File.WriteAllText(Path.Combine(dir, "dash.txt"), "no dashboard"); return; }
        log.AppendLine($"render tier={RenderCapability.Tier >> 16} widgets={dash.Widgets.Count}");
        log.AppendLine(await MeasureFramesAsync("idle"));
        dash.IsCustomizing = true;
        await Task.Delay(600);
        log.AppendLine(await MeasureFramesAsync("customizing"));
        var frames = FindVisuals<System.Windows.Controls.Grid>(window).Where(g => g.Name == "frame").ToList();
        log.AppendLine($"frames cached while wiggling: {frames.Count(g => g.CacheMode is BitmapCache)} of {dash.Widgets.Count}");
        Capture(window, Path.Combine(dir, "dash-customize.png"));
        if (Environment.GetCommandLineArgs().Contains("--debug-dash-variants"))
        {
            // Same session, same renderer: what does each kind of motion cost?
            foreach (var f in frames) f.CacheMode = null;
            log.AppendLine(await MeasureFramesAsync("variant: tilt, not cached"));
            dash.IsCustomizing = false;
            await Task.Delay(300);
            var shifts = new List<TranslateTransform>();
            foreach (var f in frames)
            {
                var t = new TranslateTransform();
                f.RenderTransform = t;
                f.CacheMode = new BitmapCache();
                t.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(-0.8, 0.8, TimeSpan.FromMilliseconds(230)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                shifts.Add(t);
            }
            log.AppendLine(await MeasureFramesAsync("variant: sideways shake, cached"));
            foreach (var f in frames) f.CacheMode = null;
            log.AppendLine(await MeasureFramesAsync("variant: sideways shake, not cached"));
            foreach (var t in shifts) t.BeginAnimation(TranslateTransform.XProperty, null);
            foreach (var f in frames) { f.CacheMode = null; f.RenderTransform = Transform.Identity; }
            var one = frames.FirstOrDefault();
            if (one != null)
            {
                var r = new RotateTransform();
                one.RenderTransformOrigin = new Point(0.5, 0.5);
                one.RenderTransform = r;
                r.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(-0.3, 0.3, TimeSpan.FromMilliseconds(230)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                log.AppendLine(await MeasureFramesAsync("variant: one card tilting"));
                r.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            var outline = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            var outlines = FindVisuals<System.Windows.Controls.Border>(window).Where(b => b.Name == "outline").ToList();
            foreach (var o in outlines) o.BeginAnimation(UIElement.OpacityProperty, outline);
            log.AppendLine(await MeasureFramesAsync($"variant: {outlines.Count} outlines pulsing"));
            foreach (var o in outlines) o.BeginAnimation(UIElement.OpacityProperty, null);
            dash.IsCustomizing = true;
            await Task.Delay(400);
        }

        string Order() => string.Join(",", dash.Widgets.Select(w => w.Key));
        var panel = FindVisuals<Controls.MasonryPanel>(window)
            .FirstOrDefault(p => p.Children.Count > 0 && p.Children[0] is System.Windows.Controls.ContentPresenter { Content: WidgetViewModel });
        if (panel == null) log.AppendLine("ERROR dashboard panel not found");
        else
        {
            // Where would a card land? Held exactly over the first card's corner it should go first; held over its own slot it stays.
            var cards = panel.Children.Cast<UIElement>().ToList();
            var dragged = cards[Math.Min(4, cards.Count - 1)];
            var own = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)dragged);
            var first = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)cards[0]);
            log.AppendLine($"best index: held at own slot -> {panel.BestIndex(dragged, own)} (is {cards.IndexOf(dragged)}), held at the first slot -> {panel.BestIndex(dragged, new Rect(first.TopLeft, own.Size))}");

            // A drag from that card's slot to the top-left corner in 24 steps: the card should only ever move towards the start, never flicker back.
            log.AppendLine($"order before: {Order()}");
            var item = ((System.Windows.Controls.ContentPresenter)dragged).Content as WidgetViewModel;
            var path = new List<int>();
            for (int step = 0; step <= 24; step++)
            {
                var at = new Point(own.X + (first.X - own.X) * step / 24, own.Y + (first.Y - own.Y) * step / 24);
                int current = dash.Widgets.IndexOf(item!);
                int target = panel.BestIndex(dragged, new Rect(at, own.Size));
                if (target >= 0 && target != current) dash.Widgets.Move(current, target);
                panel.UpdateLayout();
                path.Add(dash.Widgets.IndexOf(item!));
            }
            bool monotonic = path.Zip(path.Skip(1)).All(p => p.Second <= p.First);
            log.AppendLine($"drag path indices: {string.Join(" ", path)} (only towards the start: {monotonic})");
            log.AppendLine($"order after: {Order()}");
        }

        // Resizing: a fixed height shows more rows (and scrolls); width snaps to columns.
        var tasks = dash.Widgets.OfType<TasksWidget>().FirstOrDefault();
        if (tasks != null)
        {
            int before = tasks.Items.Count;
            tasks.IsResizing = true;
            tasks.Height = 260;
            tasks.Span = 2;
            await Task.Delay(500);
            var frame = FindVisuals<System.Windows.Controls.Grid>(window).FirstOrDefault(g => g.Name == "frame" && g.DataContext == tasks);
            log.AppendLine($"tasks widget: rows {before} -> {tasks.Items.Count} at 260 px; frame {frame?.ActualWidth:0}x{frame?.ActualHeight:0}; columns {panel?.Columns} of {panel?.ColumnWidth:0} px; '{tasks.SizeText}'");
            Capture(window, Path.Combine(dir, "dash-resize.png"));
            dash.EndResize(tasks);
            dash.FitHeightCommand.Execute(tasks);
            tasks.Span = 1;
            await Task.Delay(400);
            log.AppendLine($"after fit: rows {tasks.Items.Count}, frame height {frame?.ActualHeight:0}, fixed={tasks.IsFixedHeight}");
        }

        // Esc leaves customize mode.
        log.AppendLine($"Esc while customizing handled={dash.OnEscape()} -> customizing={dash.IsCustomizing}; Esc again handled={dash.OnEscape()}");

        // Profiles: a second setup with an icon, a widget hidden only there, switching back and forth.
        var profiles = App.Services.GetRequiredService<DashboardProfileService>();
        var shell = vm.Shell!;
        int mainCount = dash.Widgets.Count;
        var newDialog = dash.NewProfileCommand.ExecuteAsync(null);
        await Task.Delay(800);
        Capture(window, Path.Combine(dir, "dash-profile-dialog.png"));
        App.Services.GetRequiredService<DialogService>().CloseTop();
        await newDialog;
        profiles.Add("Work", Core.Glyphs.Work, profiles.Active.Layout.Count > 0 ? profiles.Active.Layout : dash.Widgets.Select(w => new WidgetState { Key = w.Key, Span = w.Span }).ToList());
        await Task.Delay(500);
        var quote = dash.Widgets.FirstOrDefault(w => w.Key == "quote");
        if (quote != null) dash.HideWidgetCommand.Execute(quote);
        await Task.Delay(300);
        int workCount = dash.Widgets.Count;
        log.AppendLine($"profiles: {string.Join(", ", profiles.All.Select(p => $"{p.Name}{(p.Icon.Length > 0 ? "(icon)" : "")}"))}; active={dash.ProfileName}; top-bar shortcuts={shell.DashboardShortcuts.Count}");
        Capture(window, Path.Combine(dir, "dash-work.png"));
        bool toMain = shell.OpenDashboardProfileAt(0);
        await Task.Delay(500);
        int backCount = dash.Widgets.Count;
        shell.OpenDashboardProfileAt(1);
        await Task.Delay(500);
        log.AppendLine($"widgets: Main {mainCount}, Work after hiding the quote {workCount}, Alt+1 -> {toMain} Main again {backCount}, Alt+2 -> Work {dash.Widgets.Count} (quote hidden={dash.HiddenWidgets.Any(w => w.Key == "quote")})");
        log.AppendLine($"Alt+9 with no 9th profile -> {shell.OpenDashboardProfileAt(8)}");
        var saved = App.Services.GetRequiredService<SettingsService>().Current;
        log.AppendLine($"saved: {saved.DashboardProfiles.Count} profiles, active={saved.DashboardProfiles.FirstOrDefault(p => p.Id == saved.ActiveDashboard)?.Name}, legacy layout entries={saved.DashboardLayout.Count}");
        var palette = new CommandPaletteViewModel(shell, vm, App.Services.GetRequiredService<SearchService>(), App.Services.GetRequiredService<SettingsService>(),
            App.Services.GetRequiredService<SessionService>(), App.Services.GetRequiredService<BackupService>(), App.Services.GetRequiredService<ToastService>(),
            App.Services.GetRequiredService<DialogService>(), App.Services.GetRequiredService<PrivacyService>(), App.Services.GetRequiredService<MusicService>(),
            App.Services.GetRequiredService<TimerService>(), profiles, App.Services.GetRequiredService<KeyBindingService>()) { Query = "dashboard work" };
        log.AppendLine($"palette 'dashboard work': {string.Join(" | ", palette.Results.Take(3).Select(r => r.Title + " " + r.Subtitle))}");

        // Settings: the keyboard section and its search; the private switch lives in Privacy now.
        shell.NavigateTo("Settings");
        await Task.Delay(1400);
        var settingsVm = shell.GetPage<SettingsViewModel>("Settings");
        if (settingsVm != null)
        {
            settingsVm.GoToSectionCommand.Execute("Keys");
            await Task.Delay(700);
            Capture(window, Path.Combine(dir, "settings-keys.png"));
            settingsVm.ShortcutQuery = "zoom";
            await Task.Delay(400);
            log.AppendLine($"shortcut search 'zoom': {string.Join(" | ", settingsVm.ShortcutsLeft.Concat(settingsVm.ShortcutsRight).SelectMany(g => g.Items.Select(i => $"{g.Title}: {i.Keys}")))}");
            settingsVm.ShortcutQuery = "nothing-like-this";
            log.AppendLine($"shortcut search with no match: has={settingsVm.HasShortcuts}");
            settingsVm.ShortcutQuery = "";
            await Task.Delay(500);
            settingsVm.GoToSectionCommand.Execute("Privacy");
            await Task.Delay(700);
            Capture(window, Path.Combine(dir, "settings-privacy.png"));
        }
        log.AppendLine($"total shortcuts listed: {Core.Shortcuts.All.Sum(g => g.Items.Count)} in {Core.Shortcuts.All.Count} groups");
        shell.NavigateTo("Dashboard");
        await Task.Delay(1200);
        Capture(window, Path.Combine(dir, "dash-final.png"));
        // The lower half: stats tiles and video thumbnails adapt to the widget's width.
        var stats = dash.Widgets.OfType<StatsWidget>().FirstOrDefault();
        if (stats != null) stats.Span = 2;
        await Task.Delay(500);
        if (FindVisuals<System.Windows.Controls.ScrollViewer>(window).FirstOrDefault(s => s.Name == "Scroll" && s.IsVisible) is { } pageScroll)
        {
            pageScroll.ScrollToVerticalOffset(pageScroll.ScrollableHeight);
            await Task.Delay(700);
            Capture(window, Path.Combine(dir, "dash-lower.png"));
        }
        File.WriteAllText(Path.Combine(dir, "dash.txt"), log.ToString());
    }

    // ----- Library speed: opening big documents, coming back to the tab, typing in the editor -----

    private static async Task<long> UntilIdle(Func<Task> action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await action();
        // Rendering is queued at Background / ContextIdle and layout follows: wait until the UI has nothing left to do.
        for (int i = 0; i < 4; i++) await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return sw.ElapsedMilliseconds;
    }

    private static string BigText(int paragraphs)
    {
        var words = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua enim minim veniam quis nostrud exercitation".Split(' ');
        var rnd = new Random(7);
        var sb = new System.Text.StringBuilder("# A long book\n\n");
        for (int p = 0; p < paragraphs; p++)
        {
            if (p % 12 == 0) sb.Append("\n## Chapter ").Append(p / 12 + 1).Append("\n\n");
            if (p % 25 == 7) { for (int i = 0; i < 4; i++) sb.Append("- item ").Append(i).Append(" with **bold** words\n"); sb.Append('\n'); }
            for (int w = 0; w < 140; w++) sb.Append(words[rnd.Next(words.Length)]).Append(w % 17 == 5 ? ", " : " ");
            sb.Append("*end*.\n\n");
        }
        return sb.ToString();
    }

    private static string BigTable(int rows, int cols)
    {
        var sb = new System.Text.StringBuilder("# A big table\n\n|");
        for (int c = 0; c < cols; c++) sb.Append(" Column ").Append(c + 1).Append(" |");
        sb.Append("\n|").Append(string.Concat(Enumerable.Repeat(" ---: |", cols))).Append('\n');
        for (int r = 0; r < rows; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cols; c++) sb.Append(' ').Append((r * 31 + c * 7) % 1000).Append('.').Append(c).Append(" |");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BigCode(int blocks)
    {
        var sb = new System.Text.StringBuilder("# Code\n\n");
        for (int b = 0; b < blocks; b++)
        {
            sb.Append("Block ").Append(b).Append(":\n\n```csharp\n");
            for (int l = 0; l < 40; l++) sb.Append("    var value").Append(l).Append(" = Compute(\"text\", ").Append(l).Append(") * 2; // comment\n");
            sb.Append("```\n\n");
        }
        return sb.ToString();
    }

    // ----- updates: check a stand-in for GitHub, refuse a damaged download, install, restart -----

    private static async Task ConfirmNextDialogAsync(System.Text.StringBuilder log)
    {
        var dialogs = App.Services.GetRequiredService<DialogService>();
        for (int i = 0; i < 80; i++)
        {
            if (dialogs.Dialogs.OfType<ViewModels.Dialogs.ConfirmDialogViewModel>().FirstOrDefault() is { } confirm)
            {
                log.AppendLine($"dialog: “{confirm.Title}” — {confirm.Message}");
                confirm.ConfirmCommand.Execute(null);
                return;
            }
            await Task.Delay(100);
        }
        log.AppendLine("no confirmation dialog appeared");
    }

    /// <returns>True when the update was installed and Satchel is restarting.</returns>
    private static async Task<bool> UpdateTestAsync(MainWindow window, MainWindowViewModel vm, string dir, string feed)
    {
        var log = new System.Text.StringBuilder();
        var shell = vm.Shell!;
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        var store = App.Services.GetRequiredService<Data.DataStore>();
        settings.OnlineFeatures = true;
        settings.CheckUpdatesOnStart = false;
        await store.AddAsync(new Models.Note { Title = "Written before the update" });
        log.AppendLine($"running {Core.AppInfo.Version} from {Core.AppPaths.AppDirectory}");
        log.AppendLine($"can install here: {UpdateService.CannotInstallReason() ?? "yes"}");
        shell.NavigateTo("Settings", "Updates");
        await UntilIdle(() => Task.Delay(900));
        var updates = shell.GetPage<SettingsViewModel>("Settings")!.Updates;
        updates.RepositoryText = "not a repo!";
        log.AppendLine($"bad name → {updates.RepositoryHint} (saved '{settings.UpdateRepository}')");
        updates.RepositoryText = "https://github.com/test/satchel.git";
        log.AppendLine($"pasted address → saved '{settings.UpdateRepository}' · {updates.RepositoryHint}");
        Capture(window, Path.Combine(dir, "updates-1-before.png"));

        using var http = new System.Net.Http.HttpClient();
        // A damaged download is refused, and nothing is changed.
        await http.GetStringAsync(feed + "/control/bad");
        await updates.CheckCommand.ExecuteAsync(null);
        await UntilIdle(() => Task.Delay(300));
        log.AppendLine($"check → '{updates.Status}' · available {updates.Available?.VersionText ?? "none"} · feed: {string.Join(", ", updates.Feed.Select(f => f.VersionText + (f.IsNew ? " (new)" : "") + (f.IsInstalled ? " (installed)" : "") + $" {f.Lines.Count} lines"))}");
        Capture(window, Path.Combine(dir, "updates-2-available.png"));
        var exe = Environment.ProcessPath!;
        var stamp = File.GetLastWriteTimeUtc(exe);
        var bad = updates.InstallCommand.ExecuteAsync(null);
        await ConfirmNextDialogAsync(log);
        await bad;
        log.AppendLine($"damaged download → '{updates.Status}' · error shown {updates.StatusIsError} · program unchanged {File.GetLastWriteTimeUtc(exe) == stamp} · .old files {Directory.GetFiles(Core.AppPaths.AppDirectory, "*.old*").Length}");
        await UntilIdle(() => Task.Delay(300));
        Capture(window, Path.Combine(dir, "updates-3-damaged.png"));

        // The real thing.
        await http.GetStringAsync(feed + "/control/good");
        await updates.CheckCommand.ExecuteAsync(null);
        var install = updates.InstallCommand.ExecuteAsync(null);
        await ConfirmNextDialogAsync(log);
        for (int i = 0; i < 40 && !updates.IsDownloading; i++) await Task.Delay(50);
        await Task.Delay(700);
        Capture(window, Path.Combine(dir, "updates-4-downloading.png"));
        log.AppendLine($"while downloading: {updates.ProgressText} ({updates.Progress:P0})");
        File.WriteAllText(Path.Combine(dir, "update-1.txt"), log.ToString());
        await install;
        log.AppendLine($"after installing: '{updates.Status}' · error {updates.StatusIsError}");
        log.AppendLine($"app folder now: {string.Join(", ", Directory.GetFiles(Core.AppPaths.AppDirectory).Select(Path.GetFileName))}");
        File.WriteAllText(Path.Combine(dir, "update-1.txt"), log.ToString());
        return !updates.StatusIsError;
    }

    private static async Task AfterUpdateTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"running {Core.AppInfo.Version} · this run follows an update: {UpdateService.JustUpdated?.From ?? "-"} → {UpdateService.JustUpdated?.To ?? "-"}");
        await Task.Delay(1500);
        var toasts = App.Services.GetRequiredService<ToastService>();
        log.AppendLine("toasts: " + string.Join(" | ", toasts.Toasts.Select(t => t.Message + (t.HasAction ? $" [{t.ActionText}]" : ""))));
        Capture(window, Path.Combine(dir, "updates-5-restarted.png"));
        log.AppendLine($"app folder: {string.Join(", ", Directory.GetFiles(Core.AppPaths.AppDirectory).Select(Path.GetFileName))}");
        var inUpdates = Directory.Exists(UpdateService.UpdatesDirectory) ? Directory.GetFileSystemEntries(UpdateService.UpdatesDirectory).Select(Path.GetFileName) : [];
        log.AppendLine($"Data\\Updates: {string.Join(", ", inUpdates)}");
        var backups = Directory.Exists(Core.AppPaths.BackupDirectory) ? Directory.GetFiles(Core.AppPaths.BackupDirectory).Select(Path.GetFileName) : [];
        log.AppendLine($"backups: {string.Join(", ", backups)}");
        var notes = await App.Services.GetRequiredService<Data.DataStore>().ListAsync<Models.Note>();
        log.AppendLine($"note from before the update: {notes.Any(n => n.Title == "Written before the update")}");
        vm.Shell!.NavigateTo("Settings", "Updates");
        await UntilIdle(() => Task.Delay(900));
        var updates = vm.Shell.GetPage<SettingsViewModel>("Settings")!.Updates;
        log.AppendLine($"settings: {updates.VersionText} · feed {string.Join(", ", updates.Feed.Select(f => f.VersionText + (f.IsInstalled ? " (installed)" : "") + (f.IsNew ? " (new)" : "")))} · offered {updates.Available?.VersionText ?? "nothing"}");
        Capture(window, Path.Combine(dir, "updates-6-settings.png"));
        File.WriteAllText(Path.Combine(dir, "update-2.txt"), log.ToString());
    }

    private static async Task LibraryFoldersTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var shell = vm.Shell!;
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        var privacy = App.Services.GetRequiredService<PrivacyService>();
        settings.ConfirmDeletes = false;
        // Documents from before folders: their "collections" (any capitalisation) become folders.
        await store.AddAsync(new Models.LibraryDoc { Title = "Pancakes", Markdown = "# Pancakes\n\nFlour, milk, eggs.", Collection = "Recipes", LastOpenedAt = DateTime.Now.AddMinutes(-5) });
        await store.AddAsync(new Models.LibraryDoc { Title = "Soup", Markdown = "# Soup\n\nWater and vegetables.", Collection = "recipes", LastOpenedAt = DateTime.Now.AddMinutes(-6) });
        await store.AddAsync(new Models.LibraryDoc { Title = "Q3 report", Markdown = "# Q3 report\n\nNumbers.", LastOpenedAt = DateTime.Now.AddMinutes(-7) });
        await store.AddAsync(new Models.LibraryDoc { Title = "Diary", Markdown = "# Diary\n\nSecret thoughts.", LastOpenedAt = DateTime.Now.AddMinutes(-8) });
        await UntilIdle(async () => { shell.NavigateTo("Library"); await Task.Delay(300); });
        var library = shell.GetPage<LibraryViewModel>("Library")!;
        if (!library.Docs.Any(d => d.Title == "Pancakes")) await library.ReloadAsync(); // another test opened the Library already
        string Rows() => string.Join(" | ", library.FolderRows.Select(r => new string('·', r.Depth * 2) + r.Name + " (" + r.Count + ")" + (r.IsPrivate ? " [private]" : "") + (r.IsSelected ? " *" : "")));
        string List() => string.Join(", ", library.Filtered.Select(d => d.Title));
        LibraryFolderRow Row(string name) => library.FolderRows.First(r => r.Name == name);
        log.AppendLine("after moving collections into folders: " + Rows());
        var dbDocs = await store.ListAsync<Models.LibraryDoc>();
        log.AppendLine("collections left in the database: " + dbDocs.Count(d => d.Collection.Length > 0));

        var work = await library.CreateFolderAsync("Work", null);
        var reports = await library.CreateFolderAsync("Reports", work);
        var q3 = library.Docs.First(d => d.Title == "Q3 report");
        await library.MoveToFolderAsync(q3, reports.Id);
        log.AppendLine("with Work › Reports: " + Rows());
        library.SelectFolderCommand.Execute(Row("Work"));
        log.AppendLine($"Work lists: [{List()}] · title '{library.ListTitle}' · empty text shown {library.IsListEmpty}");
        library.SelectFolderCommand.Execute(Row("Reports"));
        log.AppendLine($"Reports lists: [{List()}] · open doc '{library.SelectedDoc?.Title}'");
        if (FindVisuals<Controls.DropOverlay>(window).FirstOrDefault() is { } overlay) log.AppendLine($"drop overlay: '{overlay.Title}'");
        log.AppendLine("move choices: " + string.Join(" / ", library.FolderChoices.Select(c => c.Label)));

        await library.MoveFolderAsync(work, reports.Id);
        log.AppendLine($"Work into its own subfolder refused: {work.ParentId == null}");
        await library.MoveFolderAsync(reports, null);
        log.AppendLine("Reports to the top: " + Rows());
        await library.MoveFolderAsync(reports, work.Id);
        library.ToggleFolderCommand.Execute(Row("Work"));
        log.AppendLine("Work folded: " + Rows());
        library.ToggleFolderCommand.Execute(Row("Work"));
        library.SearchText = "flour";
        await UntilIdle(() => Task.Delay(300));
        log.AppendLine($"search 'flour' while in Reports: [{List()}] · title '{library.ListTitle}'");
        library.SearchText = "";
        library.SelectFolderCommand.Execute(Row("Recipes"));
        await UntilIdle(() => Task.Delay(400));
        Capture(window, Path.Combine(dir, "folders-1.png"));

        // A private folder hides its documents and folders.
        var diary = library.Docs.First(d => d.Title == "Diary");
        var secret = await library.CreateFolderAsync("Personal", null);
        var inner = await library.CreateFolderAsync("Old", secret);
        await library.MoveToFolderAsync(diary, inner.Id);
        await privacy.SetAsync(secret, true, quiet: true);
        await UntilIdle(() => Task.Delay(400));
        log.AppendLine("Personal marked private (hidden): " + Rows());
        log.AppendLine($"Diary listed: {library.Docs.Any(d => d.Title == "Diary")} · in the database while hidden: {(await store.ListAsync<Models.LibraryDoc>()).Any(d => d.Title == "Diary")}");
        settings.ShowPrivate = true;
        await UntilIdle(() => Task.Delay(1200));
        library = shell.GetPage<LibraryViewModel>("Library")!;
        if (!library.FolderRows.Any(r => r.Name == "Personal")) { await library.OnNavigatedToAsync(null); await UntilIdle(() => Task.Delay(300)); }
        log.AppendLine("private items shown: " + Rows());
        library.SelectFolderCommand.Execute(Row("Old"));
        await UntilIdle(() => Task.Delay(400));
        Capture(window, Path.Combine(dir, "folders-2.png"));
        settings.ShowPrivate = false;
        await UntilIdle(() => Task.Delay(1200));
        library = shell.GetPage<LibraryViewModel>("Library")!;
        if (library.FolderRows.Count == 0) { await library.OnNavigatedToAsync(null); await UntilIdle(() => Task.Delay(300)); }

        // Deleting a folder keeps what's inside (it moves up a level).
        await library.DeleteFolderCommand.ExecuteAsync(Row("Work"));
        log.AppendLine("Work deleted: " + Rows());
        var folders = await store.ListAsync<Models.LibraryFolder>();
        log.AppendLine("folders in the database: " + string.Join(", ", folders.Select(f => f.Name + (f.ParentId is int p ? $"→{folders.FirstOrDefault(x => x.Id == p)?.Name}" : ""))));
        var q3Now = (await store.ListAsync<Models.LibraryDoc>()).First(d => d.Title == "Q3 report");
        log.AppendLine($"Q3 report is in: {folders.FirstOrDefault(f => f.Id == q3Now.FolderId)?.Name ?? "no folder"}");
        File.WriteAllText(Path.Combine(dir, "folders.txt"), log.ToString());
    }

    private static async Task LibraryPerfTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var shell = vm.Shell!;
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var docs = new (string Title, string Markdown)[] { ("Long book", BigText(700)), ("Big table", BigTable(500, 8)), ("Lots of code", BigCode(60)) };
        foreach (var (title, md) in docs)
            await store.AddAsync(new Models.LibraryDoc { Title = title, Markdown = md, LastOpenedAt = DateTime.Now.AddDays(-1) });
        log.AppendLine($"sizes: {string.Join(", ", docs.Select(d => $"{d.Title} {d.Markdown.Length / 1024} KB"))}");

        long first = await UntilIdle(async () => { shell.NavigateTo("Library"); await Task.Delay(300); });
        log.AppendLine($"first visit to the Library: {first} ms");
        var library = shell.GetPage<LibraryViewModel>("Library")!;
        if (!library.Docs.Any(d => d.Title == docs[0].Title)) await library.ReloadAsync(); // another test opened the Library already
        foreach (var (title, _) in docs)
        {
            var doc = library.Docs.First(d => d.Title == title);
            long open = await UntilIdle(async () => { library.SelectedDoc = doc; await Task.Delay(50); });
            long back = 0, back2 = 0;
            back = await UntilIdle(async () => { shell.NavigateTo("Dashboard"); await Task.Delay(400); shell.NavigateTo("Library"); await Task.Delay(50); });
            back2 = await UntilIdle(async () => { shell.NavigateTo("Notes"); await Task.Delay(400); shell.NavigateTo("Library"); await Task.Delay(50); });
            log.AppendLine($"{title}: open {open} ms · back to the tab {back} ms, {back2} ms (incl. 400 ms on the other page) — renders so far {Views.Pages.LibraryView.RenderCount}, building {Views.Pages.LibraryView.RenderMs} ms, padding changes {Views.Pages.LibraryView.PaddingChanges}");
            Capture(window, Path.Combine(dir, "perf-" + title.Replace(' ', '-').ToLowerInvariant() + ".png"));
        }

        // Where does a visit's time go? One forced layout of the open book, and a visit without the contents panel.
        var book = library.Docs.First(d => d.Title == "Long book");
        library.SelectedDoc = book;
        await UntilIdle(() => Task.Delay(100));
        if (FindVisuals<System.Windows.Controls.FlowDocumentScrollViewer>(window).FirstOrDefault(r => r.IsVisible) is { } reader && reader.Document is { } flow)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            flow.PagePadding = new Thickness(flow.PagePadding.Left + 1, flow.PagePadding.Top, flow.PagePadding.Right + 1, flow.PagePadding.Bottom);
            reader.UpdateLayout();
            log.AppendLine($"one layout of the long book: {sw.ElapsedMilliseconds} ms (reader {reader.ActualWidth:0}×{reader.ActualHeight:0})");
            int layouts = 0;
            EventHandler counter = (_, _) => layouts++;
            reader.LayoutUpdated += counter;
            long visit = await UntilIdle(async () => { shell.NavigateTo("Notes"); await Task.Delay(400); shell.NavigateTo("Library"); await Task.Delay(50); });
            reader.LayoutUpdated -= counter;
            log.AppendLine($"visit with contents: {visit} ms");
        }
        var settingsForToc = App.Services.GetRequiredService<SettingsService>().Current;
        settingsForToc.ReaderShowToc = false;
        long noToc = await UntilIdle(async () => { shell.NavigateTo("Notes"); await Task.Delay(400); shell.NavigateTo("Library"); await Task.Delay(50); });
        log.AppendLine($"visit without the contents panel: {noToc} ms");
        settingsForToc.ReaderShowToc = true;
        int passes = 0;
        EventHandler pass = (_, _) => passes++;
        window.LayoutUpdated += pass;
        long counted = await UntilIdle(async () => { shell.NavigateTo("Notes"); await Task.Delay(400); shell.NavigateTo("Library"); await Task.Delay(50); });
        window.LayoutUpdated -= pass;
        log.AppendLine($"visit: {counted} ms, {passes} layout passes in the window");

        // Typing in the editor: the live preview follows.
        await UntilIdle(() => Task.Delay(100));
        library.ToggleEditCommand.Execute(null);
        await UntilIdle(() => Task.Delay(100));
        var typing = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            typing.Add(await UntilIdle(async () =>
            {
                library.EditText = library.EditText.Insert(20, "x");
                await Task.Delay(1600);
            }));
        }
        log.AppendLine($"preview while editing the long book: {string.Join(", ", typing)} ms (incl. 1600 ms of waiting for the pause)");
        library.ToggleEditCommand.Execute(null);
        await UntilIdle(() => Task.Delay(100));

        // Typing in the search box.
        long search = await UntilIdle(async () => { library.SearchText = "chapter"; await Task.Delay(50); });
        library.SearchText = "";
        log.AppendLine($"search across documents: {search} ms");
        File.WriteAllText(Path.Combine(dir, "perf.txt"), log.ToString());
    }

    // ----- Library imports (drop / import of md, txt, docx, doc, rtf, xlsx…) and the Sheets tab -----

    private static async Task SheetsTestAsync(MainWindow window, MainWindowViewModel vm, string dir, string? fixtures)
    {
        var log = new System.Text.StringBuilder();
        var shell = vm.Shell!;
        var store = App.Services.GetRequiredService<Data.DataStore>();
        void Shot(string name) => Capture(window, Path.Combine(dir, name + ".png"));
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { log.AppendLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }

        // --- Library: everything the fixtures folder has ---
        await Step("library", async () =>
        {
            shell.NavigateTo("Library");
            await Task.Delay(1500);
            var library = shell.GetPage<LibraryViewModel>("Library")!;
            if (fixtures != null)
            {
                var files = Directory.GetFiles(fixtures).Where(f => Services.Import.DocumentImporter.IsSupported(f)).OrderBy(f => f).ToList();
                int before = library.Docs.Count;
                await library.ImportFilesAsync(files);
                await Task.Delay(600);
                log.AppendLine($"library: {files.Count} files → {library.Docs.Count - before} documents");
                foreach (var d in library.Docs.Take(library.Docs.Count - before).Reverse())
                {
                    var assets = await store.ListAsync<Models.DocAsset>(q => q.Where(a => a.DocId == d.Id));
                    log.AppendLine($"  {d.FileName,-22} '{d.Title}' {d.WordCount} words, {assets.Count} pictures: {Models.LibraryDoc.MakeExcerpt(d.Markdown)}");
                }
                foreach (var (file, shot) in new[] { ("report.docx", "library-docx"), ("budget.xlsx", "library-xlsx"), ("shopping-cp1251.txt", "library-txt") })
                {
                    if (library.Docs.FirstOrDefault(d => d.FileName == file) is not { } doc) continue;
                    library.SelectedDoc = doc;
                    await Task.Delay(1300);
                    Shot(shot);
                }
            }
            if (FindVisuals<Controls.DropOverlay>(window).FirstOrDefault(o => o.IsVisible || o.Visibility == Visibility.Collapsed) is { } overlay)
            {
                overlay.Show(3, 1);
                await Task.Delay(400);
                Shot("library-drop");
                overlay.Hide();
                await Task.Delay(300);
            }
        });

        // --- Sheets: a new spreadsheet, typed through the grid ---
        shell.NavigateTo("Sheets", "new");
        await Task.Delay(1800);
        var sheets = shell.GetPage<SheetsViewModel>("Sheets")!;
        var doc = sheets.Document!;
        var grid = FindVisuals<Controls.SheetGrid>(window).First(g => g.IsVisible);
        string Disp(string a1) { Services.Sheets.CellRef.TryParse(a1, out var r, out var c); return doc.Sheet.Get(r, c)?.Display ?? "(empty)"; }
        void Key(System.Windows.Input.Key key, IInputElement? target = null)
        {
            var t = (UIElement)(target ?? System.Windows.Input.Keyboard.FocusedElement ?? grid);
            t.RaiseEvent(new WKeyEventArgs(WKeyboard.PrimaryDevice, PresentationSource.FromVisual(grid)!, 0, key) { RoutedEvent = WKeyboard.PreviewKeyDownEvent });
        }
        void Type(string text)
        {
            foreach (var ch in text)
            {
                var target = (UIElement)(System.Windows.Input.Keyboard.FocusedElement as UIElement ?? grid);
                var args = new System.Windows.Input.TextCompositionEventArgs(WKeyboard.PrimaryDevice, new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, target, ch.ToString()))
                    { RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent };
                target.RaiseEvent(args);
            }
        }

        await Step("typing", async () =>
        {
            grid.Focus();
            await Task.Delay(200);
            log.AppendLine($"grid focused={grid.IsKeyboardFocused}");
            // Header row: Item ⇥ Amount ⇥ Share ↵ — then rows of data.
            foreach (var row in new[] { new[] { "Item", "Amount", "Paid" }, ["Rent", "650", "2026-11-01"], ["Food", "320,5", "2026-11-03"], ["Transport", "75.25", "2026-11-05"], ["Gifts", "12%", ""] })
            {
                doc.Select(Array.IndexOf(new[] { "Item", "Rent", "Food", "Transport", "Gifts" }, row[0]), 0);
                for (int i = 0; i < row.Length; i++)
                {
                    if (row[i].Length > 0) Type(row[i]);
                    Key(i < row.Length - 1 ? System.Windows.Input.Key.Tab : System.Windows.Input.Key.Enter);
                }
            }
            await Task.Delay(200);
            log.AppendLine($"typed: A1={Disp("A1")} B2={Disp("B2")} B3={Disp("B3")} B4={Disp("B4")} C2={Disp("C2")} B5={Disp("B5")} active={doc.ActiveAddress}");
            // A formula built by typing, then F2 and Esc (no change), then Delete on a cell.
            doc.Select(5, 0); Type("Total"); Key(System.Windows.Input.Key.Tab);
            Type("=SUM(B2:B4)"); Key(System.Windows.Input.Key.Enter);
            log.AppendLine($"=SUM typed: B6={Disp("B6")} raw={doc.Sheet.Get(5, 1)?.Raw}");
            doc.Select(5, 1); Key(System.Windows.Input.Key.F2); Type("*2"); Key(System.Windows.Input.Key.Escape);
            log.AppendLine($"F2 + Esc keeps it: B6 raw={doc.Sheet.Get(5, 1)?.Raw}");
            doc.Select(4, 1); Key(System.Windows.Input.Key.Delete);
            log.AppendLine($"Delete B5: {Disp("B5")}");
            Key(System.Windows.Input.Key.Down); Key(System.Windows.Input.Key.Down);
            log.AppendLine($"arrows: active={doc.ActiveAddress}");
            doc.Undo();
            log.AppendLine($"undo: B5={Disp("B5")}; redo:");
            doc.Redo();
            log.AppendLine($"  B5={Disp("B5")}");
        });

        await Step("formulas", async () =>
        {
            var book = doc.Book;
            void Set(string a1, string raw) { Services.Sheets.CellRef.TryParse(a1, out var r, out var c); doc.Commit(r, c, raw); }
            Set("D1", "Share"); Set("D2", "=B2/B$6"); doc.SelectRange(1, 3, 3, 3); doc.Fill(down: true);
            doc.SelectRange(1, 3, 3, 3); doc.SetFormat("0.0%");
            log.AppendLine($"fill down: D3 raw={doc.Sheet.Get(2, 3)?.Raw} D2..D4={Disp("D2")} {Disp("D3")} {Disp("D4")}");
            doc.SelectRange(1, 1, 3, 1); doc.SetFormat(Services.Sheets.CellFormat.CurrencyCode());
            log.AppendLine($"currency: B2={Disp("B2")} B6={Disp("B6")}");
            doc.Select(0, 0, false); doc.SelectRange(0, 0, 0, 3); doc.ToggleStyle(Services.Sheets.CellStyle.Bold);
            doc.Select(1, 4); doc.Commit(1, 4, "=IF(B2>500,\"big\",\"ok\")"); doc.Commit(2, 4, "=VLOOKUP(\"Food\",A2:B4,2,FALSE)"); doc.Commit(3, 4, "=B4/0");
            doc.Commit(4, 4, "=XLOOKUP(\"Transport\",A2:A4,C2:C4)"); doc.Sheet.Get(4, 4); doc.Select(4, 4); doc.SetFormat("dd mmm yyyy");
            log.AppendLine($"E2..E5: {Disp("E2")} | {Disp("E3")} | {Disp("E4")} | {Disp("E5")}");
            doc.Select(6, 1); doc.AutoSum();
            log.AppendLine($"AutoSum at B7: raw={doc.Sheet.Get(6, 1)?.Raw} = {Disp("B7")}");
            doc.Select(1, 0); doc.InsertRows(below: false);
            log.AppendLine($"insert row above 2: A3={Disp("A3")} B7 raw={doc.Sheet.Get(6, 1)?.Raw} D3 raw={doc.Sheet.Get(2, 3)?.Raw}");
            doc.Undo();
            log.AppendLine($"undo insert: A2={Disp("A2")} B6 raw={doc.Sheet.Get(5, 1)?.Raw}");
            doc.SelectRange(1, 1, 3, 1);
            doc.Sort(ascending: true);
            log.AppendLine($"sort B2:B4 ascending: B2..B4={Disp("B2")} {Disp("B3")} {Disp("B4")} (A unchanged: {Disp("A2")})");
            doc.Undo();
            doc.Select(1, 1);
            doc.Sort(ascending: false);
            log.AppendLine($"sort table by Amount, header kept: A1={Disp("A1")} A2={Disp("A2")} A3={Disp("A3")} A4={Disp("A4")} D2 raw={doc.Sheet.Get(1, 3)?.Raw}");
            doc.SetFrozenRows(1);
            sheets.GoTo("A1:B4");
            log.AppendLine($"status for A1:B4: {doc.StatusText}");
            await Task.Delay(500);
            Shot("sheets-data");
            doc.Select(2, 0);
            grid.BeginEdit(null, editMode: true);
            await Task.Delay(300);
            Shot("sheets-editing");
            grid.CancelEdit();
        });

        await Step("tabs and save", async () =>
        {
            doc.AddSheet();
            doc.Commit(0, 0, "=Sheet1!B6*2");
            log.AppendLine($"Sheet2!A1 = {Disp("A1")}");
            doc.RenameSheet(0, "Budget");
            doc.SheetIndex = 1;
            log.AppendLine($"after rename: Sheet2!A1 raw={doc.Sheet.Get(0, 0)?.Raw} = {Disp("A1")}; tabs: {string.Join(", ", sheets.Tabs.Select(t => t.Name + (t.IsActive ? "*" : "")))}");
            doc.SheetIndex = 0;
            await sheets.FlushAsync();
            var saved = await store.FindAsync<Models.Spreadsheet>(sheets.Selected!.Id);
            var reloaded = Services.Sheets.Workbook.FromData(Services.Sheets.Workbook.ParseJson(saved!.Data));
            log.AppendLine($"saved: {saved.SheetCount} sheets, {saved.CellCount} cells; reloaded B6={reloaded.Find("Budget")?.Get(5, 1)?.Display} frozen={reloaded.Find("Budget")?.FrozenRows} summary='{saved.Summary}'");
            var xlsx = Path.Combine(dir, "export.xlsx");
            File.WriteAllBytes(xlsx, Services.Sheets.SpreadsheetIO.WriteXlsx(doc.Book));
            File.WriteAllBytes(Path.Combine(dir, "export.csv"), Services.Sheets.SpreadsheetIO.WriteCsv(doc.Sheet));
            log.AppendLine($"exported {new FileInfo(xlsx).Length} bytes");
        });

        await Step("import", async () =>
        {
            if (fixtures == null) return;
            await sheets.ImportFilesAsync([Path.Combine(fixtures, "budget.xlsx"), Path.Combine(fixtures, "scores.xls"), Path.Combine(fixtures, "prices.csv")]);
            await Task.Delay(1500);
            log.AppendLine($"imported: {string.Join(", ", sheets.Items.Take(3).Select(s => $"{s.Title} ({s.Summary})"))}");
            var budget = sheets.Items.First(s => s.FileName == "budget.xlsx");
            sheets.Selected = budget;
            await Task.Delay(1500);
            Shot("sheets-imported-xlsx");
            doc = sheets.Document!;
            log.AppendLine($"budget.xlsx: B6={Disp("B6")} C2={Disp("C2")} D6={Disp("D6")} frozen={doc.Sheet.FrozenRows} tabs={string.Join(",", sheets.Tabs.Select(t => t.Name))}");
            doc.SheetIndex = 1;
            await Task.Delay(700);
            Shot("sheets-lookups");
            doc.SheetIndex = 0;
        });

        await Step("search", async () =>
        {
            var palette = ActivatorUtilities.CreateInstance<CommandPaletteViewModel>(App.Services, shell, vm);
            palette.Query = "budget";
            await Task.Delay(1500);
            log.AppendLine($"palette 'budget': {string.Join(" | ", palette.Results.Take(4).Select(r => $"{r.Kind}:{r.Title}"))}");
            palette.Query = "new spreadsheet";
            log.AppendLine($"palette 'new spreadsheet': {string.Join(" | ", palette.Results.Take(2).Select(r => r.Title))}");
        });

        File.WriteAllText(Path.Combine(dir, "sheets.txt"), log.ToString());
    }

    // ----- key bindings: your own keys for tabs and for showing private items (Settings → Key bindings) -----

    private static async Task KeyBindingsTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var keys = App.Services.GetRequiredService<KeyBindingService>();
        var settingsService = App.Services.GetRequiredService<SettingsService>();
        var settings = settingsService.Current;
        var privacy = App.Services.GetRequiredService<PrivacyService>();
        var dialogs = App.Services.GetRequiredService<DialogService>();
        var shell = vm.Shell!;
        void Shot(string name) => Capture(window, Path.Combine(dir, name + ".png"));
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { log.AppendLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }
        static KeyChord K(string text) => KeyChord.Parse(text) ?? throw new ArgumentException("can't parse " + text);
        keys.ResetAll();

        await Step("chords", () =>
        {
            var texts = new[] { "Ctrl+Shift+B", "Alt+G", "F6", "Ctrl+Alt+←", "Alt+Num Plus", "Ctrl+Shift+=", "Alt+\\", "Ctrl+Shift+/", "Alt+Num 3", "Ctrl+F13", "Alt+PageDown" };
            log.AppendLine("round trip: " + string.Join(" | ", texts.Select(t => $"{t}→{KeyChord.Parse(t)?.ToString() ?? "null"}{(KeyChord.Parse(t)?.ToString() == t ? "" : " (DIFFERENT)")}")));
            log.AppendLine("bad text: " + string.Join(" | ", new[] { "", "Ctrl+", "Ctrl+Foo", "Hyper+B", "Ctrl+65", "Ctrl+#" }.Select(t => $"'{t}'→{KeyChord.Parse(t)?.ToString() ?? "null"}")));
            log.AppendLine($"ISO key: {KeyChord.From(WMods.Alt, WKey.OemBackslash)}; modifiers only: '{KeyChord.ModifierText(WMods.Control | WMods.Shift)}'");
            var probe = new[] { "Ctrl+K", "Ctrl+P", "Ctrl+B", "Ctrl+I", "Ctrl+Shift+C", "Ctrl+Shift+Tab", "Alt+3", "Alt+F4", "Ctrl+C", "Ctrl+Shift+→", "Ctrl+Shift+Space",
                "Ctrl+=", "Ctrl+0", "Ctrl+,", "Ctrl+/", "Ctrl+\\", "Ctrl+Shift+D", "Ctrl+1", "Ctrl+9", "Ctrl+Shift+H", "Ctrl+Shift+B", "Alt+B", "F6", "Ctrl+Shift+N", "Alt+Num 1" };
            log.AppendLine("taken: " + string.Join(" | ", probe.Select(p => $"{p}={KeyBindingService.TakenBy(K(p)) ?? "free"}")));
            var usable = new (WMods, WKey)[] { (WMods.None, WKey.B), (WMods.Shift, WKey.B), (WMods.None, WKey.F6), (WMods.None, WKey.F1), (WMods.Control, WKey.Tab),
                (WMods.Control, WKey.MediaPlayPause), (WMods.Alt, WKey.B), (WMods.Windows | WMods.Control, WKey.B), (WMods.Shift, WKey.F9) };
            log.AppendLine("usable: " + string.Join(" | ", usable.Select(x => $"{x.Item1}+{x.Item2}={new KeyChord(x.Item1, x.Item2).IsUsable}")));
            return Task.CompletedTask;
        });

        shell.NavigateTo("Settings", "Bindings");
        await Task.Delay(1800);
        // Showing / hiding private items reloads every page, so always ask the shell for the current Settings page.
        SettingsViewModel page = shell.GetPage<SettingsViewModel>("Settings")!;
        KeyBindingRow Row(string title) => (page = shell.GetPage<SettingsViewModel>("Settings") ?? page).KeyGroupsLeft.Concat(page.KeyGroupsRight).SelectMany(g => g.Rows).First(r => r.Title == title);
        string RowText(KeyBindingRow r) => $"{r.Title}: keys={r.Keys ?? "-"} sidebar={r.SidebarKeys ?? "-"}{(r.HasMessage ? $" msg='{r.Message}'" : "")}{(r.IsError ? " (error)" : "")}";

        await Step("settings", async () =>
        {
            log.AppendLine($"opened at: {page.ActiveSection}; left=[{string.Join(", ", page.KeyGroupsLeft.Select(g => $"{g.Title} {g.Rows.Count}"))}] right=[{string.Join(", ", page.KeyGroupsRight.Select(g => $"{g.Title} {g.Rows.Count}"))}]");
            log.AppendLine("before: " + RowText(Row("Dashboard")) + " || " + RowText(Row("Settings")) + " || " + RowText(page.PrivacyKey));
            Shot("bindings-start");

            page.CommitKey(Row("Board"), K("Ctrl+Shift+B"));
            log.AppendLine("set Board: " + RowText(Row("Board")));
            page.CommitKey(Row("Notes"), K("Ctrl+K"));
            log.AppendLine("Notes Ctrl+K: " + RowText(Row("Notes")));
            page.CommitKey(Row("Notes"), K("Ctrl+Shift+B"));
            log.AppendLine("Notes takes Ctrl+Shift+B: " + RowText(Row("Notes")) + " || " + RowText(Row("Board")));
            page.CommitKey(Row("Board"), K("Ctrl+1"));
            log.AppendLine("Board takes Ctrl+1: " + RowText(Row("Board")) + " || " + RowText(Row("Dashboard")));
            page.CommitKey(Row("Clock"), K("F6"));
            page.CommitKey(page.PrivacyKey, K("Alt+P"));
            log.AppendLine("privacy: " + RowText(page.PrivacyKey) + $" | Privacy section text: '{page.PrivacyKeyText}'");
            page.CommitKey(Row("Gamble"), K("Ctrl+Alt+G"));
            log.AppendLine("Gamble: " + RowText(Row("Gamble")));
            page.CommitKey(Row("Music"), new KeyChord(WMods.None, WKey.M));
            log.AppendLine("Music plain M: " + RowText(Row("Music")));
            log.AppendLine($"sidebar tooltips: {shell.FindNav("Dashboard")?.Tooltip} | {shell.FindNav("Today")?.Tooltip} | {shell.FindNav("Board")?.Tooltip} | {shell.FindNav("Notes")?.Tooltip} | {shell.FindNav("Clock")?.Tooltip} | {shell.SettingsItem.Tooltip}");
            log.AppendLine($"stored: {string.Join(", ", settings.KeyBindings.Select(p => p.Key + "=" + p.Value))}");
            await Task.Delay(500);
            Shot("bindings-set");

            // The box itself: click → listening → a plain letter is refused (keeps listening) → a function key is taken → click + Esc stops.
            var box = FindVisuals<System.Windows.Controls.Button>(window).FirstOrDefault(b => b.DataContext is KeyBindingRow { Title: "Games" } && b.ToolTip is string t && t.StartsWith("Click"));
            if (box == null) { log.AppendLine("box: not found"); return; }
            var games = Row("Games");
            bool Press(WKey key, System.Windows.RoutedEvent? routed = null)
            {
                var args = new WKeyEventArgs(WKeyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, key) { RoutedEvent = routed ?? WKeyboard.PreviewKeyDownEvent };
                box.RaiseEvent(args);
                return args.Handled;
            }
            box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, box));
            await Task.Delay(400);
            log.AppendLine($"box click: recording={games.IsRecording} text='{games.BoxText}' focused={ReferenceEquals(WKeyboard.FocusedElement, box)} recorder={ReferenceEquals(KeyBindingService.Recorder, box)}");
            Shot("bindings-listening");
            log.AppendLine($"press LeftShift alone: handled={Press(WKey.LeftShift)} recording={games.IsRecording} text='{games.BoxText}'");
            log.AppendLine($"press G: handled={Press(WKey.G)} recording={games.IsRecording} msg='{games.Message}' error={games.IsError}");
            await Task.Delay(250);
            Shot("bindings-refused");
            log.AppendLine($"press F7: handled={Press(WKey.F7)} recording={games.IsRecording} keys={games.Keys} recorder set={KeyBindingService.Recorder != null}");
            box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, box));
            await Task.Delay(150);
            log.AppendLine($"click again: recording={games.IsRecording}; press Esc: handled={Press(WKey.Escape)} recording={games.IsRecording} keys={games.Keys}");
            box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, box));
            await Task.Delay(150);
            log.AppendLine($"click, then Tab: handled={Press(WKey.Tab)} recording={games.IsRecording}");
        });

        await Step("run", async () =>
        {
            bool ran = shell.TryRunKeyBinding(K("Ctrl+1"));
            await Task.Delay(1300);
            log.AppendLine($"Ctrl+1: ran={ran} page={shell.SelectedItem?.Key}");
            ran = shell.TryRunKeyBinding(K("F6"));
            await Task.Delay(1100);
            log.AppendLine($"F6: ran={ran} page={shell.SelectedItem?.Key}");
            ran = shell.TryRunKeyBinding(K("Ctrl+Shift+B"));
            await Task.Delay(1100);
            log.AppendLine($"Ctrl+Shift+B: ran={ran} page={shell.SelectedItem?.Key}");
            ran = shell.TryRunKeyBinding(K("F7"));
            await Task.Delay(1100);
            log.AppendLine($"F7: ran={ran} page={shell.SelectedItem?.Key}");
            log.AppendLine($"Ctrl+Shift+H (free now): ran={shell.TryRunKeyBinding(K("Ctrl+Shift+H"))}; Alt+Q (unbound): ran={shell.TryRunKeyBinding(K("Alt+Q"))}");
            if (!settings.PrivateNeedsPassword)
            {
                bool before = privacy.ShowPrivate;
                ran = shell.TryRunKeyBinding(K("Alt+P"));
                await Task.Delay(700);
                log.AppendLine($"Alt+P: ran={ran} private shown {before}→{privacy.ShowPrivate}");
                await privacy.SetVisibleAsync(before);
            }
            else log.AppendLine("Alt+P: skipped (showing private items needs the password)");
        });

        await Step("lists", async () =>
        {
            var mine = Core.Shortcuts.All.FirstOrDefault(g => g.Title == "Your key bindings");
            log.AppendLine($"cheat sheet 'Your key bindings': {string.Join(" | ", mine?.Items.Select(i => $"{i.Keys} {i.Action}") ?? [])}");
            log.AppendLine($"cheat sheet privacy line: {Core.Shortcuts.All.SelectMany(g => g.Items).FirstOrDefault(i => i.Action.StartsWith("Show or hide private"))?.Keys}");
            var palette = ActivatorUtilities.CreateInstance<CommandPaletteViewModel>(App.Services, shell, vm);
            foreach (var q in new[] { "go to board", "go to clock", "private", "key bindings" })
            {
                palette.Query = q;
                log.AppendLine($"palette '{q}': {string.Join(" | ", palette.Results.Take(2).Select(r => $"{r.Title} [{r.Subtitle}]"))}");
            }
            _ = dialogs.ShowAsync(new ViewModels.Dialogs.ShortcutsDialogViewModel());
            await Task.Delay(1000);
            Shot("cheatsheet");
            dialogs.CloseTop();
            await Task.Delay(300);
        });

        await Step("save", async () =>
        {
            settingsService.SaveNow();
            var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(settingsService.GetValue("app.settings") ?? "{}");
            log.AppendLine($"saved: {string.Join(", ", reloaded?.KeyBindings.Select(p => p.Key + "=" + p.Value) ?? [])}");
            shell.NavigateTo("Settings", "Privacy");
            await Task.Delay(1300);
            Shot("privacy-section");
            page.ClearKeyCommand.Execute(page.PrivacyKey);
            log.AppendLine($"privacy cleared: keys={page.PrivacyKey.Keys ?? "none"} text='{page.PrivacyKeyText}' msg='{page.PrivacyKey.Message}' Ctrl+Shift+H runs={shell.TryRunKeyBinding(K("Ctrl+Shift+H"))} Alt+P runs={shell.TryRunKeyBinding(K("Alt+P"))}");
            // Hiding a tab: its row says so, and its keys still open it.
            settings.HiddenTabs = "Gamble";
            await Task.Delay(300);
            log.AppendLine($"Gamble hidden: row hidden={Row("Gamble").IsHidden} tooltip='{Row("Gamble").Tooltip}' Ctrl+Alt+G runs={shell.TryRunKeyBinding(K("Ctrl+Alt+G"))}");
            await Task.Delay(1100);
            log.AppendLine($"  → page={shell.SelectedItem?.Key}");
            shell.NavigateTo("Settings", "Bindings");
            await Task.Delay(1300);
            Shot("bindings-hidden-tab");
            settings.HiddenTabs = "";
            keys.ResetAll();
            await Task.Delay(300);
            log.AppendLine($"after reset: privacy={keys.Text(KeyBindingService.PrivacyAction)} stored={settings.KeyBindings.Count} rows: {RowText(Row("Board"))} || {RowText(Row("Dashboard"))}");
            log.AppendLine($"  tooltips: {shell.FindNav("Dashboard")?.Tooltip} | {shell.FindNav("Board")?.Tooltip}");
        });

        File.WriteAllText(Path.Combine(dir, "keys.txt"), log.ToString());
    }

    // ----- board upgrade: checklists in notes, the large editor, "Add from Satchel", private notes; collapsible sidebar; Gamble -----

    private static async Task BoardUpgradeTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var dialogs = App.Services.GetRequiredService<DialogService>();
        var privacy = App.Services.GetRequiredService<PrivacyService>();
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        var shell = vm.Shell!;
        void Shot(string name) => Capture(window, Path.Combine(dir, name + ".png"));
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { log.AppendLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        }

        await Step("sidebar", async () =>
        {
            settings.CollapsedSections = "Track,Media";
            shell.NavigateTo("Gamble");
            await Task.Delay(1300);
            var track = shell.NavRows.OfType<NavSection>().FirstOrDefault(s => s.Title == "Track");
            log.AppendLine($"sidebar: Track collapsed={track?.IsCollapsed} hidden={track?.HiddenCount}; Games folded={shell.FindNav("Games")?.IsFoldedAway}; saved='{settings.CollapsedSections}'");
            if (FindVisuals<System.Windows.Controls.ScrollViewer>(window).FirstOrDefault(s => s.Name == "NavScroll") is { } nav) nav.ScrollToEnd();
            await Task.Delay(300);
            Shot("gamble-sidebar");
            shell.NavigateTo("Games");
            await Task.Delay(1100);
            log.AppendLine($"open Games inside the collapsed section: Games folded={shell.FindNav("Games")?.IsFoldedAway}, Reading folded={shell.FindNav("Reading")?.IsFoldedAway}, Track hidden={track?.HiddenCount}");
            if (FindVisuals<System.Windows.Controls.ScrollViewer>(window).FirstOrDefault(s => s.Name == "NavScroll") is { } nav2) nav2.ScrollToEnd();
            await Task.Delay(300);
            Shot("sidebar-open-in-collapsed");
            settings.SidebarCollapsed = true;
            await Task.Delay(700);
            Shot("sidebar-icons-collapsed-sections");
            settings.SidebarCollapsed = false;
            await Task.Delay(500);
            shell.ToggleSectionCommand.Execute(track);
            await Task.Delay(300);
            log.AppendLine($"click Track: collapsed={track?.IsCollapsed}, saved='{settings.CollapsedSections}'");
            settings.CollapsedSections = "";
        });

        shell.NavigateTo("Board");
        await Task.Delay(1600);
        var board = shell.GetPage<BoardViewModel>("Board")!;
        Models.StickyNote? note = null;

        await Step("checklist", async () =>
        {
            note = await board.AddStickyAt(board.ViewCenter);
            await Task.Delay(800);
            var editor = FindVisuals<Controls.RichTextEditor>(window).FirstOrDefault(e => e.DataContext == note);
            if (editor == null) { log.AppendLine("checklist: editor not found"); return; }
            var rtb = editor.Editor;
            rtb.Focus();
            void Type(string text)
            {
                rtb.CaretPosition.InsertTextInRun(text);
                rtb.CaretPosition = rtb.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward);
            }
            bool Press(System.Windows.Input.Key key)
            {
                var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(rtb)!, 0, key)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                rtb.RaiseEvent(args);
                return args.Handled;
            }
            Type("Buy milk");
            editor.ToggleChecklist();
            rtb.CaretPosition = rtb.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward);
            bool enter1 = Press(System.Windows.Input.Key.Enter);
            Type("Bread");
            Press(System.Windows.Input.Key.Enter);
            bool tab = Press(System.Windows.Input.Key.Tab);
            Type("Sourdough");
            Press(System.Windows.Input.Key.Enter);
            bool endList = Press(System.Windows.Input.Key.Enter);
            Type("A plain line after the list");
            var first = rtb.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().First();
            editor.ToggleCheck(first);
            editor.Flush();
            await Task.Delay(300);
            log.AppendLine($"checklist: enter handled={enter1} tab={tab} end-list={endList}; items=[{string.Join(", ", editor.ChecklistItems().Select(i => (i.Done ? "x " : "o ") + i.Text))}]");
            log.AppendLine($"checklist plain text: '{note!.Text.Replace("\r", "").Replace("\n", " / ")}'");
            Shot("board-checklist");
            // The same note in the large editor
            var large = board.OpenStickyEditorCommand.ExecuteAsync(note);
            await Task.Delay(1000);
            Shot("board-note-large");
            dialogs.CloseTop();
            await large;
        });

        await Step("insert", async () =>
        {
            var dlg = new ViewModels.Dialogs.BoardInsertDialogViewModel(store);
            var shown = dialogs.ShowAsync(dlg);
            await Task.Delay(1200);
            foreach (var key in new[] { "note", "photo", "video", "game", "book", "show", "bookmark", "tasks" })
            {
                dlg.SelectSourceCommand.Execute(dlg.Sources.First(s => s.Key == key));
                for (int i = 0; i < 40 && dlg.IsLoading; i++) await Task.Delay(100);
                var item = key == "note" ? dlg.Items.FirstOrDefault(x => x.Subtitle.StartsWith("Checklist")) ?? dlg.Items.FirstOrDefault() : dlg.Items.FirstOrDefault();
                if (item != null) dlg.ToggleCommand.Execute(item);
                log.AppendLine($"insert {key}: {dlg.Items.Count} shown of {dlg.Sources.First(s => s.Key == key).Count}; picked '{item?.Title}'");
            }
            dlg.Query = "zzz-nothing";
            await Task.Delay(200);
            log.AppendLine($"search with no match: empty={dlg.IsEmpty} '{dlg.EmptyText}'");
            dlg.Query = "";
            dlg.SelectSourceCommand.Execute(dlg.Sources.First(s => s.Key == "game"));
            for (int i = 0; i < 40 && dlg.IsLoading; i++) await Task.Delay(100);
            await Task.Delay(500);
            log.AppendLine($"picked: {dlg.PickedText} → '{dlg.AddText}'");
            Shot("board-insert-dialog");
            var picked = dlg.Picked.ToList();
            dialogs.CloseTop();
            await shown;
            int before = board.Stickies.Count;
            int added = await board.InsertItemsAsync(picked);
            await Task.Delay(1800);
            var fresh = board.Stickies.Skip(before).ToList();
            log.AppendLine($"inserted {added} of {picked.Count}: {string.Join(", ", fresh.Select(s => s.IsLive ? $"live:{s.LiveKind}({(s.Live as LiveCardData)?.Title})" : s.IsLink ? $"link({s.Caption})" : s.IsImage ? "image" : "note"))}");
            if (fresh.FirstOrDefault(s => s.IsText) is { } copy) log.AppendLine($"note copy: '{copy.Text.Replace("\r", "").Replace("\n", " / ")}' color={copy.Color} {copy.Width}x{copy.Height}");
            Shot("board-inserted");
        });

        await Step("private", async () =>
        {
            if (note == null) return;
            var other = board.Stickies.FirstOrDefault(s => !ReferenceEquals(s, note) && s.IsText);
            if (other == null) { log.AppendLine("private: no second note"); return; }
            await board.ConnectAsync(note, other);
            int linksBefore = board.Links.Count;
            int dbBefore = await store.RunAsync(db => db.BoardLinks.Count());
            await privacy.SetAsync(note, true);
            await Task.Delay(1800);
            board = shell.GetPage<BoardViewModel>("Board")!;
            int dbHidden = await store.RunAsync(db => db.BoardLinks.Count());
            log.AppendLine($"private note: on board={board.Stickies.Any(s => s.Id == note.Id)}, links shown {linksBefore} → {board.Links.Count}, links in the database {dbBefore} → {dbHidden}");
            await privacy.SetVisibleAsync(true);
            await Task.Delay(1800);
            board = shell.GetPage<BoardViewModel>("Board")!;
            log.AppendLine($"private items shown: on board={board.Stickies.Any(s => s.Id == note.Id)}, links shown={board.Links.Count}");
            Shot("board-private-shown");
            await privacy.SetVisibleAsync(false);
            await Task.Delay(800);
        });

        await Step("notes", async () =>
        {
            var notes = await store.ListAsync<Models.Note>(q => q.Where(n => n.Type == Models.NoteType.Text && !n.IsTrashed));
            if (notes.FirstOrDefault() is not { } text) return;
            shell.NavigateTo("Notes", text.Id);
            await Task.Delay(1500);
            var editor = FindVisuals<Controls.RichTextEditor>(window).FirstOrDefault(e => e.IsVisible);
            if (editor == null) { log.AppendLine("notes: editor not found"); return; }
            var rtb = editor.Editor;
            rtb.Focus();
            rtb.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("Pack the charger")));
            rtb.CaretPosition = rtb.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward);
            editor.ToggleChecklist();
            await Task.Delay(500);
            log.AppendLine($"notes editor checklist items: {editor.ChecklistItems().Count}");
            Shot("notes-checklist");
        });

        File.WriteAllText(Path.Combine(dir, "board.txt"), log.ToString());
    }

    // ----- board: live cards, timestamp notes, study panel -----

    private static async Task BoardNewTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var board = new Models.Board { Name = "Study board", Style = "cork", SortOrder = 900 };
        await store.AddAsync(board);
        var card = new Models.StickyNote
        {
            BoardId = board.Id, Kind = Models.StickyKind.Link, Url = Core.YouTube.WatchUrl("aqz-KE-bpKQ"), Caption = "Big Buck Bunny", Subtitle = "Blender", Color = "white",
            X = 60, Y = 60, Width = 300, Height = 252, ZIndex = 1
        };
        await store.AddAsync(card);
        var lists = await store.ListAsync<Models.TaskList>();
        await store.AddRangeAsync(new[]
        {
            new Models.StickyNote { BoardId = board.Id, Kind = Models.StickyKind.Live, LiveKind = "tasks", LiveRef = "today", Color = "white", X = 60, Y = 380, Width = 260, Height = 300, ZIndex = 2 },
            new Models.StickyNote { BoardId = board.Id, Kind = Models.StickyKind.Live, LiveKind = "clock", LiveRef = "Tokyo Standard Time", Color = "white", X = 360, Y = 420, Width = 200, Height = 230, ZIndex = 3 },
            new Models.StickyNote { BoardId = board.Id, Kind = Models.StickyKind.Live, LiveKind = "music", Color = "white", X = 600, Y = 440, Width = 280, Height = 120, ZIndex = 4 },
            new Models.StickyNote { BoardId = board.Id, Kind = Models.StickyKind.Text, Text = "0:42 — The bunny wakes up", Content = BoardViewModel.PlainToRich("The bunny wakes up"),
                                    Url = Core.YouTube.WatchUrl("aqz-KE-bpKQ", 42), Color = "yellow", X = 420, Y = 80, Width = 220, Height = 170, ZIndex = 5 },
        });
        var countdowns = await store.ListAsync<Models.Countdown>();
        if (countdowns.Count > 0)
            await store.AddAsync(new Models.StickyNote { BoardId = board.Id, Kind = Models.StickyKind.Live, LiveKind = "countdown", LiveRef = countdowns[0].Id.ToString(), Color = "white",
                                                         X = 680, Y = 70, Width = 220, Height = 170, ZIndex = 6 });
        var boards = vm.Shell?.GetPage<BoardViewModel>("Board");
        vm.Shell?.NavigateTo("Board", board.Id);
        await Task.Delay(1800);
        boards = vm.Shell?.GetPage<BoardViewModel>("Board");
        if (boards != null)
        {
            if (boards.SelectedBoard?.Id != board.Id) { await boards.ReloadBoardsAsync(board.Id); await Task.Delay(1500); }
            log.AppendLine($"stickies={boards.Stickies.Count} live={boards.Stickies.Count(s => s.IsLive)} withData={boards.Stickies.Count(s => s.Live != null)} hasVideo={boards.HasVideoCards}");
            Capture(window, Path.Combine(dir, "board-live.png"));
            boards.ToggleStudyCommand.Execute(null);
            await Task.Delay(9000);
            log.AppendLine($"study open={boards.IsStudyOpen} video={boards.StudyVideoId} time={boards.StudyTime:0.0}");
            boards.StudyNote = "Clouds roll in";
            await boards.AddStudyNoteCommand.ExecuteAsync(null);
            await Task.Delay(800);
            var timed = boards.Stickies.Where(s => s.HasTimeLink).Select(s => $"{s.TimeText}:{s.Text}");
            log.AppendLine("time notes: " + string.Join(" | ", timed));
            log.AppendLine($"links={boards.Links.Count}");
            var chip = boards.Stickies.First(s => s.HasTimeLink && s.TimeSeconds == 42);
            boards.PlayTimeLinkCommand.Execute(chip);
            await Task.Delay(2500);
            log.AppendLine($"after chip 0:42 -> time={boards.StudyTime:0.0}");
            Capture(window, Path.Combine(dir, "board-study.png"));
            var player = FindVisuals<Controls.YouTubePlayer>(window).FirstOrDefault(p => p.Name == "StudyPlayer");
            if (player?.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().FirstOrDefault() is { CoreWebView2: { } core })
            {
                using var fs = File.Create(Path.Combine(dir, "board-study-video.png"));
                await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
        }
        File.WriteAllText(Path.Combine(dir, "board.txt"), log.ToString());
    }

    // ----- seed data for the 1.2 modules -----

    private static async Task SeedNewModulesAsync()
    {
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        var timers = App.Services.GetRequiredService<TimerService>();
        if (timers.Timers.Count == 0)
        {
            timers.Start(25 * 60, "Focus");
            var tea = timers.Start(4 * 60, "Tea");
            timers.Pause(tea);
        }
        if (!timers.StopwatchRunning && timers.StopwatchSeconds == 0)
        {
            settings.Stopwatch.ElapsedBefore = 83.4;
            settings.Stopwatch.Laps = [21.3, 44.9, 63.0];
            timers.ToggleStopwatch();
        }
        if (settings.WorldClocks.Count == 0)
            settings.WorldClocks = [new WorldClock { ZoneId = "Tokyo Standard Time", Label = "Tokyo" }, new WorldClock { ZoneId = "Eastern Standard Time", Label = "New York" },
                                    new WorldClock { ZoneId = "GMT Standard Time", Label = "London" }];
        if (await store.CountAsync<Models.Countdown>() == 0)
            await store.AddRangeAsync(new[]
            {
                new Models.Countdown { Title = "Trip to Lisbon", Date = DateTime.Today.AddDays(23), Emoji = "✈️", Color = "sky" },
                new Models.Countdown { Title = "Mum's birthday", Date = DateTime.Today.AddDays(9).AddYears(-40), Emoji = "🎂", Color = "rose", Yearly = true },
                new Models.Countdown { Title = "Release 2.0", Date = DateTime.Today.AddDays(61), Color = "green" },
            });
        if (await store.CountAsync<Models.Bookmark>() == 0)
            await store.AddRangeAsync(new[]
            {
                new Models.Bookmark { Url = "https://learn.microsoft.com/dotnet/desktop/wpf/", Title = "WPF documentation", Description = "Windows Presentation Foundation guides and API reference.", Folder = "Dev", IsFavorite = true },
                new Models.Bookmark { Url = "https://www.openstreetmap.org", Title = "OpenStreetMap", Description = "The free wiki world map.", Folder = "Maps" },
                new Models.Bookmark { Url = "https://archive.org", Title = "Internet Archive", Description = "Universal access to all knowledge.", IsReadLater = true },
                new Models.Bookmark { Url = "https://github.com", Title = "GitHub", Folder = "Dev", Color = "teal" },
            });
        if (await store.CountAsync<Models.Subscription>() == 0)
            await store.AddRangeAsync(new[]
            {
                new Models.Subscription { Name = "Netflix", AmountText = "13.99", Category = "Streaming", NextDue = DateTime.Today.AddDays(3), Color = "red" },
                new Models.Subscription { Name = "Spotify", AmountText = "10.99", Category = "Music", NextDue = DateTime.Today.AddDays(12), Color = "green" },
                new Models.Subscription { Name = "Rent", AmountText = "750", Category = "Rent", Kind = Models.MoneyKind.Bill, NextDue = DateTime.Today.AddDays(20), Color = "amber" },
                new Models.Subscription { Name = "iCloud+", AmountText = "2.99", Category = "Cloud", NextDue = DateTime.Today.AddDays(-1), Color = "sky" },
                new Models.Subscription { Name = "JetBrains", AmountText = "169", Category = "Software", Cycle = Models.BillingCycle.Yearly, NextDue = DateTime.Today.AddDays(80), Color = "orange" },
            });
        if (await store.CountAsync<Models.WatchItem>() == 0)
        {
            var eps = Enumerable.Range(1, 8).Select(i => new Models.Episode { Season = 1, Number = i, Title = $"Chapter {i}", Airdate = DateTime.Today.AddDays(-60 + i * 7).ToString("yyyy-MM-dd"), Watched = i <= 3, WatchedAt = i <= 3 ? DateTime.Now.AddDays(-3 + i) : null }).ToList();
            await store.AddRangeAsync(new[]
            {
                new Models.WatchItem { Title = "The Long Road", Kind = Models.WatchKind.Show, Status = Models.WatchStatus.Watching, Year = 2025, Network = "Netflix", Genres = "Drama", EpisodesJson = System.Text.Json.JsonSerializer.Serialize(eps), Color = "sky" },
                new Models.WatchItem { Title = "Night Train", Kind = Models.WatchKind.Movie, Status = Models.WatchStatus.Planned, Year = 2024, Runtime = 112, Color = "amber" },
                new Models.WatchItem { Title = "Old Friends", Kind = Models.WatchKind.Movie, Status = Models.WatchStatus.Completed, Year = 2019, Rating = 4, FinishedAt = DateTime.Today.AddDays(-5), Color = "rose" },
            });
        }
        if (await store.CountAsync<Models.Deck>() == 0)
        {
            var deck = new Models.Deck { Name = "Spanish basics", Color = "orange" };
            await store.AddAsync(deck);
            await store.AddRangeAsync(new[] { ("hola", "hello"), ("gracias", "thank you"), ("la ventana", "the window"), ("el perro", "the dog"), ("mañana", "tomorrow") }
                .Select((p, i) => new Models.Flashcard { DeckId = deck.Id, Front = p.Item1, Back = p.Item2, SortOrder = i, State = i < 2 ? Models.CardState.Review : Models.CardState.New,
                                                         DueAt = i < 2 ? DateTime.Now.AddHours(-1) : null, IntervalDays = i < 2 ? 3 : 0 }));
            await store.AddRangeAsync(Enumerable.Range(0, 20).Select(i => new Models.ReviewLog { DeckId = deck.Id, CardId = 1, ReviewedAt = DateTime.Today.AddDays(-i % 9).AddHours(10), Grade = i % 4 == 0 ? 1 : 3 }));
        }
        if (await store.CountAsync<Models.Tracker>() == 0)
        {
            var template = CollectionsViewModel.Templates.First(t => t.Name == "Wine cellar");
            var tracker = new Models.Tracker { Name = template.Name, Icon = template.Icon, Color = template.Color };
            tracker.SaveFields(template.Fields);
            await store.AddAsync(tracker);
            var fields = tracker.Fields;
            var a = new Models.TrackerItem { TrackerId = tracker.Id, Title = "Mavrud Reserve" };
            a.Set(fields[0].Id, "Villa Melnik"); a.Set(fields[1].Id, "2019"); a.Set(fields[2].Id, "Red"); a.Set(fields[4].Id, "4");
            var b2 = new Models.TrackerItem { TrackerId = tracker.Id, Title = "Rkatsiteli" };
            b2.Set(fields[0].Id, "Edoardo Miroglio"); b2.Set(fields[1].Id, "2022"); b2.Set(fields[2].Id, "White"); b2.Set(fields[4].Id, "3");
            await store.AddRangeAsync(new[] { a, b2 });
        }
        if (await store.CountAsync<Models.Photo>() == 0)
        {
            var photos = new List<Models.Photo>();
            for (int i = 0; i < 9; i++)
            {
                var bytes = SampleImage(16 + i, 9 + (i % 3) * 2);
                var p = PhotoImporter.FromBytes(bytes, i == 2 ? "Screenshot 2026-11-01.png" : $"IMG_{2000 + i}.jpg", DateTime.Today.AddDays(-i * 9));
                if (p != null) { p.IsFavorite = i % 4 == 0; photos.Add(p); }
            }
            await store.AddRangeAsync(photos);
        }
        // Journal moods for Insights.
        if (await store.CountAsync<Models.Note>(q => q.Where(n => n.Type == Models.NoteType.Journal && n.Mood > 0)) == 0)
            await store.AddRangeAsync(Enumerable.Range(1, 20).Select(i => new Models.Note
            {
                Type = Models.NoteType.Journal, JournalDate = DateTime.Today.AddDays(-i * 2), Mood = 2 + (i * 7 % 4), Title = "", Content = "", PlainText = "", CreatedAt = DateTime.Today.AddDays(-i * 2).AddHours(21)
            }));

        // A day with things in the auto-diary.
        if (!(await store.ListAsync<Models.TaskItem>()).Any(t => t.Title == "Water the plants"))
        {
            var done = new Models.TaskItem { Title = "Send the invoice", DueDate = DateTime.Today, State = Models.TaskState.Done, CompletedAt = DateTime.Today.AddHours(9.5) };
            await store.AddRangeAsync(new[]
            {
                new Models.TaskItem { Title = "Water the plants", DueDate = DateTime.Today, Repeat = "weekly" },
                new Models.TaskItem { Title = "Book the dentist", DueDate = DateTime.Today.AddDays(-1), Priority = Models.TaskPriority.High },
                done
            });
            await store.AddAsync(new Models.Note { Title = "Ideas for the garden", PlainText = "Raised beds, herbs by the kitchen door.", Content = "", CreatedAt = DateTime.Today.AddHours(11) });
            await store.AddAsync(new Models.Note { Type = Models.NoteType.Journal, JournalDate = DateTime.Today.AddYears(-1), Title = "", PlainText = "Hiked to the lake. Cold water, great day.",
                                                   Content = "", CreatedAt = DateTime.Today.AddYears(-1) });
        }
    }

    // ----- background music: plays, keeps playing hidden and across pages -----

    private static async Task MusicTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var music = App.Services.GetRequiredService<MusicService>();
        var playlist = new Models.MusicPlaylist { Name = "Test mix" };
        await store.AddAsync(playlist);
        var track = await music.FetchTrackAsync("aqz-KE-bpKQ");
        track.PlaylistId = playlist.Id;
        await store.AddAsync(track);
        log.AppendLine($"fetched: title='{track.Title}' artist='{track.Artist}' thumb={track.Thumbnail?.Length ?? 0} bytes");
        vm.Shell?.NavigateTo("Music");
        await Task.Delay(1500);
        music.PlayList([track], track, "Test mix");
        await Task.Delay(9000);
        log.AppendLine($"after 9s (page, cover): playing={music.IsPlaying} buffering={music.IsBuffering} pos={music.Position:0.0} dur={music.Duration:0.0}");
        var page = vm.Shell?.GetPage<MusicViewModel>("Music");
        if (page != null) page.ShowVideo = true;
        await Task.Delay(2500);
        Capture(window, Path.Combine(dir, "music-page.png"));
        if (MainWindow.MusicHost?.Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().FirstOrDefault() is { CoreWebView2: { } core })
        {
            using var fs = File.Create(Path.Combine(dir, "music-video.png"));
            await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        if (page != null) page.ShowVideo = false;
        var before = music.Position;
        vm.Shell?.NavigateTo("Dashboard");
        await Task.Delay(7000);
        log.AppendLine($"hidden on Dashboard 7s later: playing={music.IsPlaying} pos {before:0.0} -> {music.Position:0.0}");
        Capture(window, Path.Combine(dir, "music-pill.png"));
        music.PlayPause();
        await Task.Delay(1500);
        var paused = music.Position;
        await Task.Delay(2000);
        log.AppendLine($"paused: playing={music.IsPlaying} pos {paused:0.0} -> {music.Position:0.0}");
        music.Stop();
        var saved = await store.FindAsync<Models.MusicTrack>(track.Id);
        log.AppendLine($"saved duration={saved?.Duration}");
        File.WriteAllText(Path.Combine(dir, "music.txt"), log.ToString());
    }

    // ----- private flag: query filters for items and their children -----

    private static async Task PrivacyTestAsync(string file)
    {
        var log = new System.Text.StringBuilder();
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var privacy = App.Services.GetRequiredService<PrivacyService>();
        await privacy.SetVisibleAsync(true);
        var tag = $"pt{DateTime.Now:HHmmss}";
        var book = new Models.Notebook { Name = tag + "-book" };
        await store.AddAsync(book);
        var inBook = new Models.Note { Title = tag + "-in-private-book", NotebookId = book.Id };
        var own = new Models.Note { Title = tag + "-own-private" };
        var open = new Models.Note { Title = tag + "-open" };
        await store.AddRangeAsync([inBook, own, open]);
        var profile = new Models.VaultProfile { Name = tag + "-profile" };
        await store.AddAsync(profile);
        await store.AddAsync(new Models.VaultAccount { ProfileId = profile.Id, Label = tag + "-account" });
        var board = new Models.Board { Name = tag + "-board" };
        await store.AddAsync(board);
        await store.AddAsync(new Models.StickyNote { BoardId = board.Id, Text = tag + "-sticky" });
        await privacy.SetAsync(book, true, quiet: true);
        await privacy.SetAsync(own, true, quiet: true);
        await privacy.SetAsync(profile, true, quiet: true);
        await privacy.SetAsync(board, true, quiet: true);

        async Task Report(string when)
        {
            var notes = await store.ListAsync<Models.Note>(q => q.Where(n => n.CreatedAt > DateTime.Now.AddMinutes(-5)));
            var accounts = await store.ListAsync<Models.VaultAccount>(q => q.Where(a => a.ProfileId == profile.Id));
            var stickies = await store.ListAsync<Models.StickyNote>(q => q.Where(s => s.BoardId == board.Id));
            var search = await App.Services.GetRequiredService<SearchService>().BuildIndexAsync();
            log.AppendLine($"{when}: notes=[{string.Join(", ", notes.Where(n => n.Title.StartsWith(tag)).Select(n => n.Title[(tag.Length + 1)..]))}] " +
                           $"accounts={accounts.Count} stickies={stickies.Count} search-hits={search.Count(r => r.Haystack.Contains(tag) || r.Title.Contains(tag))}");
        }

        await Report("shown");
        await privacy.SetVisibleAsync(false);
        await Task.Delay(300);
        await Report("hidden");
        await privacy.SetVisibleAsync(true);
        await Task.Delay(300);
        await Report("shown again");
        await privacy.SetVisibleAsync(false);
        File.WriteAllText(file, log.ToString());
    }

    // ----- smoke test of the newer modules: drives their view models, checks the database (no screenshots needed) -----
    private static async Task ModulesTestAsync(MainWindowViewModel vm, string file)
    {
        var log = new System.Text.StringBuilder();
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var shell = vm.Shell!;
        var shotDir = Path.GetDirectoryName(Path.GetFullPath(file))!;

        // Screenshots of each page in a meaningful state (blank while Windows is locked — see README).
        async Task Shot(string name)
        {
            await Task.Delay(800);
            if (Application.Current.MainWindow is { } window) Capture(window, Path.Combine(shotDir, name + ".png"));
        }

        async Task Step(string name, Func<Task<string>> body)
        {
            try { log.AppendLine($"{name}: {await body()}"); }
            catch (Exception ex) { log.AppendLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
            File.WriteAllText(file, log.ToString());
        }

        async Task<T> Open<T>(string key, object? parameter = null) where T : PageViewModel
        {
            shell.NavigateTo(key, parameter);
            for (int i = 0; i < 60 && shell.CurrentPage is not T; i++) await Task.Delay(100);
            await Task.Delay(600);
            return (T)shell.CurrentPage!;
        }

        await Step("search", async () =>
        {
            var index = await App.Services.GetRequiredService<SearchService>().BuildIndexAsync();
            var parts = new List<string> { $"{index.Count} entries" };
            foreach (var q in new[] { "Netflix", "hola", "Villa Melnik", "Long Road", "OpenStreetMap", "IMG_2003", "BBC", "Spanish" })
            {
                var top = SearchService.Filter(index, q, 3).FirstOrDefault();
                parts.Add($"'{q}'→{(top == null ? "none" : top.Kind + ":" + top.Title)}");
            }
            return string.Join(" | ", parts);
        });

        await Step("palette", async () =>
        {
            vm.OpenPalette();
            var palette = vm.Palette!;
            for (int i = 0; i < 50 && palette.IsIndexing; i++) await Task.Delay(100);
            string Probe(string q)
            {
                palette.Query = q;
                return $"'{q}'→[{string.Join(", ", palette.Results.Take(3).Select(r => r.Kind + ":" + r.Title))}]";
            }
            var timer = Probe("timer");
            var insightsProbe = Probe("insights");
            var netflix = Probe("netflix");
            await Shot("palette");
            var result = string.Join(" | ", timer, insightsProbe, netflix, Probe("private"));
            vm.Palette = null;
            return result;
        });

        await Step("money", async () =>
        {
            var money = await Open<MoneyViewModel>("Money");
            var s = money.Items.First(x => x.Name == "Netflix");
            var before = s.NextDue;
            await money.MarkPaidCommand.ExecuteAsync(s);
            var saved = (await store.ListAsync<Models.Subscription>()).First(x => x.Id == s.Id);
            var payments = await store.CountAsync<Models.Payment>();
            await Shot("money");
            return $"Netflix due {before:dd.MM} → {saved.NextDue:dd.MM} (screen {s.NextDue:dd.MM}), payments={payments}, upcoming={money.Upcoming.Count}, totals={money.Totals.Count}, categories={money.Categories.Count}";
        });

        await Step("watching", async () =>
        {
            var watching = await Open<WatchingViewModel>("Watching");
            var show = watching.All.First(w => w.Title == "The Long Road");
            int before = show.Episodes.Count(e => e.Watched);
            await watching.WatchedNextCommand.ExecuteAsync(show);
            var saved = (await store.ListAsync<Models.WatchItem>()).First(w => w.Id == show.Id);
            await Shot("watching");
            return $"watched {before} → {show.Episodes.Count(e => e.Watched)} (db {saved.Episodes.Count(e => e.Watched)}), next={show.NextEpisode?.Code}, " +
                   $"watching={watching.Watching.Count} planned={watching.Planned.Count} completed={watching.Completed.Count}";
        });

        await Step("flashcards", async () =>
        {
            var cards = await Open<FlashcardsViewModel>("Flashcards");
            var deck = cards.Decks.First(d => d.Name == "Spanish basics");
            cards.SelectedDeck = deck;
            int logsBefore = await store.CountAsync<Models.ReviewLog>();
            cards.StartStudyCommand.Execute(null);
            var seen = new List<string>();
            for (int i = 0; i < 12 && cards.IsStudying && cards.Current != null && !cards.IsFinished; i++)
            {
                seen.Add(cards.Current.Front);
                cards.FlipCommand.Execute(null);
                if (seen.Count == 1) await Shot("flashcards-study");
                await cards.AnswerCommand.ExecuteAsync(i % 3 == 0 ? "1" : "3");
                await Task.Delay(50);
            }
            int logsAfter = await store.CountAsync<Models.ReviewLog>();
            var saved = await store.ListAsync<Models.Flashcard>(q => q.Where(c => c.DeckId == deck.Id));
            return $"answered {seen.Count} [{string.Join(",", seen)}], logs {logsBefore}→{logsAfter}, finished={cards.IsFinished}, " +
                   $"cards=[{string.Join(", ", saved.Select(c => $"{c.Front}:{c.State}/{c.IntervalDays}d"))}]";
        });

        await Step("photos", async () =>
        {
            var photos = await Open<PhotosViewModel>("Photos");
            int before = photos.Visible.Count;
            await photos.ImportImagesAsync([SampleImage(4, 3), SampleImage(16, 9)]);
            int after = photos.Visible.Count;
            await Shot("photos");
            var first = photos.Visible.First();
            bool wasFavorite = first.IsFavorite;
            await photos.ToggleFavoriteCommand.ExecuteAsync(first);
            var saved = (await store.ListAsync<Models.Photo>(q => q.Where(p => p.Id == first.Id))).First();
            photos.Viewing = first;
            for (int i = 0; i < 40 && !(photos.ViewImage is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 420 }); i++) await Task.Delay(100);
            var view = photos.ViewImage is System.Windows.Media.Imaging.BitmapSource bs ? $"{bs.PixelWidth}x{bs.PixelHeight}" : "none";
            await Shot("photos-viewer");
            photos.Viewing = null;
            return $"visible {before} → {after}, groups={photos.Groups.Count}, favorite {wasFavorite}→{first.IsFavorite} (db {saved.IsFavorite}), viewer image={view}";
        });

        await Step("collections", async () =>
        {
            var col = await Open<CollectionsViewModel>("Collections");
            col.SelectedTracker = col.Trackers.First(t => t.Name == "Wine cellar");
            await Task.Delay(200);
            col.Selected = col.Items.FirstOrDefault();
            await Shot("collections");
            return $"trackers={col.Trackers.Count}, items={col.Items.Count}, fields={col.Fields.Count}, columns={col.Columns.Count}, rows={col.Rows.Count}";
        });

        await Step("bookmarks", async () =>
        {
            var html = "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p><DT><H3>Dev</H3><DL><p><DT><A HREF=\"https://dotnet.microsoft.com\" ADD_DATE=\"1700000000\">.NET</A></DL><p>" +
                       "<DT><A HREF=\"https://example.org\">Example</A></DL>";
            var json = "{\"roots\":{\"bookmark_bar\":{\"children\":[{\"type\":\"url\",\"name\":\"Wiki\",\"url\":\"https://wikipedia.org\"}," +
                       "{\"type\":\"folder\",\"name\":\"News\",\"children\":[{\"type\":\"url\",\"name\":\"BBC\",\"url\":\"https://bbc.co.uk\"}]}]}}}";
            var netscape = BookmarksViewModel.ParseNetscape(html);
            var chromium = BookmarksViewModel.ParseChromium(json);
            var bm = await Open<BookmarksViewModel>("Bookmarks");
            await Shot("bookmarks");
            return $"netscape=[{string.Join(", ", netscape.Select(p => $"{p.Title}@{p.Folder}"))}] chromium=[{string.Join(", ", chromium.Select(p => $"{p.Title}@{p.Folder}"))}] " +
                   $"visible={bm.Visible.Count} folders={bm.Folders.Count}";
        });

        await Step("feeds", async () =>
        {
            var feeds = await Open<FeedsViewModel>("Feeds");
            feeds.SelectFilterCommand.Execute(feeds.AllFilter);
            feeds.Selected = feeds.Items.FirstOrDefault(i => !i.IsAudio);
            int reader = feeds.ReaderText.Length;
            await Shot("feeds");
            var episode = feeds.Items.FirstOrDefault(i => i.IsAudio);
            var audio = "no episode";
            if (episode != null)
            {
                feeds.Podcast.Volume = 0;
                feeds.PlayEpisodeCommand.Execute(episode);
                for (int i = 0; i < 150 && feeds.Podcast.Position < 2 && feeds.Podcast.LastError.Length == 0; i++) await Task.Delay(100);
                await Shot("feeds-podcast");
                audio = $"episode position={feeds.Podcast.Position:0.0}s duration={feeds.Podcast.Duration:0}s playing={feeds.Podcast.IsPlaying} error='{feeds.Podcast.LastError}'";
                feeds.Podcast.TogglePlay();
                await Task.Delay(800);
                var saved = (await store.ListAsync<Models.FeedItem>(q => q.Where(i => i.Id == episode.Id))).First();
                audio += $" | after pause: playing={feeds.Podcast.IsPlaying} saved position={saved.Position:0.0}s";
                feeds.Podcast.Stop();
            }
            return $"feeds={feeds.Feeds.Count} items={feeds.Items.Count} unread={feeds.UnreadFilter.Count} reader={reader} chars | {audio}";
        });

        await Step("steam", async () =>
        {
            var games = await Open<GamesViewModel>("Games");
            await games.OpenSteamCommand.ExecuteAsync(null);
            await Task.Delay(500);
            await Shot("games-steam");
            var state = $"overlay open={games.IsSteamOpen}";
            games.CloseSteamCommand.Execute(null);
            return state;
        });

        await Step("reminders", async () =>
        {
            var reminders = App.Services.GetRequiredService<ReminderService>();
            reminders.Stop();
            reminders.Start();
            await Task.Delay(2500);
            return $"announced=[{string.Join(", ", App.Services.GetRequiredService<SettingsService>().Current.RemindedPayments)}]";
        });

        await Step("insights", async () =>
        {
            var insights = await Open<InsightsViewModel>("Insights");
            for (int i = 0; i < 30 && insights.Stats.Count == 0; i++) await Task.Delay(100);
            await Shot("insights");
            return $"stats=[{string.Join(", ", insights.Stats.Select(s => s.Value + " " + s.Label))}] days={insights.Activity.Count} ({insights.ActivityText}) " +
                   $"mood={insights.HasMood} ({insights.MoodText}) reviews={insights.HasReviews} ({insights.ReviewText})";
        });

        await Step("today", async () =>
        {
            var day = DateTime.Today.AddDays(-2);
            var today = await Open<TodayViewModel>("Today", day);
            await Shot("today");
            return $"date={today.Date:yyyy-MM-dd} (asked {day:yyyy-MM-dd})";
        });
    }

    // ----- synthetic drag & drop test (real mouse input) -----

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static async Task DragTestAsync(MainWindow window, MainWindowViewModel vm, string dir)
    {
        var log = new System.Text.StringBuilder();
        Behaviors.DnD.Trace = m => log.AppendLine("   dnd: " + m);
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Activate();

        // 1) Tasks board: first "To do" card в†’ "In progress" column.
        vm.Shell?.NavigateTo("Tasks", "board");
        await Task.Delay(1500);
        var todo = FindItemsControl(window, "Todo");
        var progress = FindItemsControl(window, "InProgress");
        if (todo != null && progress != null && todo.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement card)
        {
            var title = (card.DataContext as Models.TaskItem)?.Title;
            await DragAsync(card, new Point(card.ActualWidth / 2, 18), progress, new Point(progress.ActualWidth / 2, Math.Min(progress.ActualHeight - 10, 140)));
            await Task.Delay(1200);
            var moved = progress.Items.Cast<object>().OfType<Models.TaskItem>().Any(t => t.Title == title);
            log.AppendLine($"tasks board: '{title}' moved to In progress = {moved}");
            Capture(window, Path.Combine(dir, "drag-tasks.png"));
        }
        else log.AppendLine("tasks board: elements not found");

        // 2) Notes grid: first "Others" card onto the second one (hover reorder).
        vm.Shell?.NavigateTo("Notes");
        await Task.Delay(1500);
        var others = FindItemsControl(window, "others");
        if (others != null && others.Items.Count >= 2 &&
            others.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement a && others.ItemContainerGenerator.ContainerFromIndex(1) is FrameworkElement b)
        {
            var first = (a.DataContext as Models.Note)?.DisplayTitle;
            await DragAsync(a, new Point(a.ActualWidth / 2, 20), b, new Point(b.ActualWidth / 2, b.ActualHeight / 2));
            await Task.Delay(1200);
            var nowIndex = others.Items.Cast<Models.Note>().ToList().FindIndex(n => n.DisplayTitle == first);
            log.AppendLine($"notes grid: '{first}' now at index {nowIndex} (was 0)");
            Capture(window, Path.Combine(dir, "drag-notes.png"));
        }
        else log.AppendLine("notes grid: elements not found");

        // 3) Calendar month: first event chip в†’ the cell two days later.
        vm.Shell?.NavigateTo("Calendar", "Month");
        await Task.Delay(1500);
        var chip = FindVisuals<System.Windows.Controls.Border>(window).FirstOrDefault(x => x.Name == "chip" && x.DataContext is Occurrence { Kind: OccurrenceKind.Event, IsRecurring: false } && x.IsVisible);
        if (chip?.DataContext is Occurrence occ)
        {
            var targetDate = occ.Start.Date.AddDays(2);
            var cell = FindVisuals<System.Windows.Controls.Border>(window).FirstOrDefault(x => x.Name == "cell" && x.DataContext is MonthDay md && md.Date == targetDate);
            if (cell != null)
            {
                await DragAsync(chip, new Point(chip.ActualWidth / 2, chip.ActualHeight / 2), cell, new Point(cell.ActualWidth / 2, cell.ActualHeight / 2));
                await Task.Delay(1200);
                log.AppendLine($"calendar: '{occ.Title}' {occ.Start:d} в†’ now {occ.Event?.Start:d} (expected {targetDate:d})");
                Capture(window, Path.Combine(dir, "drag-calendar.png"));
            }
        }
        else log.AppendLine("calendar: no chip found");

        File.WriteAllText(Path.Combine(dir, "drag-test.txt"), log.ToString());
    }

    private static async Task DragAsync(FrameworkElement from, Point fromOffset, FrameworkElement to, Point toOffset)
    {
        var start = from.PointToScreen(fromOffset);
        var end = to.PointToScreen(toOffset);
        SetCursorPos((int)start.X, (int)start.Y);
        await Task.Delay(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // left down
        await Task.Delay(120);
        const int steps = 30;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            SetCursorPos((int)(start.X + (end.X - start.X) * t), (int)(start.Y + (end.Y - start.Y) * t));
            await Task.Delay(25);
        }
        await Task.Delay(250);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // left up
    }

    private static System.Windows.Controls.ItemsControl? FindItemsControl(DependencyObject root, string listTag) =>
        FindVisuals<System.Windows.Controls.ItemsControl>(root).FirstOrDefault(ic => Equals(Behaviors.DnD.GetListTag(ic), listTag) && ic.IsVisible);

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var nested in FindVisuals<T>(child)) yield return nested;
        }
    }

    private static async Task SeedVaultAsync()
    {
        var store = App.Services.GetRequiredService<Data.DataStore>();
        if (await store.CountAsync<Models.VaultProfile>() > 0) return;
        var google = new Models.VaultProfile { Name = "Google", Category = "Email", Website = "https://accounts.google.com", Color = "red", Tags = "email, cloud", SortOrder = 0 };
        var github = new Models.VaultProfile { Name = "GitHub", Category = "Developer", Website = "https://github.com", Color = "slate", Icon = Glyphs.Code, SortOrder = 1 };
        var bank = new Models.VaultProfile { Name = "Northwind Bank", Category = "Finance", Website = "https://northwind.example", Color = "teal", Icon = Glyphs.Bank, IsFavorite = true, SortOrder = 2 };
        var wifi = new Models.VaultProfile { Name = "Home Wi-Fi", Category = "Home", Color = "sky", Icon = Glyphs.Wifi, SortOrder = 3 };
        var shop = new Models.VaultProfile { Name = "Shopify Store", Category = "Shopping", Website = "https://shop.example", Color = "green", SortOrder = 4 };
        await store.AddRangeAsync(new[] { google, github, bank, wifi, shop });
        await store.AddRangeAsync(new[]
        {
            new Models.VaultAccount { ProfileId = google.Id, Kind = Models.AccountKind.Email, Label = "Personal", Email = "alex.morgan@mail.example", Password = "Maple-Rocket-Harbor-72!", TotpSecret = "JBSWY3DPEHPK3PXP", PasswordChangedAt = DateTime.Now.AddDays(-40), IsFavorite = true, SortOrder = 0,
                Fields = Models.CustomField.Serialize([new Models.CustomField { Label = "Recovery email", Type = Models.FieldType.Email, Value = "alex.backup@mail.example" }]) },
            new Models.VaultAccount { ProfileId = google.Id, Kind = Models.AccountKind.Email, Label = "Work", Email = "alex@northwind.example", Password = "summer2024", PasswordChangedAt = DateTime.Now.AddDays(-500), SignInWith = "Single sign-on", SortOrder = 1 },
            new Models.VaultAccount { ProfileId = github.Id, Kind = Models.AccountKind.ApiToken, Label = "CI deploy token", Url = "https://github.com/settings/tokens", ExpiresAt = DateTime.Today.AddDays(12), SortOrder = 0,
                Fields = Models.CustomField.Serialize([new Models.CustomField { Label = "Token", Type = Models.FieldType.Secret, Value = "ghp_x7Kq2vR9mP4tW8yZ1aB3cD5eF6gH0iJ" }, new Models.CustomField { Label = "Scopes", Type = Models.FieldType.Text, Value = "repo, workflow" }]) },
            new Models.VaultAccount { ProfileId = github.Id, Kind = Models.AccountKind.Login, Label = "Main", Username = "alexm", Password = "summer2024", SignInWith = "Password", SortOrder = 1 },
            new Models.VaultAccount { ProfileId = bank.Id, Kind = Models.AccountKind.BankAccount, Label = "Checking", Username = "amorgan", Password = "Tq#8vL!2pZ@9wR", SortOrder = 0, PasswordChangedAt = DateTime.Now.AddDays(-10),
                Fields = Models.CustomField.Serialize([new Models.CustomField { Label = "IBAN", Type = Models.FieldType.Secret, Value = "BG80 BNBG 9661 1020 3456 78" }]) },
            new Models.VaultAccount { ProfileId = bank.Id, Kind = Models.AccountKind.Card, Label = "Debit card", ExpiresAt = DateTime.Today.AddMonths(14), SortOrder = 1,
                Fields = Models.CustomField.Serialize([new Models.CustomField { Label = "Card number", Type = Models.FieldType.Secret, Value = "4111 1111 1111 1111" }, new Models.CustomField { Label = "CVV", Type = Models.FieldType.Secret, Value = "123" }]) },
            new Models.VaultAccount { ProfileId = wifi.Id, Kind = Models.AccountKind.WiFi, Label = "Router", Password = "Cozy-Lantern-Meadow", SortOrder = 0,
                Fields = Models.CustomField.Serialize([new Models.CustomField { Label = "Network name (SSID)", Type = Models.FieldType.Text, Value = "Morgan-5G" }]) },
            new Models.VaultAccount { ProfileId = shop.Id, Kind = Models.AccountKind.Login, Label = "Admin", Email = "alex.morgan@mail.example", Password = "Maple-Rocket-Harbor-72!", SortOrder = 0 },
        });
    }

    /// <summary>Writes binding trace messages; for errors also the managed stack so the culprit can be located.</summary>
    private sealed class StackTraceListener(string path) : System.Diagnostics.TextWriterTraceListener(path)
    {
        public override void WriteLine(string? message)
        {
            base.WriteLine(message);
            if (message != null && message.Contains("path error"))
            {
                var frames = Environment.StackTrace.Split('\n')
                    .Where(l => l.Contains("Satchel.") || l.Contains("ApplyTemplate") || l.Contains("ItemContainerGenerator") || l.Contains("Measure") || l.Contains("OnPropertyChanged"))
                    .Take(25);
                base.WriteLine("   STACK:\n" + string.Join("\n", frames));
            }
        }
    }

    private static void ReportPathErrors(Window window, string page)
    {
        var bad = new List<string>();
        foreach (var fe in FindVisuals<FrameworkElement>(window))
        {
            var enumerator = fe.GetLocalValueEnumerator();
            while (enumerator.MoveNext())
            {
                if (BindingOperations.GetBindingExpression(fe, enumerator.Current.Property) is { Status: BindingStatus.PathError } be)
                {
                    var chain = new List<string>();
                    DependencyObject? cur = fe;
                    for (int i = 0; i < 14 && cur != null; i++)
                    {
                        if (cur is FrameworkElement f) chain.Add($"{f.GetType().Name}{(string.IsNullOrEmpty(f.Name) ? "" : "#" + f.Name)}");
                        cur = VisualTreeHelper.GetParent(cur);
                    }
                    bad.Add($"{page}: {be.ParentBinding.Path?.Path} on {string.Join(" < ", chain)}");
                }
            }
        }
        if (bad.Count > 0) File.AppendAllLines(Path.Combine(Core.AppPaths.DataDirectory, "path-errors.txt"), bad);
    }

    private static async Task ModuleShotAsync(MainWindow window, MainWindowViewModel vm, string page, string? arg, string file)
    {
        var shell = vm.Shell!;
        switch (page)
        {
            case "ProjectDetail":
                shell.NavigateTo("Projects");
                await Task.Delay(1200);
                if (shell.GetPage<ProjectsViewModel>("Projects") is { } projects && projects.Visible.FirstOrDefault() is { } p)
                {
                    await projects.OpenProjectCommand.ExecuteAsync(p);
                    projects.Tab = arg ?? "Overview";
                }
                break;
            case "ReadingDetail" or "ReadingQuotes":
                shell.NavigateTo("Reading");
                await Task.Delay(1200);
                if (shell.GetPage<ReadingViewModel>("Reading") is { } reading)
                {
                    if (page == "ReadingQuotes") { reading.Selected = null; reading.View = "Quotes"; }
                    else { reading.View = "Reading"; reading.Selected = reading.Books.FirstOrDefault(b => b.Status == Models.BookStatus.Reading); }
                }
                break;
            case "GamesDetail" or "GamesLibrary":
                shell.NavigateTo("Games");
                await Task.Delay(1200);
                if (shell.GetPage<GamesViewModel>("Games") is { } games)
                {
                    games.IsBoardView = page == "GamesDetail";
                    games.Selected = page == "GamesDetail" ? games.Playing.FirstOrDefault() : null;
                }
                break;
            case "SketchEdit":
                shell.NavigateTo("Sketch");
                await Task.Delay(1200);
                if (shell.GetPage<SketchViewModel>("Sketch") is { } sketch && sketch.Sketches.FirstOrDefault() is { } s) await sketch.OpenCommand.ExecuteAsync(s);
                break;
            case "ReadingSearch":
                shell.NavigateTo("Reading");
                await Task.Delay(1200);
                if (shell.GetPage<ReadingViewModel>("Reading") is { } rs)
                {
                    rs.Selected = null;
                    rs.OpenAddCommand.Execute(null);
                    rs.SearchQuery = arg ?? "dune";
                    await rs.SearchCommand.ExecuteAsync(null);
                    await Task.Delay(2500);
                    File.WriteAllText(Path.ChangeExtension(file, null) + ".txt",
                        $"{rs.SearchResults.Count} results; first: {rs.SearchResults.FirstOrDefault()?.Title} / {rs.SearchResults.FirstOrDefault()?.Author} ({rs.SearchResults.FirstOrDefault()?.Meta}); thumbs={rs.SearchResults.Count(r => r.Thumb != null)}");
                }
                break;
            case "GamesSearch":
                shell.NavigateTo("Games");
                await Task.Delay(1200);
                if (shell.GetPage<GamesViewModel>("Games") is { } gs)
                {
                    gs.Selected = null;
                    gs.OpenAddCommand.Execute(null);
                    gs.AddQuery = arg ?? "hollow knight";
                    await gs.SearchCommand.ExecuteAsync(null);
                    await Task.Delay(2500);
                    File.WriteAllText(Path.ChangeExtension(file, null) + ".txt",
                        $"{gs.SearchResults.Count} results; first: {gs.SearchResults.FirstOrDefault()?.Name} (app {gs.SearchResults.FirstOrDefault()?.AppId}); thumbs={gs.SearchResults.Count(r => r.Thumb != null)}");
                }
                break;
            case "VideoPlay":
                shell.NavigateTo("Videos");
                await Task.Delay(1500);
                if (shell.GetPage<VideosViewModel>("Videos") is { } videos)
                {
                    videos.Section = "Saved";
                    if (videos.Visible.FirstOrDefault() is { } v) await videos.PlayCommand.ExecuteAsync(v);
                    await Task.Delay(9000);
                    if (FindVisuals<Microsoft.Web.WebView2.Wpf.WebView2>(window).FirstOrDefault() is { CoreWebView2: { } core })
                    {
                        using var fs = File.Create(Path.ChangeExtension(file, null) + "-webview.png");
                        await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
                    }
                    File.WriteAllText(Path.ChangeExtension(file, null) + "-player.txt", $"time={videos.CurrentTime:0.0} duration={videos.Duration:0.0}");
                }
                break;
        }
        await Task.Delay(1500);
        Capture(window, file);
        ReportPathErrors(window, page);
    }

    private static async Task SeedModulesAsync(MainWindowViewModel vm)
    {
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var log = new List<string>();
        if (await store.CountAsync<Models.Project>() == 0)
        {
            var list = new Models.TaskList { Name = "Balcony makeover", Color = "green", Icon = Glyphs.Flag, SortOrder = 1000 };
            var book = new Models.Notebook { Name = "Balcony makeover", Color = "green", SortOrder = 1000 };
            var board = new Models.Board { Name = "Balcony makeover", SortOrder = 1000 };
            await store.RunAsync(db => { db.AddRange(list, book, board); db.SaveChanges(); });
            var p1 = new Models.Project { Name = "Balcony makeover", Description = "A green corner to read and have coffee in. Done = plants in, new lights, a comfy chair.",
                Status = Models.ProjectStatus.Active, Color = "green", Icon = Glyphs.Home, StartDate = DateTime.Today.AddDays(-12), DueDate = DateTime.Today.AddDays(24),
                TaskListId = list.Id, NotebookId = book.Id, BoardId = board.Id, IsPinned = true };
            var p2 = new Models.Project { Name = "Learn piano basics", Description = "Play three songs by ear before summer.", Status = Models.ProjectStatus.Idea, Color = "sky", Icon = Glyphs.Music };
            var p3 = new Models.Project { Name = "Tax return 2026", Description = "Collect receipts, fill the form, submit.", Status = Models.ProjectStatus.Planning, Color = "amber", Icon = Glyphs.Calculator, DueDate = DateTime.Today.AddDays(-2) };
            await store.AddRangeAsync(new[] { p1, p2, p3 });
            await store.AddRangeAsync(new[]
            {
                new Models.TaskItem { ListId = list.Id, Title = "Measure the balcony", State = Models.TaskState.Done, CompletedAt = DateTime.Now.AddDays(-9) },
                new Models.TaskItem { ListId = list.Id, Title = "Pick plants that like shade", State = Models.TaskState.Done, CompletedAt = DateTime.Now.AddDays(-4) },
                new Models.TaskItem { ListId = list.Id, Title = "Order string lights", State = Models.TaskState.InProgress, DueDate = DateTime.Today.AddDays(2) },
                new Models.TaskItem { ListId = list.Id, Title = "Buy planters and soil", State = Models.TaskState.Todo, DueDate = DateTime.Today.AddDays(5) },
                new Models.TaskItem { ListId = list.Id, Title = "Find a second-hand chair", State = Models.TaskState.Todo },
            });
            await store.AddRangeAsync(new[]
            {
                new Models.Milestone { ProjectId = p1.Id, Title = "Plan and budget", IsDone = true, DueDate = DateTime.Today.AddDays(-5), SortOrder = 0 },
                new Models.Milestone { ProjectId = p1.Id, Title = "Plants in", DueDate = DateTime.Today.AddDays(10), SortOrder = 1 },
                new Models.Milestone { ProjectId = p1.Id, Title = "First coffee out there ☕", DueDate = DateTime.Today.AddDays(24), SortOrder = 2 },
            });
            await store.AddRangeAsync(new[]
            {
                new Models.ProjectUpdate { ProjectId = p1.Id, Text = "Budget set: 300 € max.", Kind = Models.UpdateKind.Decision, Date = DateTime.Now.AddDays(-8) },
                new Models.ProjectUpdate { ProjectId = p1.Id, Text = "Found ferns and ivy that handle shade well.", Kind = Models.UpdateKind.Win, Date = DateTime.Now.AddDays(-4) },
                new Models.ProjectUpdate { ProjectId = p1.Id, Text = "The lights I wanted are out of stock until next week.", Kind = Models.UpdateKind.Blocker, Date = DateTime.Now.AddDays(-1) },
            });
            await store.AddAsync(new Models.ProjectResource { ProjectId = p1.Id, Title = "Shade-loving balcony plants", Url = "https://www.rhs.org.uk/plants/types/shade" });
            log.Add("projects");
        }
        if (await store.CountAsync<Models.Book>() == 0)
        {
            var reading = new Models.Book { Title = "Project Hail Mary", Author = "Andy Weir", TotalPages = 476, CurrentPage = 212, Status = Models.BookStatus.Reading, Color = "teal", StartedAt = DateTime.Today.AddDays(-9) };
            var b2 = new Models.Book { Title = "Atomic Habits", Author = "James Clear", TotalPages = 320, Status = Models.BookStatus.WantToRead, Color = "amber" };
            var b3 = new Models.Book { Title = "The Hobbit", Author = "J. R. R. Tolkien", TotalPages = 310, CurrentPage = 310, Status = Models.BookStatus.Finished, Rating = 5, Color = "green", FinishedAt = DateTime.Today.AddDays(-30) };
            var b4 = new Models.Book { Title = "Dune", Author = "Frank Herbert", TotalPages = 612, Status = Models.BookStatus.WantToRead, Color = "orange" };
            await store.AddRangeAsync(new[] { reading, b2, b3, b4 });
            var sessions = Enumerable.Range(0, 9).Select(i => new Models.ReadingSession { BookId = reading.Id, Date = DateTime.Today.AddDays(-i).AddHours(21), Pages = 12 + (i * 7) % 25, ToPage = 212 - i * 20 }).ToList();
            await store.AddRangeAsync(sessions);
            await store.AddRangeAsync(new[]
            {
                new Models.BookQuote { BookId = b3.Id, Text = "Not all those who wander are lost.", Page = 167 },
                new Models.BookQuote { BookId = reading.Id, Text = "I penetrated the outer cell membrane with a nanosyringe.", Page = 140 },
            });
            log.Add("books");
        }
        if (await store.CountAsync<Models.Game>() == 0)
        {
            var games = new[]
            {
                new Models.Game { Title = "Hollow Knight", Platform = "PC", Genre = "Metroidvania", Status = Models.GameStatus.Playing, Hours = 23.5, Completion = 61, Rating = 5, Color = "slate", LastPlayedAt = DateTime.Now.AddDays(-1) },
                new Models.Game { Title = "Stardew Valley", Platform = "Nintendo Switch", Genre = "Farming sim", Status = Models.GameStatus.Playing, Hours = 41, Completion = 35, Color = "green" },
                new Models.Game { Title = "Baldur's Gate 3", Platform = "PC", Genre = "RPG", Status = Models.GameStatus.Backlog, Color = "red" },
                new Models.Game { Title = "Outer Wilds", Platform = "PC", Genre = "Exploration", Status = Models.GameStatus.Completed, Hours = 19, Completion = 100, Rating = 5, Color = "orange", FinishedAt = DateTime.Today.AddDays(-40) },
                new Models.Game { Title = "Hades II", Platform = "PC", Genre = "Roguelike", Status = Models.GameStatus.Wishlist, Color = "rose" },
            };
            await store.AddRangeAsync(games);
            await store.AddRangeAsync(Enumerable.Range(0, 6).Select(i => new Models.PlaySession { GameId = games[0].Id, Date = DateTime.Now.AddDays(-i * 2), Minutes = 45 + i * 10, Note = i == 0 ? "Beat the Mantis Lords!" : "" }));
            log.Add("games");
        }
        if (await store.CountAsync<Models.Sketch>() == 0)
        {
            var strokes = new System.Windows.Ink.StrokeCollection();
            var attrs = new System.Windows.Ink.DrawingAttributes { Color = Color.FromRgb(0x1F, 0x23, 0x28), Width = 4, Height = 4, FitToCurve = true };
            var pts = new System.Windows.Input.StylusPointCollection();
            for (double a = 0; a < Math.PI * 6; a += 0.08) pts.Add(new System.Windows.Input.StylusPoint(800 + Math.Cos(a) * a * 22, 500 + Math.Sin(a) * a * 22));
            strokes.Add(new System.Windows.Ink.Stroke(pts, attrs));
            var house = new System.Windows.Input.StylusPointCollection(new[] { new Point(250, 650), new Point(250, 420), new Point(400, 300), new Point(550, 420), new Point(550, 650), new Point(250, 650) }
                .Select(p => new System.Windows.Input.StylusPoint(p.X, p.Y)));
            strokes.Add(new System.Windows.Ink.Stroke(house, new System.Windows.Ink.DrawingAttributes { Color = Color.FromRgb(0xDC, 0x26, 0x26), Width = 6, Height = 6 }));
            var sun = new System.Windows.Input.StylusPointCollection();
            for (double a = 0; a <= Math.PI * 2.05; a += 0.1) sun.Add(new System.Windows.Input.StylusPoint(1300 + Math.Cos(a) * 90, 220 + Math.Sin(a) * 90));
            strokes.Add(new System.Windows.Ink.Stroke(sun, new System.Windows.Ink.DrawingAttributes { Color = Color.FromArgb(150, 0xF5, 0x9E, 0x0B), Width = 26, Height = 26, IsHighlighter = true }));
            byte[] isf;
            using (var ms = new MemoryStream()) { strokes.Save(ms, true); isf = ms.ToArray(); }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(0.225, 0.225));
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1600, 1000));
                strokes.Draw(dc);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(360, 225, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            await store.AddAsync(new Models.Sketch { Title = "Level idea: spiral tower", Strokes = isf, Thumbnail = Services.ImageService.FromBitmap(rtb, 360, photo: false), Paper = "plain" });
            log.Add("sketch");
        }
        if (await store.CountAsync<Models.VideoChannel>() == 0)
        {
            var feed = App.Services.GetRequiredService<VideoFeedService>();
            var (channel, error) = await feed.ResolveAsync("@NASA");
            if (channel != null)
            {
                await store.AddAsync(channel);
                int n = await feed.RefreshChannelAsync(channel, [], firstImport: true);
                log.Add($"channel {channel.Title} ({channel.YoutubeId}), avatar={(channel.Avatar != null)}, new={n}");
            }
            else log.Add("channel failed: " + error);
            var saved = await feed.FetchVideoAsync("jNQXAC9IVRw");
            if (saved != null) { saved.IsSaved = true; await store.AddAsync(saved); log.Add($"saved video: {saved.Title} / {saved.Author} thumb={(saved.Thumbnail != null)}"); }
            var count = await store.CountAsync<Models.Video>();
            log.Add($"videos in db: {count}");
        }
        File.WriteAllLines(Path.Combine(Core.AppPaths.DataDirectory, "seed-modules.txt"), log);
    }

    private static async Task SeedBoardsAsync()
    {
        var store = App.Services.GetRequiredService<Data.DataStore>();
        var existing = await store.ListAsync<Models.Board>();
        if (existing.Any(b => b.Name == "Investigation")) return;
        var cork = new Models.Board { Name = "Investigation", Style = "cork", SortOrder = 10 };
        var clean = new Models.Board { Name = "Launch plan", SortOrder = 11 };
        await store.AddRangeAsync(new[] { cork, clean });

        static string Rich(params (string Text, bool Bold, bool Italic)[] lines)
        {
            var doc = new System.Windows.Documents.FlowDocument();
            foreach (var (text, bold, italic) in lines)
            {
                var run = new System.Windows.Documents.Run(text);
                if (bold) run.FontWeight = FontWeights.Bold;
                if (italic) run.FontStyle = FontStyles.Italic;
                doc.Blocks.Add(new System.Windows.Documents.Paragraph(run));
            }
            return Controls.RichTextEditor.SerializeDocument(doc).Content;
        }

        var a = new Models.StickyNote { BoardId = cork.Id, Color = "yellow", X = 90, Y = 70, Width = 230, Height = 190, Rotation = -2.4, ZIndex = 1,
            Text = "The missing bicycle\nLast seen Friday 18:40", Content = Rich(("The missing bicycle", true, false), ("Last seen Friday 18:40", false, true)) };
        var b = new Models.StickyNote { BoardId = cork.Id, Color = "pink", X = 470, Y = 90, Width = 220, Height = 180, Rotation = 2.1, ZIndex = 2,
            Text = "Neighbour heard a bell around 19:00", Content = Rich(("Neighbour heard a bell around 19:00", false, false)) };
        var c = new Models.StickyNote { BoardId = cork.Id, Color = "blue", X = 800, Y = 330, Width = 230, Height = 190, Rotation = -1.2, ZIndex = 3,
            Text = "Check the park cameras\nAsk the café owner", Content = Rich(("Check the park cameras", false, false), ("Ask the café owner", false, false)) };
        var img = new Models.StickyNote { BoardId = cork.Id, Kind = Models.StickyKind.Image, Color = "white", X = 260, Y = 380, Width = 260, Height = 230, Rotation = 3, ZIndex = 4,
            Image = SampleImage(), Caption = "Park entrance, 18:52" };
        var link = new Models.StickyNote { BoardId = cork.Id, Kind = Models.StickyKind.Link, Color = "white", X = 640, Y = 640, Width = 300, Height = 252, Rotation = -2, ZIndex = 5,
            Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Caption = "Me at the zoo", Subtitle = "jawed", Image = SampleImage(16, 9) };
        var c1 = new Models.StickyNote { BoardId = clean.Id, Color = "green", X = 80, Y = 80, Text = "Idea", Content = Rich(("Idea", true, false), ("Portable organizer", false, false)), ZIndex = 1 };
        var c2 = new Models.StickyNote { BoardId = clean.Id, Color = "teal", X = 420, Y = 60, Text = "Build", Content = Rich(("Build", true, false), ("WPF + SQLite", false, false)), ZIndex = 2 };
        var c3 = new Models.StickyNote { BoardId = clean.Id, Color = "orange", X = 760, Y = 160, Text = "Launch", Content = Rich(("Launch", true, false), ("Copy to a flash drive", false, false)), ZIndex = 3 };
        var c4 = new Models.StickyNote { BoardId = clean.Id, Color = "white", X = 400, Y = 380, Text = "Feedback", Content = Rich(("Feedback", true, false), ("Boards are my favourite!", false, true)), ZIndex = 4 };
        await store.AddRangeAsync(new[] { a, b, c, img, link, c1, c2, c3, c4 });
        await store.AddRangeAsync(new[]
        {
            new Models.BoardLink { BoardId = cork.Id, FromId = a.Id, ToId = b.Id },
            new Models.BoardLink { BoardId = cork.Id, FromId = b.Id, ToId = c.Id, Label = "19:00?" },
            new Models.BoardLink { BoardId = cork.Id, FromId = a.Id, ToId = img.Id, Color = "blue" },
            new Models.BoardLink { BoardId = cork.Id, FromId = img.Id, ToId = link.Id, Color = "green" },
            new Models.BoardLink { BoardId = clean.Id, FromId = c1.Id, ToId = c2.Id, Arrow = true },
            new Models.BoardLink { BoardId = clean.Id, FromId = c2.Id, ToId = c3.Id, Arrow = true, Label = "then" },
            new Models.BoardLink { BoardId = clean.Id, FromId = c3.Id, ToId = c4.Id, Color = "rose" },
        });
    }

    /// <summary>A small generated "photo" (sky, sun, hills) so tests don't depend on files.</summary>
    private static byte[] SampleImage(int w = 4, int h = 3)
    {
        int width = 480, height = width * h / w;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x7C, 0xB9, 0xE8), Color.FromRgb(0xF6, 0xC1, 0x8B), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)), null, new Point(width * 0.72, height * 0.38), height * 0.12, height * 0.12);
            var hills = new StreamGeometry();
            using (var ctx = hills.Open())
            {
                ctx.BeginFigure(new Point(0, height * 0.7), true, true);
                ctx.BezierTo(new Point(width * 0.3, height * 0.5), new Point(width * 0.55, height * 0.85), new Point(width, height * 0.62), true, true);
                ctx.LineTo(new Point(width, height), true, true);
                ctx.LineTo(new Point(0, height), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x4E, 0x8D, 0x5B)), null, hills);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5D, 0x40, 0x37)), null, new Rect(width * 0.2, height * 0.55, 10, height * 0.25));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x38, 0x7A, 0x45)), null, new Point(width * 0.2 + 5, height * 0.52), 32, 26);
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Services.ImageService.FromBitmap(rtb, 800);
    }

    private static async Task SeedContactsAsync()
    {
        var store = App.Services.GetRequiredService<Data.DataStore>();
        if (await store.CountAsync<Models.Contact>() > 0) return;
        static string Entries(params (string Label, string Value)[] items) =>
            Models.ContactEntry.Serialize(items.Select(i => new Models.ContactEntry { Label = i.Label, Value = i.Value }));
        var today = DateTime.Today;
        await store.AddRangeAsync(new[]
        {
            new Models.Contact { FirstName = "Ana", LastName = "Petrova", Company = "Northwind", JobTitle = "Product designer", Color = "teal", IsFavorite = true,
                Birthday = new DateTime(1991, today.AddDays(3).Month, today.AddDays(3).Day), Tags = "work, friends",
                Emails = Entries(("Work", "ana.petrova@northwind.example"), ("Personal", "ana.p@mail.example")), Phones = Entries(("Mobile", "+359 88 123 4567")),
                Address = "12 Vitosha Blvd\nSofia 1000", Website = "https://ana.example", Notes = "Prefers calls after 5pm." },
            new Models.Contact { FirstName = "Ben", LastName = "Carter", Color = "sky", Birthday = new DateTime(1988, 11, 28),
                Emails = Entries(("Personal", "ben.carter@mail.example")), Phones = Entries(("Mobile", "+44 7700 900123"), ("Home", "+44 20 7946 0958")) },
            new Models.Contact { FirstName = "Dr. Elena", LastName = "Ivanova", JobTitle = "Dentist", Company = "Smile Clinic", Color = "green",
                Phones = Entries(("Office", "+359 2 987 6543")), Address = "3 Rakovski St\nSofia" },
            new Models.Contact { Nickname = "Plumber", Company = "QuickFix", Color = "amber", Phones = Entries(("Mobile", "+359 87 555 0101")), Tags = "home" },
            new Models.Contact { FirstName = "Maria", LastName = "Lopez", Color = "red", Birthday = new DateTime(1995, 1, 5), Emails = Entries(("Personal", "maria.lopez@mail.example")) },
        });
    }

    public static void Capture(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        double w = root.ActualWidth, h = root.ActualHeight;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var fs = File.Create(path))
            encoder.Save(fs);
        if (Environment.GetCommandLineArgs().Contains("--debug-tree")) DumpTree(window, path + ".tree.txt");
    }

    /// <summary>Writes the top of the visual tree (type, name, visibility, opacity, size, background) next to a screenshot.</summary>
    private static void DumpTree(Window window, string file)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"window: state={window.WindowState} visible={window.IsVisible} opacity={window.Opacity} bg={window.Background} size={window.ActualWidth}x{window.ActualHeight} allowsTransparency={window.AllowsTransparency}");
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 9) return;
            var line = new string(' ', depth * 2) + node.GetType().Name;
            if (node is FrameworkElement fe)
            {
                line += $" '{fe.Name}' vis={fe.Visibility} isVisible={fe.IsVisible} op={fe.Opacity:0.##} size={fe.ActualWidth:0}x{fe.ActualHeight:0}";
                if (fe.RenderTransform is { } rt && !rt.Value.IsIdentity) line += $" rt={rt.Value}";
                if (fe.Effect != null) line += " effect=" + fe.Effect.GetType().Name;
                if (fe.OpacityMask != null) line += " mask";
                if (fe.Clip != null) line += " clip=" + fe.Clip.Bounds;
            }
            if (node is System.Windows.Controls.Panel p) line += $" bg={p.Background}";
            if (node is System.Windows.Controls.Border b) line += $" bg={b.Background}";
            if (node is System.Windows.Controls.Control c) line += $" bg={c.Background}";
            sb.AppendLine(line);
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n && i < 12; i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(window, 0);
        File.WriteAllText(file, sb.ToString());
    }
}
#endif
