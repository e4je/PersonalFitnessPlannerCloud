using System.Text.Json.Serialization;

namespace PersonalFitnessPlanner.Contracts;

public sealed record PlanSlotOptionDto : CloudEntityDto
{
    public Guid PlanSlotId { get; init; }

    public Guid ExerciseId { get; init; }

    public Guid? EquipmentId { get; init; }

    public bool IsPreferred { get; init; }

    public int SortOrder { get; init; }

    public int SetCount { get; init; }

    public int IntroSetCount { get; init; } = 2;

    public int IntroWeeks { get; init; } = 2;

    public int RepMin { get; init; }

    public int RepMax { get; init; }

    public string RepUnit { get; init; } = "reps";

    public int RirMin { get; init; } = 2;

    public int RirMax { get; init; } = 3;

    public int RestSeconds { get; init; } = 90;
}

public sealed record PlanSlotDto : CloudEntityDto
{
    public Guid PlanDayId { get; init; }

    public int Position { get; init; }

    public string BodyPart { get; init; } = string.Empty;

    public string Cues { get; init; } = string.Empty;

    public string CommonMistakes { get; init; } = string.Empty;

    public string SeatPosition { get; init; } = string.Empty;

    public string BenchAngle { get; init; } = string.Empty;

    public string MachineNumber { get; init; } = string.Empty;

    public IReadOnlyList<PlanSlotOptionDto> Options { get; init; }
        = Array.Empty<PlanSlotOptionDto>();
}

public sealed record PlanDayDto : CloudEntityDto
{
    public Guid PlanVersionId { get; init; }

    public string Code { get; init; } = "A";

    public string Name { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public IReadOnlyList<PlanSlotDto> Slots { get; init; } = Array.Empty<PlanSlotDto>();
}

public sealed record PlanVersionDto : CloudEntityDto
{
    public Guid PlanId { get; init; }

    public string PlanName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public int VersionNumber { get; init; } = 1;

    public string Status { get; init; } = "published";

    public int WeeklyFrequency { get; init; } = 3;

    public int MinRestDays { get; init; } = 1;

    public int FatigueThreshold { get; init; } = 8;

    [JsonPropertyName("initial_reduced_weeks")]
    public int IntroWeeks { get; init; } = 2;

    [JsonPropertyName("initial_set_count")]
    public int IntroMaxSets { get; init; } = 2;

    public DateTimeOffset? PublishedAt { get; init; }

    public string? SnapshotJson { get; init; }

    public IReadOnlyList<PlanDayDto> Days { get; init; } = Array.Empty<PlanDayDto>();
}

public sealed record PlanAssignmentDto : CloudEntityDto
{
    public Guid UserId { get; init; }

    public Guid PlanVersionId { get; init; }

    public DateOnly StartLocalDate { get; init; }

    public DateOnly? EndLocalDate { get; init; }

    public bool IsActive { get; init; } = true;
}
