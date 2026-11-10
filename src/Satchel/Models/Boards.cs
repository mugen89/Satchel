using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Models;

public partial class Board : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private string _background = "dots";
    /// <summary>"" = clean canvas, "cork" = corkboard with push-pins and string connections.</summary>
    [ObservableProperty] private string _style = "";

    [NotMapped] public bool IsCork => Style == "cork";

    partial void OnStyleChanged(string value) => OnPropertyChanged(nameof(IsCork));
}

public enum StickyKind { Text = 0, Image = 1, Link = 2, Live = 3 }

public partial class StickyNote : Entity
{
    [ObservableProperty] private int _boardId;
    [ObservableProperty] private StickyKind _kind;
    /// <summary>Plain text — search and previews; also the whole content of notes written before rich text.</summary>
    [ObservableProperty][property: Encrypted] private string _text = "";
    /// <summary>Rich text as base64 XamlPackage (formatting and pasted images). Empty = use <see cref="Text"/>.</summary>
    [ObservableProperty][property: Encrypted] private string _content = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _caption = "";
    [ObservableProperty][property: Encrypted] private string _subtitle = "";
    [ObservableProperty] private string _color = "yellow";
    /// <summary>Push-pin colour on corkboards ("" = picked automatically).</summary>
    [ObservableProperty] private string _pin = "";
    /// <summary>Base text size; 0 = default.</summary>
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private double _x = 80;
    [ObservableProperty] private double _y = 80;
    [ObservableProperty] private double _width = 220;
    [ObservableProperty] private double _height = 200;
    [ObservableProperty] private int _zIndex;
    [ObservableProperty] private double _rotation;
    /// <summary>Live cards: what they show ("tasks", "note", "project", "countdown", "clock", "weather", "music").</summary>
    [ObservableProperty] private string _liveKind = "";
    /// <summary>Live cards: which item (a list/note/project id, a time zone…).</summary>
    [ObservableProperty] private string _liveRef = "";

    /// <summary>Live cards: the data shown, filled in by the board page.</summary>
    [ObservableProperty][property: NotMapped] private object? _live;

    [NotMapped] public bool IsText => Kind == StickyKind.Text;
    [NotMapped] public bool IsImage => Kind == StickyKind.Image;
    [NotMapped] public bool IsLink => Kind == StickyKind.Link;
    [NotMapped] public bool IsLive => Kind == StickyKind.Live;

    /// <summary>Text notes taken against a video keep a link with the time (…watch?v=ID&amp;t=83s): the chip on the note jumps there.</summary>
    [NotMapped] public string? TimeVideoId => Kind == StickyKind.Text && Url.Length > 0 ? YouTube.VideoId(Url) : null;
    [NotMapped] public double TimeSeconds => YouTube.StartSeconds(Url);
    [NotMapped] public bool HasTimeLink => TimeVideoId != null;
    [NotMapped] public string TimeText => YouTube.FormatTime(TimeSeconds);
    [NotMapped] public string? VideoId => Kind == StickyKind.Link ? YouTube.VideoId(Url) : null;
    [NotMapped] public bool IsVideo => VideoId != null;
    [NotMapped] public double EffectiveFontSize => FontSize > 0 ? FontSize : 15;
    [NotMapped] public string Domain => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";

    /// <summary>Centre of the push-pin in board coordinates (follows the note's rotation).</summary>
    [NotMapped] public System.Windows.Point PinPoint
    {
        get
        {
            double cx = X + Width / 2, cy = Y + Height / 2;
            double dy = 9 - Height / 2;
            double a = Rotation * Math.PI / 180;
            return new System.Windows.Point(cx - dy * Math.Sin(a), cy + dy * Math.Cos(a));
        }
    }

    partial void OnKindChanged(StickyKind value)
    {
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsLink));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(HasTimeLink));
    }

    partial void OnUrlChanged(string value)
    {
        OnPropertyChanged(nameof(VideoId));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(Domain));
        OnPropertyChanged(nameof(TimeVideoId));
        OnPropertyChanged(nameof(TimeSeconds));
        OnPropertyChanged(nameof(HasTimeLink));
        OnPropertyChanged(nameof(TimeText));
    }

    partial void OnFontSizeChanged(double value) => OnPropertyChanged(nameof(EffectiveFontSize));

    /// <summary>A copy for a board (a copy of a private note stays private).</summary>
    public StickyNote CloneFor(int boardId) => new()
    {
        BoardId = boardId, Kind = Kind, Text = Text, Content = Content, Image = Image, Url = Url, Caption = Caption, Subtitle = Subtitle,
        Color = Color, Pin = Pin, FontSize = FontSize, X = X, Y = Y, Width = Width, Height = Height, ZIndex = ZIndex, Rotation = Rotation,
        LiveKind = LiveKind, LiveRef = LiveRef, IsPrivate = IsPrivate
    };
}

/// <summary>A connection between two notes on a board (a curve, or a string on corkboards).</summary>
public partial class BoardLink : Entity
{
    [ObservableProperty] private int _boardId;
    [ObservableProperty] private int _fromId;
    [ObservableProperty] private int _toId;
    /// <summary>Palette key; "" = default (red string / accent line).</summary>
    [ObservableProperty] private string _color = "";
    [ObservableProperty][property: Encrypted] private string _label = "";
    [ObservableProperty] private bool _arrow;
}

public sealed class ContactEntry
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    public static List<ContactEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ContactEntry>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<ContactEntry> items) => JsonSerializer.Serialize(items);
}

public partial class Contact : Entity
{
    [ObservableProperty][property: Encrypted] private string _firstName = "";
    [ObservableProperty][property: Encrypted] private string _lastName = "";
    [ObservableProperty][property: Encrypted] private string _nickname = "";
    [ObservableProperty][property: Encrypted] private string _company = "";
    [ObservableProperty][property: Encrypted] private string _jobTitle = "";
    [ObservableProperty][property: Encrypted] private string _emails = "";
    [ObservableProperty][property: Encrypted] private string _phones = "";
    [ObservableProperty][property: Encrypted] private string _address = "";
    [ObservableProperty][property: Encrypted] private string _website = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private DateTime? _birthday;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _color = "teal";

    [NotMapped] public string FullName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            if (!string.IsNullOrEmpty(name)) return name;
            if (!string.IsNullOrWhiteSpace(Nickname)) return Nickname;
            return string.IsNullOrWhiteSpace(Company) ? "Unnamed contact" : Company;
        }
    }

    // Sorted and grouped by the displayed name, so the letter headers always follow the list order.
    [NotMapped] public string SortKey => FullName.ToUpperInvariant();
    [NotMapped] public string GroupLetter => FullName.Length > 0 && char.IsLetter(FullName[0]) ? char.ToUpperInvariant(FullName[0]).ToString() : "#";

    [NotMapped] public string Subtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(JobTitle) && !string.IsNullOrWhiteSpace(Company)) return $"{JobTitle} · {Company}";
            if (!string.IsNullOrWhiteSpace(Company)) return Company;
            if (!string.IsNullOrWhiteSpace(JobTitle)) return JobTitle;
            var email = ContactEntry.Parse(Emails).FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(email)) return email;
            return ContactEntry.Parse(Phones).FirstOrDefault()?.Value ?? "";
        }
    }

    [NotMapped] public string BirthdayText
    {
        get
        {
            if (!Birthday.HasValue) return "";
            var next = NextBirthday;
            var days = (next - DateTime.Today).Days;
            var when = days == 0 ? "today 🎉" : days == 1 ? "tomorrow" : $"in {days} days";
            return $"{Birthday.Value:d MMMM} · {when}";
        }
    }

    [NotMapped] public DateTime NextBirthday
    {
        get
        {
            if (!Birthday.HasValue) return DateTime.MaxValue;
            var b = Birthday.Value;
            var today = DateTime.Today;
            var next = SafeDate(today.Year, b.Month, b.Day);
            if (next < today) next = SafeDate(today.Year + 1, b.Month, b.Day);
            return next;
        }
    }

    [NotMapped] public int? AgeNext => Birthday.HasValue && Birthday.Value.Year > 1900 ? NextBirthday.Year - Birthday.Value.Year : null;

    private static DateTime SafeDate(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    partial void OnFirstNameChanged(string value) => RaiseNames();
    partial void OnLastNameChanged(string value) => RaiseNames();
    partial void OnNicknameChanged(string value) => RaiseNames();
    partial void OnCompanyChanged(string value) => RaiseNames();
    partial void OnJobTitleChanged(string value) => RaiseNames();
    partial void OnBirthdayChanged(DateTime? value) => OnPropertyChanged(nameof(BirthdayText));

    private void RaiseNames()
    {
        OnPropertyChanged(nameof(FullName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(GroupLetter));
    }
}
