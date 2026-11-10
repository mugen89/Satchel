using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Satchel.Behaviors;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>Password field with a reveal toggle and two-way bindable Password.</summary>
public partial class PasswordInput : UserControl, PasswordInputHost
{
    private bool _syncing;

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(PasswordInput),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordPropertyChanged)
        { DefaultUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });

    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PasswordInput), new PropertyMetadata(""));
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public static readonly DependencyProperty ShowRevealProperty = DependencyProperty.Register(
        nameof(ShowReveal), typeof(bool), typeof(PasswordInput), new PropertyMetadata(true, (d, e) =>
            ((PasswordInput)d).Reveal.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed));
    public bool ShowReveal { get => (bool)GetValue(ShowRevealProperty); set => SetValue(ShowRevealProperty, value); }

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(PasswordInput), new PropertyMetadata(false, (d, e) =>
            ((PasswordInput)d).Reveal.IsChecked = e.NewValue is true));
    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }

    public PasswordInput()
    {
        InitializeComponent();
        MinHeight = 36;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                // Make sure the binding has the latest value before an Enter-bound command runs.
                GetBindingExpression(PasswordProperty)?.UpdateSource();
            }
        };
    }

    public void FocusInput()
    {
        var target = Reveal.IsChecked == true ? (Control)Txt : Pwd;
        target.Focus();
        Keyboard.Focus(target);
    }

    private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (PasswordInput)d;
        if (c._syncing) return;
        c._syncing = true;
        var value = e.NewValue as string ?? "";
        if (c.Pwd.Password != value) c.Pwd.Password = value;
        if (c.Txt.Text != value) c.Txt.Text = value;
        c._syncing = false;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Txt.Text = Pwd.Password;
        Password = Pwd.Password;
        _syncing = false;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Pwd.Password = Txt.Text;
        Password = Txt.Text;
        _syncing = false;
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        bool show = Reveal.IsChecked == true;
        IsRevealed = show;
        Reveal.Content = show ? Glyphs.Hide : Glyphs.View;
        bool hadFocus = Pwd.IsKeyboardFocusWithin || Txt.IsKeyboardFocusWithin;
        Txt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Pwd.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (hadFocus)
        {
            if (show) { Txt.Focus(); Txt.CaretIndex = Txt.Text.Length; }
            else Pwd.Focus();
        }
    }
}
