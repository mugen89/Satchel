using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.ViewModels.Dialogs;

namespace Satchel.Services;

/// <summary>
/// The private flag on every item. Hiding works at the database level (see <c>AppDbContext.ConfigurePrivacy</c>), so lists,
/// search, the dashboard and the auto-diary all leave private items out while "Show private items" is off.
/// </summary>
public sealed class PrivacyService
{
    private readonly SettingsService _settings;
    private readonly SessionService _session;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;

    public PrivacyService(SettingsService settings, SessionService session, DataStore store, DialogService dialogs, ToastService toasts)
    {
        _settings = settings;
        _session = session;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        PrivacyState.ShowPrivate = settings.Current.ShowPrivate;
        _settings.Changed += OnSettingChanged;
    }

    public bool ShowPrivate => _settings.Current.ShowPrivate;

    /// <summary>"Show private items" was switched: pages reload their content.</summary>
    public event Action? VisibilityChanged;

    /// <summary>An item was marked private or not private.</summary>
    public event Action<Entity>? ItemChanged;

    private void OnSettingChanged(string name)
    {
        if (name != nameof(AppSettings.ShowPrivate) || PrivacyState.ShowPrivate == _settings.Current.ShowPrivate) return;
        PrivacyState.ShowPrivate = _settings.Current.ShowPrivate;
        VisibilityChanged?.Invoke();
    }

    /// <summary>Runs on every unlock, before any page loads.</summary>
    public void OnUnlocked()
    {
        if (_settings.Current.HidePrivateOnUnlock && _settings.Current.ShowPrivate) _settings.Current.ShowPrivate = false;
        PrivacyState.ShowPrivate = _settings.Current.ShowPrivate;
    }

    public async Task<bool> SetVisibleAsync(bool show)
    {
        if (show == ShowPrivate) return true;
        if (show && _settings.Current.PrivateNeedsPassword)
        {
            var ok = await _dialogs.ShowAsync(new VerifyPasswordDialogViewModel(_session, "Show private items",
                "Enter your master password to show the items you marked private.")) is true;
            if (!ok) return false;
        }
        _settings.Current.ShowPrivate = show;
        _toasts.Show(show ? "Private items are shown" : "Private items are hidden", ToastKind.Info, durationMs: 1800);
        return true;
    }

    public Task<bool> ToggleVisibleAsync() => SetVisibleAsync(!ShowPrivate);

    public Task ToggleAsync(Entity item) => SetAsync(item, !item.IsPrivate);

    public async Task SetAsync(Entity item, bool value, bool quiet = false)
    {
        if (item.Id == 0) { item.IsPrivate = value; return; }
        await _store.RunAsync(db =>
        {
            Mark(db, item, value);
            // A project's own task list, notebook and board go private with it.
            if (item is Project p)
            {
                if (p.TaskListId is int listId && db.TaskLists.IgnoreQueryFilters().FirstOrDefault(x => x.Id == listId) is { } list) Mark(db, list, value);
                if (p.NotebookId is int bookId && db.Notebooks.IgnoreQueryFilters().FirstOrDefault(x => x.Id == bookId) is { } book) Mark(db, book, value);
                if (p.BoardId is int boardId && db.Boards.IgnoreQueryFilters().FirstOrDefault(x => x.Id == boardId) is { } board) Mark(db, board, value);
            }
            // A Library folder's subfolders follow it, so nothing inside a private folder shows.
            if (item is LibraryFolder folder)
            {
                var all = db.LibraryFolders.IgnoreQueryFilters().ToList();
                var queue = new Queue<int>([folder.Id]);
                var seen = new HashSet<int> { folder.Id };
                while (queue.Count > 0)
                {
                    int parent = queue.Dequeue();
                    foreach (var child in all.Where(f => f.ParentId == parent && seen.Add(f.Id)))
                    {
                        Mark(db, child, value);
                        queue.Enqueue(child.Id);
                    }
                }
            }
            db.SaveChanges();
        });
        ItemChanged?.Invoke(item);
        if (quiet) return;
        if (value && !ShowPrivate)
            _toasts.Show(App.Services.GetService<KeyBindingService>()?.Text(KeyBindingService.PrivacyAction) is { } keys
                    ? $"Marked private — it's hidden now. Show private items in Settings → Privacy or with {keys}."
                    : "Marked private — it's hidden now. Show private items in Settings → Privacy.", ToastKind.Info, "Undo",
                () => _ = SetAsync(item, false, quiet: true), durationMs: 6000);
        else
            _toasts.Show(value ? "Marked private" : "No longer private", ToastKind.Success, durationMs: 1800);
    }

    /// <summary>Updates only the IsPrivate column (pending edits elsewhere are left alone).</summary>
    private static void Mark(AppDbContext db, Entity e, bool value)
    {
        e.IsPrivate = value;
        if (db.Entry(e).State == EntityState.Detached) db.Attach(e);
        db.Entry(e).Property(nameof(Entity.IsPrivate)).IsModified = true;
    }
}

/// <summary>Commands usable from any XAML menu: <c>Command="{x:Static s:PrivacyCommands.Toggle}" CommandParameter="{Binding}"</c>.</summary>
public static class PrivacyCommands
{
    private static PrivacyService Service => App.Services.GetRequiredService<PrivacyService>();

    public static IAsyncRelayCommand<object?> Toggle { get; } = new AsyncRelayCommand<object?>(async item =>
    {
        if (item is Entity e) await Service.ToggleAsync(e);
    });

    public static IAsyncRelayCommand ToggleVisible { get; } = new AsyncRelayCommand(() => Service.ToggleVisibleAsync());
}
