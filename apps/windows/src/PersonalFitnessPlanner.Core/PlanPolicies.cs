using System.Security.Claims;
using System.Text.Json;

namespace PersonalFitnessPlanner.Core;

public sealed class PlanVersionPolicy
{
    private readonly PlanValidator _validator;

    public PlanVersionPolicy(PlanValidator? validator = null)
    {
        _validator = validator ?? new PlanValidator();
    }

    public bool CanEdit(TrainingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Status == PlanStatus.Draft && plan.DeletedAt is null;
    }

    public bool CanAssign(TrainingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Status == PlanStatus.Published && plan.DeletedAt is null;
    }

    public void EnsureEditable(TrainingPlan plan)
    {
        if (!CanEdit(plan))
        {
            throw new InvalidOperationException(
                "Published, archived, or deleted plan versions are immutable. Create a new draft version instead.");
        }
    }

    public void EnsureAssignable(TrainingPlan plan)
    {
        if (!CanAssign(plan))
        {
            throw new InvalidOperationException("Only a published, non-deleted plan version can be assigned.");
        }
    }

    public TrainingPlan CreateNextDraft(
        TrainingPlan source,
        Guid? newVersionId = null,
        DateTimeOffset? now = null,
        Func<Guid>? idFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Status == PlanStatus.Draft)
        {
            throw new InvalidOperationException("The source is already editable; a new version is not required.");
        }

        if (source.DeletedAt is not null)
        {
            throw new InvalidOperationException("A deleted plan version cannot be used as a version source.");
        }

        var createId = idFactory ?? Guid.NewGuid;
        var versionId = newVersionId ?? createId();
        if (versionId == Guid.Empty)
        {
            throw new ArgumentException("New version UUID cannot be empty.", nameof(newVersionId));
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var days = source.Days
            .Select(day => day with
            {
                Id = RequireGeneratedId(createId(), "plan day"),
                Items = day.Items
                    .Select(item => item with
                    {
                        Id = RequireGeneratedId(createId(), "plan item"),
                        Options = item.Options
                            .Select(option => option with
                            {
                                Id = RequireGeneratedId(createId(), "exercise option"),
                            })
                            .ToArray(),
                    })
                    .ToArray(),
            })
            .ToArray();

        return source with
        {
            Id = versionId,
            VersionNumber = checked(source.VersionNumber + 1),
            Status = PlanStatus.Draft,
            Days = days,
            PublishedAt = null,
            SnapshotJson = null,
            EntityVersion = 1,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            DeletedAt = null,
        };
    }

    public TrainingPlan Publish(TrainingPlan draft, DateTimeOffset? publishedAt = null)
    {
        EnsureEditable(draft);
        _validator.EnsureValid(draft);
        var timestamp = publishedAt ?? DateTimeOffset.UtcNow;
        return draft with
        {
            Status = PlanStatus.Published,
            PublishedAt = timestamp,
            UpdatedAt = timestamp,
            EntityVersion = checked(draft.EntityVersion + 1),
        };
    }

    private static Guid RequireGeneratedId(Guid id, string resource)
    {
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException($"The UUID factory returned an empty UUID for a {resource}.");
        }

        return id;
    }
}

public sealed class ExerciseOptionSelector
{
    public ExerciseOption Select(
        PlanItem item,
        Guid? requestedOptionId = null,
        IEnumerable<Guid>? unavailableExerciseIds = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var unavailable = unavailableExerciseIds is null
            ? new HashSet<Guid>()
            : new HashSet<Guid>(unavailableExerciseIds);

        if (requestedOptionId is { } optionId)
        {
            var requested = item.Options.FirstOrDefault(option => option.Id == optionId)
                ?? throw new ArgumentException(
                    "The requested option does not belong to this plan item.",
                    nameof(requestedOptionId));

            if (unavailable.Contains(requested.ExerciseId))
            {
                throw new InvalidOperationException("The requested exercise is currently unavailable.");
            }

            return requested;
        }

        return item.Options
            .Where(option => !unavailable.Contains(option.ExerciseId))
            .OrderByDescending(option => option.IsPreferred)
            .ThenBy(option => option.SortOrder)
            .ThenBy(option => option.Id)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No available exercise option exists for this plan item.");
    }

    public ExerciseOption SelectByExerciseId(
        PlanItem item,
        Guid exerciseId,
        IEnumerable<Guid>? unavailableExerciseIds = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var option = item.Options.FirstOrDefault(candidate => candidate.ExerciseId == exerciseId)
            ?? throw new ArgumentException(
                "The requested exercise is not a valid option for this plan item.",
                nameof(exerciseId));
        return Select(item, option.Id, unavailableExerciseIds);
    }

    public bool TrySelect(
        PlanItem item,
        out ExerciseOption? selected,
        Guid? requestedOptionId = null,
        IEnumerable<Guid>? unavailableExerciseIds = null)
    {
        try
        {
            selected = Select(item, requestedOptionId, unavailableExerciseIds);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            selected = null;
            return false;
        }
    }
}

public static class IntroSetPolicy
{
    public static int GetEffectiveSetCount(
        ExerciseOption option,
        DateOnly assignmentStartDate,
        DateOnly workoutDate)
    {
        ArgumentNullException.ThrowIfNull(option);
        return GetEffectiveSetCount(
            option.SetCount,
            option.IntroSetCount,
            option.IntroWeeks,
            assignmentStartDate,
            workoutDate);
    }

    public static int GetEffectiveSetCount(
        TrainingPlan plan,
        ExerciseOption option,
        DateOnly assignmentStartDate,
        DateOnly workoutDate)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(option);
        return GetEffectiveSetCount(
            option.SetCount,
            Math.Min(option.IntroSetCount, plan.IntroMaxSets),
            Math.Min(option.IntroWeeks, plan.IntroWeeks),
            assignmentStartDate,
            workoutDate);
    }

    public static int GetEffectiveSetCount(
        int fullSetCount,
        int introSetCount,
        int introWeeks,
        DateOnly assignmentStartDate,
        DateOnly workoutDate)
    {
        if (fullSetCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fullSetCount), "Full set count must be positive.");
        }

        if (introSetCount <= 0 || introSetCount > fullSetCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(introSetCount),
                "Intro set count must be positive and no greater than the full set count.");
        }

        if (introWeeks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(introWeeks), "Intro weeks cannot be negative.");
        }

        if (workoutDate < assignmentStartDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workoutDate),
                "Workout date cannot precede the plan assignment start date.");
        }

        return IsIntroPeriod(assignmentStartDate, workoutDate, introWeeks)
            ? introSetCount
            : fullSetCount;
    }

    public static bool IsIntroPeriod(
        DateOnly assignmentStartDate,
        DateOnly workoutDate,
        int introWeeks = 2)
    {
        if (introWeeks <= 0 || workoutDate < assignmentStartDate)
        {
            return false;
        }

        return workoutDate.DayNumber - assignmentStartDate.DayNumber < checked(introWeeks * 7);
    }
}

public static class AuthorizationPolicy
{
    private static readonly HashSet<string> AdministratorRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "administrator",
        "super_admin",
        "superadmin",
    };

    public static bool IsAdministrator(IEnumerable<string> authenticatedTokenRoles)
    {
        ArgumentNullException.ThrowIfNull(authenticatedTokenRoles);
        return authenticatedTokenRoles
            .SelectMany(SplitRoleValues)
            .Any(AdministratorRoles.Contains);
    }

    public static bool IsAdministrator(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var roleValues = principal.Claims
            .Where(claim =>
                claim.Type == ClaimTypes.Role ||
                claim.Type.Equals("role", StringComparison.OrdinalIgnoreCase) ||
                claim.Type.Equals("roles", StringComparison.OrdinalIgnoreCase))
            .Select(claim => claim.Value);
        return IsAdministrator(roleValues);
    }

    public static void DemandAdministrator(ClaimsPrincipal principal)
    {
        if (!IsAdministrator(principal))
        {
            throw new UnauthorizedAccessException(
                "This operation requires an administrator role from the authenticated token.");
        }
    }

    private static IEnumerable<string> SplitRoleValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(trimmed) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        return trimmed.Split(
            new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public static class WorkoutSnapshotPolicy
{
    public static Guid ResolvePlanVersion(
        Guid? existingWorkoutPlanVersionId,
        Guid activeAssignmentPlanVersionId)
    {
        if (activeAssignmentPlanVersionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The active assignment plan-version ID cannot be empty.",
                nameof(activeAssignmentPlanVersionId));
        }

        return existingWorkoutPlanVersionId is { } existing && existing != Guid.Empty
            ? existing
            : activeAssignmentPlanVersionId;
    }

    public static bool HasValidSnapshot(WorkoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.PlanSnapshotJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(session.PlanSnapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void EnsureValidSnapshot(WorkoutSession session)
    {
        if (!HasValidSnapshot(session))
        {
            throw new InvalidOperationException(
                "A workout must preserve the exact plan-version snapshot as a JSON object.");
        }
    }

    public static void EnsureSnapshotUnchanged(
        WorkoutSession original,
        WorkoutSession updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        if (original.Id != updated.Id)
        {
            throw new ArgumentException("Workout IDs do not match.", nameof(updated));
        }

        if (original.PlanVersionId != updated.PlanVersionId ||
            !string.Equals(
                original.PlanSnapshotJson,
                updated.PlanSnapshotJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workout plan version and historical snapshot are immutable after the workout starts.");
        }
    }
}
