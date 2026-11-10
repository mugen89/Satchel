using System.Windows;
using System.Windows.Controls;

namespace Satchel.Behaviors;

/// <summary>Two-way bindable password + "has text" flag for placeholder triggers.</summary>
public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssistant),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject o) => (string)o.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject o, string v) => o.SetValue(BoundPasswordProperty, v);

    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false, OnAttachChanged));

    public static bool GetAttach(DependencyObject o) => (bool)o.GetValue(AttachProperty);
    public static void SetAttach(DependencyObject o, bool v) => o.SetValue(AttachProperty, v);

    public static readonly DependencyProperty HasPasswordProperty = DependencyProperty.RegisterAttached(
        "HasPassword", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false));

    public static bool GetHasPassword(DependencyObject o) => (bool)o.GetValue(HasPasswordProperty);
    public static void SetHasPassword(DependencyObject o, bool v) => o.SetValue(HasPasswordProperty, v);

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false));

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnPasswordChanged;
        if (e.NewValue is true)
        {
            pb.PasswordChanged += OnPasswordChanged;
            SetHasPassword(pb, pb.Password.Length > 0);
        }
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        if (!GetAttach(pb)) SetAttach(pb, true);
        if ((bool)pb.GetValue(UpdatingProperty)) return;
        var value = e.NewValue as string ?? "";
        if (pb.Password != value) pb.Password = value;
        SetHasPassword(pb, value.Length > 0);
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        SetHasPassword(pb, pb.Password.Length > 0);
        pb.SetValue(UpdatingProperty, true);
        SetBoundPassword(pb, pb.Password);
        pb.SetValue(UpdatingProperty, false);
    }
}
