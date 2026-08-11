namespace PersonalFitnessPlanner.Core;

public static class UnitConverter
{
    public const double PoundsPerKilogram = 2.2046226218487757;

    public static double KilogramsToPounds(double kilograms)
    {
        ValidateFiniteNonNegative(kilograms, nameof(kilograms));
        return kilograms * PoundsPerKilogram;
    }

    public static double PoundsToKilograms(double pounds)
    {
        ValidateFiniteNonNegative(pounds, nameof(pounds));
        return pounds / PoundsPerKilogram;
    }

    public static double Convert(double value, UnitSystem from, UnitSystem to) =>
        (from, to) switch
        {
            _ when from == to => ValidateAndReturn(value),
            (UnitSystem.Kilograms, UnitSystem.Pounds) => KilogramsToPounds(value),
            (UnitSystem.Pounds, UnitSystem.Kilograms) => PoundsToKilograms(value),
            _ => throw new ArgumentOutOfRangeException(nameof(to), to, "Unknown unit system."),
        };

    /// <summary>Rounds to the nearest available plate/stack increment.</summary>
    public static double RoundToIncrement(double value, double increment)
    {
        ValidateFiniteNonNegative(value, nameof(value));
        if (!double.IsFinite(increment) || increment <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), increment, "Increment must be positive.");
        }

        return Math.Round(value / increment, MidpointRounding.AwayFromZero) * increment;
    }

    private static double ValidateAndReturn(double value)
    {
        ValidateFiniteNonNegative(value, nameof(value));
        return value;
    }

    private static void ValidateFiniteNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Value must be finite and non-negative.");
        }
    }
}

public static class TrainingVolumeCalculator
{
    /// <summary>Returns kg-repetitions. Missing weight/reps and invalid negative values contribute zero.</summary>
    public static double CalculateSetVolume(double? weightKg, int? reps)
    {
        if (weightKg is null || reps is null || weightKg < 0 || reps < 0 || !double.IsFinite(weightKg.Value))
        {
            return 0;
        }

        return weightKg.Value * reps.Value;
    }

    public static double CalculateSessionVolume(
        IEnumerable<WorkoutSet> sets,
        bool includeWarmups = false)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return sets
            .Where(set => set.Completed && (includeWarmups || !set.IsWarmup) && set.DeletedAt is null)
            .Sum(set => CalculateSetVolume(set.WeightKg, set.Reps));
    }

    public static double ConvertVolumeFromKilograms(double kilogramVolume, UnitSystem displayUnit)
    {
        if (!double.IsFinite(kilogramVolume) || kilogramVolume < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kilogramVolume),
                kilogramVolume,
                "Volume must be finite and non-negative.");
        }

        return displayUnit == UnitSystem.Pounds
            ? kilogramVolume * UnitConverter.PoundsPerKilogram
            : kilogramVolume;
    }
}
