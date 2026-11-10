using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Satchel.Core;
using Satchel.Security;

namespace Satchel.Controls;

/// <summary>Live TOTP code with a countdown ring. Click to copy.</summary>
public sealed class TotpBadge : Border
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static event Action? Tick;

    static TotpBadge()
    {
        Timer.Tick += (_, _) => Tick?.Invoke();
    }

    private readonly TextBlock _code = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _seconds = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _ring = new() { Width = 26, Height = 26, Thickness = 3 };
    private TotpConfig? _config;
    private long _counter = -1;

    public static readonly DependencyProperty SecretProperty = DependencyProperty.Register(
        nameof(Secret), typeof(string), typeof(TotpBadge), new PropertyMetadata(null, (d, _) => ((TotpBadge)d).OnSecretChanged()));
    public string? Secret { get => (string?)GetValue(SecretProperty); set => SetValue(SecretProperty, value); }

    public string CurrentCode { get; private set; } = "";

    public TotpBadge()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Click to copy the one-time code";
        Background = Brushes.Transparent;
        _code.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _code.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _seconds.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var ringHost = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        ringHost.Children.Add(_ring);
        ringHost.Children.Add(_seconds);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_code);
        panel.Children.Add(ringHost);
        Child = panel;

        Loaded += (_, _) => { Tick += Update; if (!Timer.IsEnabled) Timer.Start(); _counter = -1; Update(); };
        Unloaded += (_, _) => Tick -= Update;
        MouseLeftButtonUp += (_, e) =>
        {
            if (!string.IsNullOrEmpty(CurrentCode))
            {
                SecureClipboard.Copy(CurrentCode, sensitive: true, "One-time code copied");
                Anim.Pop(_code, 1.08);
            }
            e.Handled = true;
        };
    }

    private void OnSecretChanged()
    {
        _config = Totp.Parse(Secret);
        _counter = -1;
        Update();
    }

    private void Update()
    {
        if (_config == null)
        {
            _code.Text = string.IsNullOrWhiteSpace(Secret) ? "" : "Invalid secret";
            _code.FontSize = string.IsNullOrWhiteSpace(Secret) ? 18 : 12;
            _seconds.Text = "";
            CurrentCode = "";
            return;
        }
        _code.FontSize = 18;
        var now = DateTimeOffset.UtcNow;
        long counter = now.ToUnixTimeSeconds() / _config.Period;
        int remaining = Totp.SecondsRemaining(_config, now);
        _seconds.Text = remaining.ToString();
        bool urgent = remaining <= 5;
        _ring.SetResourceReference(ProgressRing.RingBrushProperty, urgent ? "DangerBrush" : "AccentBrush");

        if (counter != _counter)
        {
            _counter = counter;
            CurrentCode = Totp.Compute(_config, now);
            _code.Text = Totp.Pretty(CurrentCode);
            double exactRemaining = _config.Period - (now.ToUnixTimeMilliseconds() / 1000.0 % _config.Period);
            _ring.Countdown(exactRemaining / _config.Period, TimeSpan.FromSeconds(exactRemaining));
            if (IsLoaded) Anim.EnterUp(_code, 6, 220);
        }
    }
}
