namespace SlotGuard;

internal static class ResourceTypes
{
    public const string Other = "other";
    public const string Room = "room";
    public const string Equipment = "equipment";
    public const string Studio = "studio";

    public static bool IsValid(string type) => type is Room or Equipment or Studio or Other;
}

internal sealed record ResourceDetails(string? Type, int? Capacity, string[]? Equipment)
{
    public bool IsValid() => Type is not null && ResourceTypes.IsValid(Type)
        && Capacity is null or >= 1 and <= 10000 && ValidEquipment(Equipment);

    public Resource ApplyTo(Resource resource)
    {
        var equipment = NormalizeEquipment(Equipment);
        return resource with
        {
            Type = Type!,
            Capacity = Capacity,
            Equipment = equipment.SequenceEqual(resource.Equipment) ? resource.Equipment : equipment
        };
    }

    public static bool ValidEquipment(string[]? values) => values is null ||
        values.Length <= 12 && values.All(value => value?.Trim().Length is > 0 and <= 40
            && !value.Contains(',') && !value.Any(char.IsControl));

    public static string[] NormalizeEquipment(string[]? values) => (values ?? [])
        .Select(value => value.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
}
