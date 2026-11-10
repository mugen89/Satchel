namespace Satchel.ViewModels;

/// <summary>Gamble: the future home for trading and bets. A placeholder for now.</summary>
public sealed class GambleViewModel : PageViewModel
{
    // Pages are created with the navigator (the shell); the placeholder doesn't need it yet.
    public GambleViewModel(INavigator nav) => Navigator = nav;

    public INavigator Navigator { get; }
    public override string Title => "Gamble";
}
