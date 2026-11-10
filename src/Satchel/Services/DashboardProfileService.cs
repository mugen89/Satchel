namespace Satchel.Services;

/// <summary>
/// Dashboard profiles: named setups of widgets (which ones, their order and size), each with an optional icon.
/// Profiles with an icon get a shortcut in the top bar; Alt+1…9 switches to any of them.
/// </summary>
public sealed class DashboardProfileService
{
    private readonly SettingsService _settings;

    public DashboardProfileService(SettingsService settings) => _settings = settings;

    private AppSettings S => _settings.Current;

    /// <summary>A profile was added, removed, renamed or given an icon, or another one became active.</summary>
    public event Action? Changed;

    public IReadOnlyList<DashboardProfile> All
    {
        get
        {
            Ensure();
            return S.DashboardProfiles;
        }
    }

    public DashboardProfile Active
    {
        get
        {
            Ensure();
            return S.DashboardProfiles.FirstOrDefault(p => p.Id == S.ActiveDashboard) ?? S.DashboardProfiles[0];
        }
    }

    private void Ensure()
    {
        if (S.DashboardProfiles.Count > 0) return;
        // First start with profiles: the layout you already had becomes "Main".
        var main = new DashboardProfile { Name = "Main", Layout = S.DashboardLayout.Select(Clone).ToList() };
        S.DashboardProfiles = [main];
        S.DashboardLayout = [];
        S.ActiveDashboard = main.Id;
        _settings.ScheduleSave();
    }

    public bool NameExists(string name, DashboardProfile? except = null) =>
        All.Any(p => !ReferenceEquals(p, except) && string.Equals(p.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public void Activate(string id)
    {
        if (Active.Id == id || All.All(p => p.Id != id)) return;
        S.ActiveDashboard = id;
        Changed?.Invoke();
    }

    /// <summary>Switches to the n-th profile (0-based, in list order). False when there's no such profile.</summary>
    public bool ActivateAt(int index)
    {
        if (index < 0 || index >= All.Count) return false;
        Activate(All[index].Id);
        return true;
    }

    /// <summary>Adds a profile and makes it the active one.</summary>
    public DashboardProfile Add(string name, string icon, IEnumerable<WidgetState> layout)
    {
        var profile = new DashboardProfile { Name = name.Trim(), Icon = icon, Layout = layout.Select(Clone).ToList() };
        S.DashboardProfiles = [.. All, profile];
        S.ActiveDashboard = profile.Id;
        _settings.ScheduleSave();
        Changed?.Invoke();
        return profile;
    }

    public void Update(DashboardProfile profile, string name, string icon)
    {
        profile.Name = name.Trim();
        profile.Icon = icon;
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>Removes a profile (never the last one). The first remaining profile becomes active if needed.</summary>
    public bool Remove(DashboardProfile profile)
    {
        if (All.Count <= 1 || !All.Contains(profile)) return false;
        S.DashboardProfiles = All.Where(p => !ReferenceEquals(p, profile)).ToList();
        if (S.ActiveDashboard == profile.Id) S.ActiveDashboard = S.DashboardProfiles[0].Id;
        _settings.ScheduleSave();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Stores the active profile's widget layout (no <see cref="Changed"/> event: nothing about the profile list changed).</summary>
    public void SaveLayout(IEnumerable<WidgetState> layout)
    {
        Active.Layout = layout.Select(Clone).ToList();
        _settings.ScheduleSave();
    }

    public static WidgetState Clone(WidgetState w) => new() { Key = w.Key, Visible = w.Visible, Span = w.Span, Height = w.Height };
}
