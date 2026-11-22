using System.Text.Json;

namespace SlotGuard;

internal sealed class ResourceTemplate
{
    public Guid Id { get; init; }
    public Guid Revision { get; set; }
    public required string OrganizationId { get; init; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public required string Definition { get; set; }

    public TemplateSettings Read() => JsonSerializer.Deserialize<TemplateSettings>(Definition, JsonSerializerOptions.Web)!;
}

internal sealed record TemplateSettings(ResourceDetails Details, ResourceSchedule Schedule, bool RequiresApproval,
    bool LockSchedule = false, bool LockRules = false)
{
    public string Write() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    public Resource Create(Resource resource, Guid? templateId) => Details.ApplyTo(resource) with
    {
        TemplateId = templateId,
        ScheduleDefinition = SlotGuard.Schedule.Write(Schedule),
        RequiresApproval = RequiresApproval
    };

    public ResourceSchedule Manage(ResourceSchedule current) => current with
    {
        Week = LockSchedule ? Schedule.Week : current.Week,
        TimeZone = LockSchedule ? Schedule.ZoneId : current.ZoneId,
        Booking = LockRules ? Schedule.Rules : current.Rules,
        Policy = LockRules ? Schedule.Policies : current.Policies
    };
}

internal sealed record TemplateView(string Key, string Name, TemplateSettings Settings, Guid? Revision = null, int LinkedCount = 0);
internal sealed record ResourceTemplateLock(string Name, bool LockSchedule, bool LockRules);

internal static class TemplatePresets
{
    public static readonly TemplateView[] All =
    [
        new("preset:meeting", "Переговорная", new TemplateSettings(
            new ResourceDetails(ResourceTypes.Room, 8, ["видеосвязь", "доска", "экран"]),
            Week(9, 18, new BookingRules(true), new BookingPolicy(30, 30, 4, 60, 60)), false)),
        new("preset:lab", "Лаборатория", new TemplateSettings(
            new ResourceDetails(ResourceTypes.Room, 6, ["вытяжка", "рабочая станция"]),
            Week(10, 17, new BookingRules(true, 15, 15), new BookingPolicy(120, 21, 2, 180, 180)), true))
    ];

    private static ResourceSchedule Week(int from, int to, BookingRules rules, BookingPolicy policy)
    {
        var work = new DayHours([new SchedulePeriod(from * 60, to * 60)], [new SchedulePeriod(13 * 60, 14 * 60)]);
        var closed = new DayHours([], []);
        return new ResourceSchedule([work, work, work, work, work, closed, closed], [], Schedule.DefaultZoneId, rules, policy);
    }
}
