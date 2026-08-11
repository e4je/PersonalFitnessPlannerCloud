using System.Reflection;
using System.Text.Json;
using PersonalFitnessPlanner.Contracts;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Data;

public sealed class DefaultPlanLoader
{
    private const string ResourceName = "PersonalFitnessPlanner.DefaultPlan.json";

    public async Task<PlanData> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"嵌入资源 {ResourceName} 不存在。");
        var document = await JsonSerializer.DeserializeAsync<CanonicalPlanDocument>(
                stream,
                ContractJson.Options,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("默认训练计划为空。");

        Validate(document);
        var plan = Map(document);
        Validate(plan);
        return plan;
    }

    private static PlanData Map(CanonicalPlanDocument document) => new(
        document.PlanVersionId,
        document.PlanId,
        document.Name,
        document.Version,
        document.Status,
        document.AdaptationWeeks,
        document.AdaptationSets,
        document.Days.OrderBy(day => day.Order).Select(day => new PlanDayData(
            day.Code,
            day.Name,
            day.Slots.OrderBy(slot => slot.Order).Select(slot => new PlanItemData(
                slot.SlotId,
                slot.Order,
                slot.MuscleGroup,
                slot.Cues,
                slot.CommonMistakes,
                slot.Options.OrderBy(option => option.Order).Select(option => new ExerciseOptionData(
                    option.OptionId,
                    option.ExerciseId,
                    option.ExerciseName,
                    option.Equipment,
                    option.IsPrimary,
                    option.Sets,
                    option.RepMin,
                    option.RepMax,
                    option.RepUnit,
                    option.RestSeconds,
                    option.EquipmentId)).ToArray(),
                slot.SeatPosition,
                slot.BenchAngle,
                slot.MachineNumber)).ToArray())).ToArray(),
        document.PublishedAt,
        document.WeeklyStrengthTarget,
        document.MinimumRestDays,
        document.FatigueThreshold);

    private static void Validate(CanonicalPlanDocument document)
    {
        if (document.PlanId == Guid.Empty || document.PlanVersionId == Guid.Empty ||
            document.Version <= 0 || string.IsNullOrWhiteSpace(document.Name) ||
            string.IsNullOrWhiteSpace(document.Status))
        {
            throw new InvalidDataException("默认计划的计划/版本 UUID、名称、状态或版本号无效。");
        }

        if (document.AdaptationWeeks < 0 || document.AdaptationSets <= 0)
        {
            throw new InvalidDataException("默认计划的适应期配置无效。");
        }

        if (document.WeeklyStrengthTarget is < 1 or > 7 ||
            document.MinimumRestDays is < 0 or > 14 ||
            document.FatigueThreshold is < 1 or > 10)
        {
            throw new InvalidDataException("默认计划的周训练目标、最少休息日或疲劳阈值无效。");
        }

        if (document.Days.Count != 2 ||
            document.Days.Select(day => day.DayId).Any(id => id == Guid.Empty) ||
            document.Days.Select(day => day.DayId).Distinct().Count() != document.Days.Count)
        {
            throw new InvalidDataException("默认计划必须包含两个具有唯一 UUID 的训练日。");
        }

        var slotIds = new HashSet<Guid>();
        var optionIds = new HashSet<Guid>();
        foreach (var day in document.Days)
        {
            if (day.Slots.Any(slot => !slot.Enabled))
            {
                throw new InvalidDataException($"默认训练日 {day.Code} 不允许包含停用槽位。");
            }

            foreach (var slot in day.Slots)
            {
                if (slot.SlotId == Guid.Empty || !slotIds.Add(slot.SlotId) ||
                    !string.Equals(slot.SlotCode, $"{day.Code.ToUpperInvariant()}{slot.Order:00}", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 的槽位 UUID 或编码无效。");
                }

                if (slot.PrimaryExerciseId == Guid.Empty || slot.Sets <= 0 || slot.RepMin <= 0 ||
                    slot.RepMax < slot.RepMin || slot.RestSeconds < 0 || slot.AdaptationSets <= 0 ||
                    string.IsNullOrWhiteSpace(slot.MuscleGroup) || string.IsNullOrWhiteSpace(slot.Cues))
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 的槽位处方无效。");
                }

                if (slot.Options.Any(option => !option.Enabled))
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 不允许包含停用动作选项。");
                }

                var optionOrders = slot.Options.Select(option => option.Order).Order().ToArray();
                if (!optionOrders.SequenceEqual(Enumerable.Range(1, slot.Options.Count)))
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 的动作选项顺序必须从 1 连续编号。");
                }

                foreach (var option in slot.Options)
                {
                    if (option.OptionId == Guid.Empty || !optionIds.Add(option.OptionId) ||
                        option.ExerciseId == Guid.Empty || option.EquipmentId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(option.ExerciseName) || string.IsNullOrWhiteSpace(option.Equipment) ||
                        string.IsNullOrWhiteSpace(option.RepUnit) || option.Sets <= 0 || option.RepMin <= 0 ||
                        option.RepMax < option.RepMin || option.RestSeconds < 0 ||
                        option.RirMin < 0 || option.RirMax < option.RirMin)
                    {
                        throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 存在无效动作选项。");
                    }
                }

                var primaryOptions = slot.Options.Where(option => option.IsPrimary).ToArray();
                var primary = primaryOptions.FirstOrDefault();
                if (primaryOptions.Length != 1 || primary is null ||
                    primary.ExerciseId != slot.PrimaryExerciseId ||
                    primary.Sets != slot.Sets || primary.RepMin != slot.RepMin || primary.RepMax != slot.RepMax ||
                    !string.Equals(primary.RepUnit, slot.RepUnit, StringComparison.Ordinal) ||
                    primary.RestSeconds != slot.RestSeconds)
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {slot.Order} 的首选动作与槽位处方不一致。");
                }
            }
        }
    }

    private static void Validate(PlanData plan)
    {
        var days = plan.Days.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        if (days.Count != 2 || !days.ContainsKey("A") || !days.ContainsKey("B"))
        {
            throw new InvalidDataException("默认计划必须且只能包含 A、B 两个训练日。");
        }

        foreach (var day in days.Values)
        {
            var positions = day.Items.Select(x => x.Position).Order().ToArray();
            if (!positions.SequenceEqual(Enumerable.Range(1, 8)))
            {
                throw new InvalidDataException($"训练日 {day.Code} 必须包含不重复的 1-8 共八个位置。");
            }

            foreach (var item in day.Items)
            {
                if (item.Options.Count == 0 || item.Options.Count(x => x.IsPreferred) != 1)
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {item.Position} 必须有且只有一个首选动作。");
                }

                if (item.Options.Select(option => option.ExerciseId).Distinct().Count() != item.Options.Count)
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {item.Position} 不能重复引用同一动作 UUID。");
                }

                if (item.Options.Any(x => x.Sets <= 0 || x.RepMin <= 0 || x.RepMax < x.RepMin || x.RestSeconds < 0))
                {
                    throw new InvalidDataException($"训练日 {day.Code} 位置 {item.Position} 存在无效组次。");
                }
            }
        }
    }

    private sealed class CanonicalPlanDocument
    {
        public Guid PlanId { get; init; }
        public Guid PlanVersionId { get; init; }
        public int Version { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int AdaptationWeeks { get; init; }
        public int AdaptationSets { get; init; }
        public int WeeklyStrengthTarget { get; init; }
        public int MinimumRestDays { get; init; }
        public int FatigueThreshold { get; init; }
        public DateTimeOffset? PublishedAt { get; init; }
        public IReadOnlyList<CanonicalPlanDay> Days { get; init; } = [];
    }

    private sealed class CanonicalPlanDay
    {
        public Guid DayId { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Order { get; init; }
        public IReadOnlyList<CanonicalPlanSlot> Slots { get; init; } = [];
    }

    private sealed class CanonicalPlanSlot
    {
        public Guid SlotId { get; init; }
        public string SlotCode { get; init; } = string.Empty;
        public int Order { get; init; }
        public string MuscleGroup { get; init; } = string.Empty;
        public Guid PrimaryExerciseId { get; init; }
        public int Sets { get; init; }
        public int RepMin { get; init; }
        public int RepMax { get; init; }
        public string RepUnit { get; init; } = string.Empty;
        public int RestSeconds { get; init; }
        public string Cues { get; init; } = string.Empty;
        public string CommonMistakes { get; init; } = string.Empty;
        public int AdaptationSets { get; init; }
        public bool Enabled { get; init; }
        public string SeatPosition { get; init; } = string.Empty;
        public string BenchAngle { get; init; } = string.Empty;
        public string MachineNumber { get; init; } = string.Empty;
        public IReadOnlyList<CanonicalPlanOption> Options { get; init; } = [];
    }

    private sealed class CanonicalPlanOption
    {
        public Guid OptionId { get; init; }
        public Guid ExerciseId { get; init; }
        public string ExerciseName { get; init; } = string.Empty;
        public Guid EquipmentId { get; init; }
        public string Equipment { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
        public int Order { get; init; }
        public int Sets { get; init; }
        public int RepMin { get; init; }
        public int RepMax { get; init; }
        public string RepUnit { get; init; } = string.Empty;
        public int RestSeconds { get; init; }
        public int RirMin { get; init; }
        public int RirMax { get; init; }
        public bool PerSide { get; init; }
        public bool Enabled { get; init; }
    }
}
