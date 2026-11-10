using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Security;

namespace Satchel.Controls;

/// <summary>Five-segment password strength bar with label and hint.</summary>
public sealed class StrengthMeter : StackPanel
{
    private readonly Border[] _bars = new Border[5];
    private readonly TextBlock _label = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(StrengthMeter), new PropertyMetadata("", (d, _) => ((StrengthMeter)d).Update()));
    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }

    public static readonly DependencyProperty ShowHintProperty = DependencyProperty.Register(
        nameof(ShowHint), typeof(bool), typeof(StrengthMeter), new PropertyMetadata(true, (d, _) => ((StrengthMeter)d).Update()));
    public bool ShowHint { get => (bool)GetValue(ShowHintProperty); set => SetValue(ShowHintProperty, value); }

    public StrengthMeter()
    {
        var grid = new UniformGrid { Rows = 1, Columns = 5, Height = 4 };
        for (int i = 0; i < 5; i++)
        {
            var bar = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, i < 4 ? 4 : 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
            _bars[i] = bar;
            grid.Children.Add(bar);
        }
        Children.Add(grid);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        row.Children.Add(_label);
        row.Children.Add(_hint);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(row);
        Update();
    }

    private void Update()
    {
        var result = PasswordStrength.Evaluate(Password);
        string brushKey = result.Score switch
        {
            <= 1 => "DangerBrush",
            2 => "WarningBrush",
            _ => "SuccessBrush"
        };
        for (int i = 0; i < 5; i++)
        {
            bool on = result.Score >= 0 && i <= result.Score;
            _bars[i].SetResourceReference(Border.BackgroundProperty, on ? brushKey : "StrokeBrush");
            if (on && Anim.Enabled) _bars[i].BeginAnimation(OpacityProperty, Anim.To(1, 200, Anim.EaseOut, 0.4, i * 30));
        }
        _label.Text = result.Label;
        _label.SetResourceReference(TextBlock.ForegroundProperty, result.Score < 0 ? "TextMutedBrush" : brushKey);
        _hint.Text = ShowHint ? result.Hint : "";
        Visibility = Visibility.Visible;
    }
}
