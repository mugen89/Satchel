using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class FlashcardsView : UserControl
{
    private FlashcardsViewModel? _vm;

    public FlashcardsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.FocusNewCard -= OnFocusNewCard;
            }
            _vm = e.NewValue as FlashcardsViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.FocusNewCard += OnFocusNewCard;
        };
    }

    private void OnFocusNewCard() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FrontBox.Focus());

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FlashcardsViewModel.IsStudying) when _vm?.IsStudying == true:
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
                break;
            case nameof(FlashcardsViewModel.IsFlipped):
            case nameof(FlashcardsViewModel.Current):
                FlipAnimation();
                break;
        }
    }

    /// <summary>A quick squeeze-and-open, like turning the card over.</summary>
    private void FlipAnimation()
    {
        if (!Anim.Enabled) return;
        CardFlip.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(0.86, 1, Anim.Ms(220)) { EasingFunction = Anim.EaseOutQuint });
        CardFace.BeginAnimation(OpacityProperty, Anim.To(1, 200, null, from: 0.4));
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e) => _vm?.FlipCommand.Execute(null);

    /// <summary>Space flips, 1–4 grade, Esc ends the session.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is not { IsStudying: true } vm) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;
        switch (e.Key)
        {
            case Key.Space or Key.Enter:
                if (vm.IsFinished) vm.EndStudyCommand.Execute(null);
                else if (!vm.IsFlipped) vm.FlipCommand.Execute(null);
                else vm.AnswerCommand.Execute("3");
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3 or Key.D4 or Key.NumPad4:
                if (vm.IsFlipped)
                {
                    var n = e.Key is Key.D1 or Key.NumPad1 ? "1" : e.Key is Key.D2 or Key.NumPad2 ? "2" : e.Key is Key.D3 or Key.NumPad3 ? "3" : "4";
                    vm.AnswerCommand.Execute(n);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                vm.EndStudyCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Ctrl+Enter in the add boxes adds the card.</summary>
    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _vm?.AddCardCommand.Execute(null);
        }
    }
}
