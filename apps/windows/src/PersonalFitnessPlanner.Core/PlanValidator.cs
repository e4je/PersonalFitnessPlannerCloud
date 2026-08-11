namespace PersonalFitnessPlanner.Core;

public enum PlanValidationSeverity
{
    Warning,
    Error,
}

public sealed record PlanValidationIssue(
    string Code,
    string Path,
    string Message,
    PlanValidationSeverity Severity = PlanValidationSeverity.Error);

public sealed record PlanValidationResult(IReadOnlyList<PlanValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != PlanValidationSeverity.Error);

    public IReadOnlyList<PlanValidationIssue> Errors =>
        Issues.Where(issue => issue.Severity == PlanValidationSeverity.Error).ToArray();

    public IReadOnlyList<PlanValidationIssue> Warnings =>
        Issues.Where(issue => issue.Severity == PlanValidationSeverity.Warning).ToArray();
}

public sealed class PlanValidationException : Exception
{
    public PlanValidationException(PlanValidationResult result)
        : base(CreateMessage(result))
    {
        Result = result;
    }

    public PlanValidationResult Result { get; }

    private static string CreateMessage(PlanValidationResult result) =>
        "Training plan is invalid: " + string.Join(
            "; ",
            result.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));
}

public sealed class PlanValidator
{
    public PlanValidationResult Validate(TrainingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<PlanValidationIssue>();

        RequireId(plan.Id, "plan.id", issues);
        RequireId(plan.PlanId, "plan.plan_id", issues);
        if (string.IsNullOrWhiteSpace(plan.Name))
        {
            Error(issues, "plan.name_required", "plan.name", "Plan name is required.");
        }

        if (plan.VersionNumber <= 0)
        {
            Error(issues, "plan.version_invalid", "plan.version_number", "Version number must be positive.");
        }

        if (plan.IntroWeeks is < 0 or > 52)
        {
            Error(issues, "plan.intro_weeks_invalid", "plan.intro_weeks", "Intro weeks must be between 0 and 52.");
        }

        if (plan.IntroMaxSets is < 1 or > 20)
        {
            Error(issues, "plan.intro_sets_invalid", "plan.intro_max_sets", "Intro set count must be between 1 and 20.");
        }

        if (plan.Days.Count == 0)
        {
            Error(issues, "plan.days_required", "plan.days", "At least A and B plan days are required.");
            return new PlanValidationResult(issues);
        }

        var duplicateCodes = plan.Days
            .GroupBy(day => day.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var code in duplicateCodes)
        {
            Error(issues, "plan.day_code_duplicate", "plan.days", $"Plan day code '{code}' is duplicated.");
        }

        foreach (var requiredCode in new[] { "A", "B" })
        {
            if (!plan.Days.Any(day => string.Equals(day.Code.Trim(), requiredCode, StringComparison.OrdinalIgnoreCase)))
            {
                Error(
                    issues,
                    "plan.day_required",
                    "plan.days",
                    $"Plan day '{requiredCode}' is required.");
            }
        }

        for (var dayIndex = 0; dayIndex < plan.Days.Count; dayIndex++)
        {
            ValidateDay(plan.Days[dayIndex], dayIndex, issues);
        }

        return new PlanValidationResult(issues);
    }

    public void EnsureValid(TrainingPlan plan)
    {
        var result = Validate(plan);
        if (!result.IsValid)
        {
            throw new PlanValidationException(result);
        }
    }

    private static void ValidateDay(
        PlanDay day,
        int dayIndex,
        ICollection<PlanValidationIssue> issues)
    {
        var path = $"plan.days[{dayIndex}]";
        RequireId(day.Id, $"{path}.id", issues);

        if (!Enum.TryParse<PlanDayCode>(day.Code, true, out _))
        {
            Error(issues, "plan.day_code_invalid", $"{path}.code", "Plan day code must be A or B.");
        }

        if (string.IsNullOrWhiteSpace(day.Name))
        {
            Warning(issues, "plan.day_name_empty", $"{path}.name", "Plan day has no display name.");
        }

        if (day.Items.Count == 0)
        {
            Error(issues, "plan.items_required", $"{path}.items", "Plan day must contain at least one item.");
            return;
        }

        var duplicatePositions = day.Items
            .GroupBy(item => item.Position)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var position in duplicatePositions)
        {
            Error(
                issues,
                "plan.position_duplicate",
                $"{path}.items",
                $"Position {position} is duplicated.");
        }

        for (var itemIndex = 0; itemIndex < day.Items.Count; itemIndex++)
        {
            ValidateItem(day.Items[itemIndex], $"{path}.items[{itemIndex}]", issues);
        }
    }

    private static void ValidateItem(
        PlanItem item,
        string path,
        ICollection<PlanValidationIssue> issues)
    {
        RequireId(item.Id, $"{path}.id", issues);
        if (item.Position <= 0)
        {
            Error(issues, "plan.position_invalid", $"{path}.position", "Position must be positive.");
        }

        if (string.IsNullOrWhiteSpace(item.BodyPart))
        {
            Error(issues, "plan.body_part_required", $"{path}.body_part", "Body part is required.");
        }

        if (item.Options.Count == 0)
        {
            Error(issues, "plan.options_required", $"{path}.options", "At least one exercise option is required.");
            return;
        }

        if (item.Options.Count(option => option.IsPreferred) != 1)
        {
            Error(
                issues,
                "plan.preferred_option_invalid",
                $"{path}.options",
                "Exactly one preferred exercise option is required.");
        }

        var duplicateExercises = item.Options
            .GroupBy(option => option.ExerciseId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var exerciseId in duplicateExercises)
        {
            Error(
                issues,
                "plan.exercise_duplicate",
                $"{path}.options",
                $"Exercise '{exerciseId:D}' is duplicated in one position.");
        }

        for (var optionIndex = 0; optionIndex < item.Options.Count; optionIndex++)
        {
            ValidateOption(item.Options[optionIndex], $"{path}.options[{optionIndex}]", issues);
        }
    }

    private static void ValidateOption(
        ExerciseOption option,
        string path,
        ICollection<PlanValidationIssue> issues)
    {
        RequireId(option.Id, $"{path}.id", issues);
        RequireId(option.ExerciseId, $"{path}.exercise_id", issues);

        if (string.IsNullOrWhiteSpace(option.ExerciseName))
        {
            Warning(issues, "plan.exercise_name_empty", $"{path}.exercise_name", "Exercise name is empty.");
        }

        if (option.SetCount is < 1 or > 20)
        {
            Error(issues, "plan.set_count_invalid", $"{path}.set_count", "Set count must be between 1 and 20.");
        }

        if (option.IntroWeeks is < 0 or > 52)
        {
            Error(issues, "plan.intro_weeks_invalid", $"{path}.intro_weeks", "Intro weeks must be between 0 and 52.");
        }

        if (option.IntroSetCount <= 0 || option.IntroSetCount > option.SetCount)
        {
            Error(
                issues,
                "plan.intro_set_count_invalid",
                $"{path}.intro_set_count",
                "Intro set count must be positive and cannot exceed full set count.");
        }

        if (option.RepMin <= 0 || option.RepMax < option.RepMin)
        {
            Error(issues, "plan.rep_range_invalid", $"{path}.rep_range", "Rep range is invalid.");
        }

        if (string.IsNullOrWhiteSpace(option.RepUnit))
        {
            Error(issues, "plan.rep_unit_required", $"{path}.rep_unit", "Rep unit is required.");
        }

        if (option.RirMin is < 0 or > 10 || option.RirMax is < 0 or > 10 || option.RirMax < option.RirMin)
        {
            Error(issues, "plan.rir_range_invalid", $"{path}.rir_range", "RIR range must be ordered and between 0 and 10.");
        }

        if (option.RestSeconds is < 0 or > 3600)
        {
            Error(issues, "plan.rest_invalid", $"{path}.rest_seconds", "Rest seconds must be between 0 and 3600.");
        }
    }

    private static void RequireId(
        Guid id,
        string path,
        ICollection<PlanValidationIssue> issues)
    {
        if (id == Guid.Empty)
        {
            Error(issues, "plan.id_required", path, "UUID is required.");
        }
    }

    private static void Error(
        ICollection<PlanValidationIssue> issues,
        string code,
        string path,
        string message) =>
        issues.Add(new PlanValidationIssue(code, path, message));

    private static void Warning(
        ICollection<PlanValidationIssue> issues,
        string code,
        string path,
        string message) =>
        issues.Add(new PlanValidationIssue(code, path, message, PlanValidationSeverity.Warning));
}
