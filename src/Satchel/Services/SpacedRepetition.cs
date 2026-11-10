using Satchel.Models;

namespace Satchel.Services;

/// <summary>
/// Scheduling for flashcards, in the spirit of Anki's SM-2: new cards go through short learning steps (1 and 10 minutes),
/// then graduate to day intervals that grow with the card's ease. "Again" on a learned card sends it back to relearning.
/// </summary>
public static class SpacedRepetition
{
    private static readonly double[] LearnSteps = [1, 10];   // minutes
    private const double RelearnStep = 10;                    // minutes
    private const double GraduatingInterval = 1, EasyInterval = 4;  // days
    private const double MinEase = 1.3;

    /// <summary>What the four buttons would do, for the "Good · 3d" labels.</summary>
    public static string Preview(Flashcard card, int grade)
    {
        var copy = new Flashcard { State = card.State, Ease = card.Ease, IntervalDays = card.IntervalDays, Step = card.Step, Reps = card.Reps, Lapses = card.Lapses };
        var due = Apply(copy, grade, DateTime.Now, fuzz: false);
        var span = due - DateTime.Now;
        return span.TotalMinutes < 60 ? $"{Math.Max(1, Math.Round(span.TotalMinutes))} min"
             : span.TotalHours < 24 ? $"{Math.Round(span.TotalHours)} h"
             : span.TotalDays < 30 ? $"{Math.Round(span.TotalDays)} d"
             : span.TotalDays < 365 ? $"{span.TotalDays / 30:0.#} mo"
             : $"{span.TotalDays / 365:0.#} y";
    }

    /// <summary>Updates the card for an answer (1 again, 2 hard, 3 good, 4 easy) and returns its next due time.</summary>
    public static DateTime Apply(Flashcard c, int grade, DateTime now, bool fuzz = true)
    {
        grade = Math.Clamp(grade, 1, 4);
        c.Reps++;
        c.LastReviewedAt = now;
        DateTime due;
        switch (c.State)
        {
            case CardState.New:
            case CardState.Learning:
                c.State = CardState.Learning;
                if (grade == 1) { c.Step = 0; due = now.AddMinutes(LearnSteps[0]); }
                else if (grade == 2) { due = now.AddMinutes(c.Step == 0 ? (LearnSteps[0] + LearnSteps[1]) / 2 : LearnSteps[Math.Min(c.Step, LearnSteps.Length - 1)]); }
                else if (grade == 3)
                {
                    c.Step++;
                    if (c.Step >= LearnSteps.Length) return Graduate(c, GraduatingInterval, now, fuzz);
                    due = now.AddMinutes(LearnSteps[c.Step]);
                }
                else return Graduate(c, EasyInterval, now, fuzz);
                break;

            case CardState.Relearning:
                if (grade == 1) { due = now.AddMinutes(RelearnStep); }
                else if (grade == 2) { due = now.AddMinutes(RelearnStep * 1.5); }
                else
                {
                    c.State = CardState.Review;
                    c.IntervalDays = Math.Max(1, grade == 4 ? c.IntervalDays * 1.5 : c.IntervalDays);
                    due = now.Date.AddDays(Fuzz(c.IntervalDays, fuzz)).AddHours(4);
                }
                break;

            default: // Review
                if (grade == 1)
                {
                    c.Lapses++;
                    c.State = CardState.Relearning;
                    c.Ease = Math.Max(MinEase, c.Ease - 0.2);
                    c.IntervalDays = Math.Max(1, Math.Round(c.IntervalDays * 0.5));
                    due = now.AddMinutes(RelearnStep);
                }
                else
                {
                    double interval = Math.Max(1, c.IntervalDays);
                    if (grade == 2) { interval *= 1.2; c.Ease = Math.Max(MinEase, c.Ease - 0.15); }
                    else if (grade == 3) interval *= c.Ease;
                    else { interval *= c.Ease * 1.3; c.Ease += 0.15; }
                    c.IntervalDays = Math.Min(3650, Math.Max(c.IntervalDays + 1, Math.Round(interval)));
                    due = now.Date.AddDays(Fuzz(c.IntervalDays, fuzz)).AddHours(4);
                }
                break;
        }
        c.DueAt = due;
        return due;
    }

    private static DateTime Graduate(Flashcard c, double days, DateTime now, bool fuzz)
    {
        c.State = CardState.Review;
        c.Step = 0;
        c.IntervalDays = days;
        var due = now.Date.AddDays(Fuzz(days, fuzz)).AddHours(4);
        c.DueAt = due;
        return due;
    }

    /// <summary>A little randomness so cards learned together don't stay together forever.</summary>
    private static double Fuzz(double days, bool fuzz)
    {
        if (!fuzz || days < 3) return days;
        double spread = Math.Max(1, days * 0.05);
        return Math.Round(days + (Random.Shared.NextDouble() * 2 - 1) * spread);
    }
}
