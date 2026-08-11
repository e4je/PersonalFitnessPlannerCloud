using System.Text.Json.Serialization;

namespace PersonalFitnessPlanner.Contracts;

/// <summary>
/// Common metadata carried by every synchronizable server-owned resource.
/// UUIDs are represented as <see cref="Guid"/> and timestamps as ISO-8601
/// <see cref="DateTimeOffset"/> values on the wire.
/// </summary>
public abstract record CloudEntityDto
{
    public Guid Id { get; init; }

    public long Version { get; init; } = 1;

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed record ApiMessageDto
{
    public string Message { get; init; } = string.Empty;
}

public sealed record ApiErrorDto
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string[]> Errors { get; init; }
        = new Dictionary<string, string[]>();

    public string? TraceId { get; init; }
}

public sealed record UserDto : CloudEntityDto
{
    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Timezone { get; init; } = "UTC";

    public string WeightUnit { get; init; } = "KG";

    /// <summary>
    /// Informational roles returned by the server. Authorization decisions must
    /// still be made from authenticated token claims, not from a UI setting.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}

public sealed record EquipmentDto : CloudEntityDto
{
    public string Name { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string? Brand { get; init; }

    public string? Model { get; init; }

    public string? Notes { get; init; }
}

public sealed record ExerciseAlternativeDto : CloudEntityDto
{
    public Guid ExerciseId { get; init; }

    public Guid AlternativeExerciseId { get; init; }

    public int SortOrder { get; init; }
}

public sealed record ExerciseDto : CloudEntityDto
{
    public string Name { get; init; } = string.Empty;

    public string BodyPart { get; init; } = string.Empty;

    public Guid? EquipmentId { get; init; }

    public int DefaultSets { get; init; }

    public int RepMin { get; init; }

    public int RepMax { get; init; }

    public string RepUnit { get; init; } = "reps";

    public string Cues { get; init; } = string.Empty;

    public string CommonMistakes { get; init; } = string.Empty;

    public int DefinitionVersion { get; init; } = 1;

    public IReadOnlyList<ExerciseAlternativeDto> Alternatives { get; init; }
        = Array.Empty<ExerciseAlternativeDto>();
}

public abstract record CursorPageDto
{
    public string? Cursor { get; init; }

    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }

    public bool FullResyncRequired { get; init; }
}

public sealed record ExercisePageDto : CursorPageDto
{
    public IReadOnlyList<ExerciseDto> Items { get; init; } = Array.Empty<ExerciseDto>();
}

public sealed record EquipmentPageDto : CursorPageDto
{
    public IReadOnlyList<EquipmentDto> Items { get; init; } = Array.Empty<EquipmentDto>();
}
