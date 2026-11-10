using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A countdown to a date (trip, birthday, exam…), optionally repeating every year.</summary>
public partial class Countdown : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty] private DateTime _date = DateTime.Today.AddDays(30);
    [ObservableProperty] private string _emoji = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private bool _yearly;
    [ObservableProperty] private bool _onDashboard = true;
    [ObservableProperty] private double _sortOrder;

    [NotMapped] public DateTime NextDate
    {
        get
        {
            if (!Yearly) return Date;
            var today = DateTime.Today;
            var next = new DateTime(today.Year, Date.Month, Math.Min(Date.Day, DateTime.DaysInMonth(today.Year, Date.Month)), Date.Hour, Date.Minute, 0);
            if (next.Date < today) next = new DateTime(today.Year + 1, Date.Month, Math.Min(Date.Day, DateTime.DaysInMonth(today.Year + 1, Date.Month)), Date.Hour, Date.Minute, 0);
            return next;
        }
    }

    [NotMapped] public int DaysLeft => (NextDate.Date - DateTime.Today).Days;
    [NotMapped] public bool IsPast => DaysLeft < 0;
    [NotMapped] public string DaysText => DaysLeft switch
    {
        0 => "Today",
        1 => "Tomorrow",
        -1 => "Yesterday",
        < 0 => $"{-DaysLeft} days ago",
        _ => $"{DaysLeft} days"
    };
    [NotMapped] public string DateText => NextDate.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

    partial void OnDateChanged(DateTime value) => Refresh();
    partial void OnYearlyChanged(bool value) => Refresh();

    public void Refresh()
    {
        OnPropertyChanged(nameof(NextDate));
        OnPropertyChanged(nameof(DaysLeft));
        OnPropertyChanged(nameof(IsPast));
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(DateText));
    }
}

/// <summary>Small encrypted settings (weather location, Steam ID, ignore lists…) — unlike app settings these need the master password.</summary>
public partial class SecureValue : Entity
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty][property: Encrypted] private string _value = "";
}
