using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

public sealed record UpcomingPayment(DateTime Date, Subscription Item)
{
    public string DayText => Format.FriendlyDay(Date);
    public string AmountDisplay => Item.AmountDisplay;
}

public sealed record MoneyTotal(string Currency, string Monthly, string Yearly);

/// <summary>Money: subscriptions and bills — what they cost per month and year, what's due next, payments made.</summary>
public sealed partial class MoneyViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Subscription> _dirty = [];
    private List<Subscription> _all = [];
    private Dictionary<int, string> _vaultNames = [];
    private bool _loaded;

    public MoneyViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Money";
    public ObservableCollection<Subscription> Items { get; } = [];
    public ObservableCollection<UpcomingPayment> Upcoming { get; } = [];
    public ObservableCollection<ChartSlice> Categories { get; } = [];
    public ObservableCollection<MoneyTotal> Totals { get; } = [];
    public ObservableCollection<Payment> Payments { get; } = [];
    public ObservableCollection<string> CategoryNames { get; } = [];

    [ObservableProperty] private Subscription? _selected;
    [ObservableProperty] private string _filter = "active";
    [ObservableProperty] private string _monthlyText = "";
    [ObservableProperty] private string _yearlyText = "";
    [ObservableProperty] private string _next30Text = "";
    [ObservableProperty] private string _mainCurrency = "EUR";

    public bool HasSelection => Selected != null;
    public bool IsEmpty => _all.Count == 0;
    public bool HasItems => Items.Count > 0;
    public bool HasSeveralCurrencies => Totals.Count > 1;
    public int ActiveCount => _all.Count(s => s.IsActive);

    public static IReadOnlyList<Choice<BillingCycle>> Cycles { get; } =
    [
        new(BillingCycle.Weekly, "Weekly"), new(BillingCycle.Monthly, "Monthly"), new(BillingCycle.Quarterly, "Every 3 months"),
        new(BillingCycle.HalfYearly, "Every 6 months"), new(BillingCycle.Yearly, "Yearly"), new(BillingCycle.EveryNDays, "Every N days"), new(BillingCycle.Once, "One-off")
    ];

    public static IReadOnlyList<Choice<MoneyKind>> Kinds { get; } = [new(MoneyKind.Subscription, "Subscription"), new(MoneyKind.Bill, "Bill")];
    public static IReadOnlyList<Choice<string>> Currencies => SettingsViewModel.Currencies;
    public static IReadOnlyList<string> SuggestedCategories { get; } = ["Streaming", "Music", "Software", "Cloud", "Phone & internet", "Utilities", "Rent", "Insurance", "Gym", "News", "Games", "Other"];

    public static IReadOnlyList<Choice<int>> RemindChoices { get; } =
        [new(0, "No reminder"), new(1, "1 day before"), new(3, "3 days before"), new(7, "A week before"), new(14, "Two weeks before")];

    partial void OnFilterChanged(string value) => Apply();

    partial void OnSelectedChanged(Subscription? oldValue, Subscription? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnItemChanged;
        if (newValue != null) newValue.PropertyChanged += OnItemChanged;
        OnPropertyChanged(nameof(HasSelection));
        _ = LoadPaymentsAsync(newValue);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Subscription s || e.PropertyName is nameof(Entity.UpdatedAt) or nameof(Subscription.VaultName) or nameof(Entity.IsPrivate)) return;
        if (e.PropertyName is nameof(Subscription.Amount) or nameof(Subscription.MonthlyCost) or nameof(Subscription.YearlyCost) or nameof(Subscription.CycleText)
            or nameof(Subscription.AmountDisplay) or nameof(Subscription.DaysUntilDue) or nameof(Subscription.IsOverdue) or nameof(Subscription.IsDueSoon) or nameof(Subscription.DueText)) return;
        _dirty.Add(s);
        _saveTimer.Stop();
        _saveTimer.Start();
        if (e.PropertyName is nameof(Subscription.AmountText) or nameof(Subscription.Cycle) or nameof(Subscription.CycleDays) or nameof(Subscription.Category)
            or nameof(Subscription.Currency) or nameof(Subscription.IsActive) or nameof(Subscription.NextDue)) Recompute();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var s in batch) s.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && _all.FirstOrDefault(s => s.Id == id) is { } item) Selected = item;
        else if (parameter is "new") await Add();
    }

    public override void OnNewShortcut() => _ = Add();

    private async Task LoadAsync()
    {
        var (items, profiles) = await _store.RunAsync(db => (db.Subscriptions.ToList(), db.Profiles.Select(p => new { p.Id, p.Name }).ToList()));
        _vaultNames = profiles.ToDictionary(p => p.Id, p => p.Name);
        foreach (var s in items) s.VaultName = s.VaultProfileId is int pid && _vaultNames.TryGetValue(pid, out var n) ? n : "";
        _all = items;
        MainCurrency = _settings.Current.Currency;
        _loaded = true;
        Recompute();
    }

    private void Recompute()
    {
        foreach (var s in _all) s.RefreshComputed();
        var active = _all.Where(s => s.IsActive).ToList();
        // Totals per currency (no conversion — it's your money, your rates).
        Totals.Clear();
        foreach (var g in active.Where(s => s.Cycle != BillingCycle.Once).GroupBy(s => s.Currency).OrderByDescending(g => g.Key == MainCurrency).ThenByDescending(g => g.Sum(s => s.MonthlyCost)))
            Totals.Add(new MoneyTotal(g.Key, Subscription.FormatMoney(Math.Round(g.Sum(s => s.MonthlyCost), 2), g.Key), Subscription.FormatMoney(Math.Round(g.Sum(s => s.YearlyCost), 0), g.Key)));
        var main = Totals.FirstOrDefault();
        MonthlyText = main?.Monthly ?? Subscription.FormatMoney(0, MainCurrency);
        YearlyText = main?.Yearly ?? Subscription.FormatMoney(0, MainCurrency);
        var mainCurrency = main?.Currency ?? MainCurrency;
        var soon = Occurrences(active, DateTime.Today, DateTime.Today.AddDays(30)).Where(o => o.Item.Currency == mainCurrency).Sum(o => o.Item.Amount);
        Next30Text = Subscription.FormatMoney(soon, mainCurrency);

        Upcoming.Clear();
        foreach (var o in Occurrences(active, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(45)).Where(o => o.Date >= DateTime.Today || o.Item.IsOverdue).Take(12))
            Upcoming.Add(o);

        // Category share of the monthly cost (main currency): 6 slots, the rest folds into "Other".
        Categories.Clear();
        var byCategory = active.Where(s => s.Currency == mainCurrency && s.Cycle != BillingCycle.Once)
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "Other" : s.Category.Trim())
            .Select(g => (Name: g.Key, Monthly: g.Sum(s => s.MonthlyCost))).OrderByDescending(x => x.Monthly).ToList();
        int slot = 0;
        foreach (var (name, monthly) in byCategory.Take(6))
            Categories.Add(new ChartSlice(name, (double)monthly, name == "Other" ? -1 : slot++, Subscription.FormatMoney(Math.Round(monthly, 2), mainCurrency) + " / mo"));
        var rest = byCategory.Skip(6).Sum(x => x.Monthly);
        if (rest > 0) Categories.Add(new ChartSlice("Other", (double)rest, -1, Subscription.FormatMoney(Math.Round(rest, 2), mainCurrency) + " / mo"));

        CategoryNames.Clear();
        foreach (var c in SuggestedCategories.Concat(_all.Select(s => s.Category.Trim())).Where(c => c.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(c => c))
            CategoryNames.Add(c);
        Apply();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSeveralCurrencies));
        OnPropertyChanged(nameof(ActiveCount));
    }

    /// <summary>Every due date of the active items between two dates.</summary>
    public static IEnumerable<UpcomingPayment> Occurrences(IEnumerable<Subscription> items, DateTime from, DateTime to)
    {
        var list = new List<UpcomingPayment>();
        foreach (var s in items)
        {
            var d = s.NextDue.Date;
            int guard = 0;
            while (d <= to && guard++ < 200)
            {
                if (d >= from) list.Add(new UpcomingPayment(d, s));
                if (s.Cycle == BillingCycle.Once) break;
                d = s.Advance(d);
            }
        }
        return list.OrderBy(o => o.Date);
    }

    private void Apply()
    {
        IEnumerable<Subscription> items = Filter switch
        {
            "subscriptions" => _all.Where(s => s.IsActive && s.Kind == MoneyKind.Subscription),
            "bills" => _all.Where(s => s.IsActive && s.Kind == MoneyKind.Bill),
            "soon" => _all.Where(s => s.IsActive && (s.IsDueSoon || s.IsOverdue)),
            "cancelled" => _all.Where(s => !s.IsActive),
            _ => _all.Where(s => s.IsActive)
        };
        Items.Clear();
        foreach (var s in items.OrderBy(s => s.NextDue)) Items.Add(s);
        OnPropertyChanged(nameof(HasItems));
    }

    private async Task LoadPaymentsAsync(Subscription? s)
    {
        Payments.Clear();
        if (s is not { Id: > 0 }) return;
        var id = s.Id;
        var list = await _store.ListAsync<Payment>(q => q.Where(p => p.SubscriptionId == id).OrderByDescending(p => p.Date));
        if (!ReferenceEquals(Selected, s)) return;
        foreach (var p in list.Take(24)) Payments.Add(p);
    }

    // ----- commands -----

    [RelayCommand] private void SetFilter(string? f) => Filter = f ?? "active";
    [RelayCommand] private void Select(Subscription? s) => Selected = s;
    [RelayCommand] private void CloseDetail() => Selected = null;
    [RelayCommand] private void SelectUpcoming(UpcomingPayment? p) { if (p != null) Selected = p.Item; }

    [RelayCommand]
    private async Task Add()
    {
        var name = await _dialogs.PromptAsync("New subscription or bill", "What do you pay for? You'll set the amount, how often and the next due date next.", "", "e.g. Netflix, Rent, Gym", "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        var s = new Subscription
        {
            Name = name.Trim(), Currency = _settings.Current.Currency, NextDue = DateTime.Today.AddMonths(1), StartedAt = DateTime.Today,
            Color = Palettes.Tags[_all.Count % 10].Key, Category = GuessCategory(name)
        };
        await _store.AddAsync(s);
        _all.Add(s);
        Filter = "active";
        Recompute();
        Selected = s;
    }

    private static string GuessCategory(string name)
    {
        var n = name.ToLowerInvariant();
        if (new[] { "netflix", "disney", "hbo", "prime video", "youtube premium", "apple tv", "max", "skyshowtime" }.Any(n.Contains)) return "Streaming";
        if (new[] { "spotify", "apple music", "tidal", "deezer" }.Any(n.Contains)) return "Music";
        if (new[] { "icloud", "dropbox", "google one", "onedrive", "backblaze" }.Any(n.Contains)) return "Cloud";
        if (new[] { "adobe", "office", "microsoft 365", "jetbrains", "github", "chatgpt", "claude", "notion", "1password" }.Any(n.Contains)) return "Software";
        if (new[] { "rent", "mortgage" }.Any(n.Contains)) return "Rent";
        if (new[] { "electric", "water", "gas", "heating" }.Any(n.Contains)) return "Utilities";
        if (new[] { "phone", "mobile", "internet", "fiber", "vivacom", "a1", "yettel" }.Any(n.Contains)) return "Phone & internet";
        if (new[] { "gym", "fitness" }.Any(n.Contains)) return "Gym";
        if (new[] { "insurance" }.Any(n.Contains)) return "Insurance";
        if (new[] { "xbox", "playstation", "nintendo", "game pass", "steam" }.Any(n.Contains)) return "Games";
        return "";
    }

    /// <summary>Logs a payment for the current due date and moves the due date to the next cycle.</summary>
    [RelayCommand]
    private async Task MarkPaid(Subscription? s)
    {
        s ??= Selected;
        if (s == null) return;
        var payment = new Payment { SubscriptionId = s.Id, Date = DateTime.Today, Amount = s.Amount, Currency = s.Currency };
        await _store.AddAsync(payment);
        var previous = s.NextDue;
        if (s.Cycle == BillingCycle.Once) s.IsActive = false;
        else
        {
            var next = s.Advance(s.NextDue);
            while (next.Date <= DateTime.Today && s.Cycle != BillingCycle.Once) next = s.Advance(next);
            s.NextDue = next;
        }
        _dirty.Add(s);
        await FlushAsync();
        Recompute();
        if (ReferenceEquals(Selected, s)) Payments.Insert(0, payment);
        _toasts.Show(s.IsActive ? $"Paid {s.AmountDisplay} — next due {Format.FriendlyDay(s.NextDue).ToLowerInvariant()}" : $"Paid {s.AmountDisplay}",
            ToastKind.Success, "Undo", async () =>
            {
                await _store.DeleteAsync(payment);
                s.NextDue = previous;
                s.IsActive = true;
                _dirty.Add(s);
                await FlushAsync();
                Recompute();
                Payments.Remove(payment);
            });
    }

    [RelayCommand]
    private async Task ToggleActive()
    {
        if (Selected is not { } s) return;
        s.IsActive = !s.IsActive;
        _dirty.Add(s);
        await FlushAsync();
        _toasts.Info(s.IsActive ? $"{s.Name} is active again" : $"{s.Name} marked as cancelled — kept in Cancelled");
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } s) return;
        if (!await _dialogs.ConfirmAsync("Delete?", $"“{s.Name}” and its payment history will be deleted.", "Delete", danger: true)) return;
        _dirty.Remove(s);
        await _store.DeleteAsync(s);
        _all.Remove(s);
        Selected = null;
        Recompute();
    }

    [RelayCommand]
    private async Task LinkVault()
    {
        if (Selected is not { } s) return;
        var profiles = await _store.ListAsync<VaultProfile>(q => q.OrderBy(p => p.Name));
        if (profiles.Count == 0) { _toasts.Info("Add the login to the Vault first"); return; }
        var options = profiles.OrderByDescending(p => p.Name.Contains(s.Name, StringComparison.CurrentCultureIgnoreCase) || s.Name.Contains(p.Name, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new PickOption(p.Name, string.IsNullOrWhiteSpace(p.Website) ? p.Category : p.Website, p.Icon, p, p.Color)).ToList();
        if (await _dialogs.ShowAsync(new PickDialogViewModel("Link a Vault login", $"Which Vault profile has the login for {s.Name}?", options, "Search the vault")) is not PickOption { Value: VaultProfile profile }) return;
        s.VaultProfileId = profile.Id;
        s.VaultName = profile.Name;
        if (string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(profile.Website)) s.Url = profile.Website;
        _dirty.Add(s);
        await FlushAsync();
    }

    [RelayCommand]
    private void OpenVault()
    {
        if (Selected?.VaultProfileId is int id) _nav.NavigateTo("Vault", id);
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Selected is { Url.Length: > 0 } s) UiCommands.OpenUrl.Execute(s.Url.Contains("://") ? s.Url : "https://" + s.Url);
    }

    [RelayCommand]
    private void SetMainCurrency(string? currency)
    {
        if (currency == null) return;
        _settings.Current.Currency = currency;
        MainCurrency = currency;
        Recompute();
    }

    [RelayCommand]
    private async Task DeletePayment(Payment? p)
    {
        if (p == null) return;
        await _store.DeleteAsync(p);
        Payments.Remove(p);
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        if (Selected != null) Selected.PropertyChanged -= OnItemChanged;
    }
}
