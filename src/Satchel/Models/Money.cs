using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum BillingCycle { Weekly = 0, Monthly = 1, Quarterly = 2, HalfYearly = 3, Yearly = 4, EveryNDays = 5, Once = 6 }
public enum MoneyKind { Subscription = 0, Bill = 1 }

/// <summary>A recurring subscription or bill. Amounts are stored encrypted (as invariant text).</summary>
public partial class Subscription : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private string _amountText = "0";
    [ObservableProperty][property: Encrypted] private string _category = "";
    [ObservableProperty][property: Encrypted] private string _paymentMethod = "";
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty] private string _currency = "EUR";
    [ObservableProperty] private BillingCycle _cycle = BillingCycle.Monthly;
    [ObservableProperty] private int _cycleDays = 30;
    [ObservableProperty] private DateTime _nextDue = DateTime.Today;
    [ObservableProperty] private MoneyKind _kind;
    /// <summary>The vault profile with the login for this service (optional).</summary>
    [ObservableProperty] private int? _vaultProfileId;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private int _remindDays = 3;
    [ObservableProperty] private bool _autoPay;
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private string _vaultName = "";

    [NotMapped]
    public decimal Amount
    {
        get => decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        set => AmountText = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Average cost per month (for totals).</summary>
    [NotMapped] public decimal MonthlyCost => Cycle switch
    {
        BillingCycle.Weekly => Amount * 52m / 12m,
        BillingCycle.Monthly => Amount,
        BillingCycle.Quarterly => Amount / 3m,
        BillingCycle.HalfYearly => Amount / 6m,
        BillingCycle.Yearly => Amount / 12m,
        BillingCycle.EveryNDays => CycleDays > 0 ? Amount * 365m / CycleDays / 12m : 0,
        _ => 0
    };

    [NotMapped] public decimal YearlyCost => MonthlyCost * 12m;
    [NotMapped] public int DaysUntilDue => (NextDue.Date - DateTime.Today).Days;
    [NotMapped] public bool IsOverdue => IsActive && DaysUntilDue < 0;
    [NotMapped] public bool IsDueSoon => IsActive && DaysUntilDue >= 0 && DaysUntilDue <= Math.Max(3, RemindDays);

    [NotMapped] public string CycleText => Cycle switch
    {
        BillingCycle.Weekly => "every week",
        BillingCycle.Monthly => "every month",
        BillingCycle.Quarterly => "every 3 months",
        BillingCycle.HalfYearly => "every 6 months",
        BillingCycle.Yearly => "every year",
        BillingCycle.EveryNDays => $"every {CycleDays} days",
        _ => "once"
    };

    [NotMapped] public string AmountDisplay => FormatMoney(Amount, Currency);

    [NotMapped] public string DueText
    {
        get
        {
            if (!IsActive) return "Cancelled";
            var d = DaysUntilDue;
            return d switch
            {
                < -1 => $"{-d} days overdue",
                -1 => "1 day overdue",
                0 => "Due today",
                1 => "Due tomorrow",
                < 14 => $"Due in {d} days",
                _ => $"Due {NextDue:d MMM}"
            };
        }
    }

    partial void OnAmountTextChanged(string value) => RaiseCost();
    partial void OnCycleChanged(BillingCycle value) => RaiseCost();
    partial void OnCycleDaysChanged(int value) => RaiseCost();
    partial void OnCurrencyChanged(string value) => OnPropertyChanged(nameof(AmountDisplay));
    partial void OnNextDueChanged(DateTime value) => RaiseDue();
    partial void OnIsActiveChanged(bool value) => RaiseDue();

    private void RaiseCost()
    {
        OnPropertyChanged(nameof(Amount));
        OnPropertyChanged(nameof(MonthlyCost));
        OnPropertyChanged(nameof(YearlyCost));
        OnPropertyChanged(nameof(CycleText));
        OnPropertyChanged(nameof(AmountDisplay));
    }

    private void RaiseDue()
    {
        OnPropertyChanged(nameof(DaysUntilDue));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsDueSoon));
        OnPropertyChanged(nameof(DueText));
    }

    public void RefreshComputed()
    {
        RaiseCost();
        RaiseDue();
    }

    /// <summary>The due date after <paramref name="from"/> for this cycle.</summary>
    public DateTime Advance(DateTime from) => Cycle switch
    {
        BillingCycle.Weekly => from.AddDays(7),
        BillingCycle.Monthly => from.AddMonths(1),
        BillingCycle.Quarterly => from.AddMonths(3),
        BillingCycle.HalfYearly => from.AddMonths(6),
        BillingCycle.Yearly => from.AddYears(1),
        BillingCycle.EveryNDays => from.AddDays(Math.Max(1, CycleDays)),
        _ => from
    };

    public static string FormatMoney(decimal amount, string currency)
    {
        var symbol = currency.ToUpperInvariant() switch
        {
            "EUR" => "€", "USD" => "$", "GBP" => "£", "BGN" => "лв", "JPY" => "¥", "CHF" => "CHF", "PLN" => "zł", "RON" => "lei", "SEK" => "kr", "NOK" => "kr", "DKK" => "kr",
            _ => currency
        };
        var number = amount.ToString(amount == Math.Round(amount) ? "#,0" : "#,0.00", CultureInfo.CurrentCulture);
        return currency.ToUpperInvariant() is "USD" or "GBP" or "JPY" ? $"{symbol}{number}" : $"{number} {symbol}";
    }
}

/// <summary>A payment made for a subscription or bill.</summary>
public partial class Payment : Entity
{
    [ObservableProperty] private int _subscriptionId;
    [ObservableProperty] private DateTime _date = DateTime.Today;
    [ObservableProperty][property: Encrypted] private string _amountText = "0";
    [ObservableProperty][property: Encrypted] private string _note = "";
    [ObservableProperty] private string _currency = "EUR";

    [NotMapped]
    public decimal Amount
    {
        get => decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        set => AmountText = value.ToString(CultureInfo.InvariantCulture);
    }

    [NotMapped] public string AmountDisplay => Subscription.FormatMoney(Amount, Currency);
}
