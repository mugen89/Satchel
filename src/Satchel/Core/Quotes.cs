namespace Satchel.Core;

public sealed record Quote(string Text, string Author);

/// <summary>Daily quotes for the dashboard (kept offline; one per day, the same all day).</summary>
public static class Quotes
{
    public static IReadOnlyList<Quote> All { get; } =
    [
        new("The unexamined life is not worth living.", "Socrates"),
        new("Waste no more time arguing what a good man should be. Be one.", "Marcus Aurelius"),
        new("The impediment to action advances action. What stands in the way becomes the way.", "Marcus Aurelius"),
        new("Very little is needed to make a happy life; it is all within yourself, in your way of thinking.", "Marcus Aurelius"),
        new("Dwell on the beauty of life. Watch the stars, and see yourself running with them.", "Marcus Aurelius"),
        new("It is not that we have a short time to live, but that we waste a lot of it.", "Seneca"),
        new("We suffer more often in imagination than in reality.", "Seneca"),
        new("Begin at once to live, and count each separate day as a separate life.", "Seneca"),
        new("While we are postponing, life speeds by.", "Seneca"),
        new("Difficulties strengthen the mind, as labour does the body.", "Seneca"),
        new("Men are disturbed not by things, but by the views which they take of things.", "Epictetus"),
        new("First say to yourself what you would be; and then do what you have to do.", "Epictetus"),
        new("Wealth consists not in having great possessions, but in having few wants.", "Epictetus"),
        new("The journey of a thousand miles begins with a single step.", "Laozi"),
        new("Nature does not hurry, yet everything is accomplished.", "Laozi"),
        new("Real knowledge is to know the extent of one's ignorance.", "Confucius"),
        new("Happiness depends upon ourselves.", "Aristotle"),
        new("Well begun is half done.", "Aristotle"),
        new("He who has a why to live can bear almost any how.", "Friedrich Nietzsche"),
        new("I think, therefore I am.", "René Descartes"),
        new("Lost time is never found again.", "Benjamin Franklin"),
        new("Well done is better than well said.", "Benjamin Franklin"),
        new("Energy and persistence conquer all things.", "Benjamin Franklin"),
        new("Do what you can, with what you have, where you are.", "Theodore Roosevelt"),
        new("Nothing great was ever achieved without enthusiasm.", "Ralph Waldo Emerson"),
        new("Our life is frittered away by detail. Simplify, simplify.", "Henry David Thoreau"),
        new("The question is not what you look at, but what you see.", "Henry David Thoreau"),
        new("The best way out is always through.", "Robert Frost"),
        new("Forever is composed of nows.", "Emily Dickinson"),
        new("“Hope” is the thing with feathers that perches in the soul.", "Emily Dickinson"),
        new("Perfection is achieved not when there is nothing more to add, but when there is nothing left to take away.", "Antoine de Saint-Exupéry"),
        new("What is essential is invisible to the eye.", "Antoine de Saint-Exupéry"),
        new("All we have to decide is what to do with the time that is given us.", "J. R. R. Tolkien"),
        new("Not all those who wander are lost.", "J. R. R. Tolkien"),
        new("Imagination is more important than knowledge.", "Albert Einstein"),
        new("Life is like riding a bicycle. To keep your balance you must keep moving.", "Albert Einstein"),
        new("Genius is one per cent inspiration and ninety-nine per cent perspiration.", "Thomas Edison"),
        new("A merry heart doeth good like a medicine.", "Proverbs 17:22"),
        new("To every thing there is a season.", "Ecclesiastes 3:1"),
        new("Fall seven times, stand up eight.", "Japanese proverb"),
        new("The best time to plant a tree was twenty years ago. The second best time is now.", "Chinese proverb"),
        new("Be not afraid of growing slowly; be afraid only of standing still.", "Chinese proverb"),
        new("Little by little, one goes far.", "Spanish proverb"),
        new("If you want to go fast, go alone. If you want to go far, go together.", "African proverb"),
        new("Know thyself.", "Inscription at Delphi"),
    ];

    /// <summary>The same quote all day; changes at midnight.</summary>
    public static int IndexForToday() => (DateTime.Today.DayOfYear + DateTime.Today.Year * 7) % All.Count;
}
