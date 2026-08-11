using System.Text.Json;

namespace PersonalFitnessPlanner.Contracts;

public sealed record BootstrapDto
{
    public UserDto? User { get; init; }

    public PlanVersionDto? CurrentPlan { get; init; }

    public IReadOnlyList<PlanVersionDto> PlanVersions { get; init; }
        = Array.Empty<PlanVersionDto>();

    public IReadOnlyList<ExerciseDto> Exercises { get; init; } = Array.Empty<ExerciseDto>();

    public IReadOnlyList<EquipmentDto> Equipment { get; init; } = Array.Empty<EquipmentDto>();

    public IReadOnlyList<PlanAssignmentDto> Assignments { get; init; }
        = Array.Empty<PlanAssignmentDto>();

    public IReadOnlyList<WorkoutSessionDto> WorkoutSessions { get; init; }
        = Array.Empty<WorkoutSessionDto>();

    public IReadOnlyList<ReadinessDto> Readiness { get; init; } = Array.Empty<ReadinessDto>();

    public IReadOnlyList<CardioSessionDto> CardioSessions { get; init; }
        = Array.Empty<CardioSessionDto>();

    public string? Cursor { get; init; }

    public string? SyncCursor { get; init; }
}

public sealed record SyncChangeDto
{
    public Guid Id { get; init; }

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public string Operation { get; init; } = "UPSERT";

    public long Version { get; init; } = 1;

    public Dictionary<string, JsonElement?>? Payload { get; init; }

    public DateTimeOffset? ChangedAt { get; init; }
}

public sealed record SyncChangesDto : CursorPageDto
{
    public IReadOnlyList<SyncChangeDto> Changes { get; init; } = Array.Empty<SyncChangeDto>();
}

public sealed record SyncOperationDto
{
    public Guid Id { get; init; }

    public string IdempotencyKey { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }

    public string Operation { get; init; } = "UPSERT";

    public Dictionary<string, JsonElement?>? Payload { get; init; }
}

public sealed record SyncBatchRequestDto
{
    public Guid BatchId { get; init; }

    public DateTimeOffset SentAt { get; init; }

    public IReadOnlyList<SyncOperationDto> Operations { get; init; }
        = Array.Empty<SyncOperationDto>();
}

public sealed record SyncBatchItemResultDto
{
    public Guid Id { get; init; }

    public Guid? ClientOutboxId { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? Error { get; init; }

    public long? ServerVersion { get; init; }

    public Dictionary<string, JsonElement?>? ServerCopy { get; init; }
}

public sealed record SyncBatchResponseDto
{
    public Guid? BatchId { get; init; }

    public IReadOnlyList<SyncBatchItemResultDto> Results { get; init; }
        = Array.Empty<SyncBatchItemResultDto>();

    public string? Cursor { get; init; }
}
