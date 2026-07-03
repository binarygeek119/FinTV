namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Built-in AI playout templates defining daypart structure for 48-slot daily lineups.
/// </summary>
public static class AiPlayoutTemplates
{
    public const string NoneId = "none";

    private static readonly IReadOnlyList<AiPlayoutTemplate> All =
    [
        new AiPlayoutTemplate
        {
            Id = NoneId,
            Name = "No template",
            Description = "Flat schedule using channel rules only; no daypart structure."
        },
        new AiPlayoutTemplate
        {
            Id = "classic-cable",
            Name = "Classic Cable Dayparts",
            Description = "Morning cartoons through late-night adult animation with primetime block.",
            Dayparts =
            [
                new AiPlayoutDaypart(44, 11, "Late Night",
                    "Reruns, reshows, replays; adult-oriented animated comedy (e.g. Family Guy, South Park, Rick and Morty). No kids content."),
                new AiPlayoutDaypart(12, 17, "Morning Cartoons",
                    "Kids cartoons and animated series for young children."),
                new AiPlayoutDaypart(18, 29, "Daytime TV",
                    "General daytime TV: talk, lifestyle, sitcom reruns, game shows as appropriate to channel rules."),
                new AiPlayoutDaypart(30, 33, "After School Cartoons",
                    "Kids and tween animated shows for after-school audience."),
                new AiPlayoutDaypart(34, 37, "Teen Hour",
                    "Teen-oriented TV and animation; not preschool content."),
                new AiPlayoutDaypart(38, 43, "Primetime",
                    "Flagship primetime shows and movies; longer spanSlots allowed for features.", maxSpanSlots: 8)
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "kids-all-day",
            Name = "Kids All Day",
            Description = "Youth-focused programming across most of the day with lighter primetime.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight",
                    "Gentle reruns or preschool-safe content only; no adult themes."),
                new AiPlayoutDaypart(12, 33, "Kids Block",
                    "Kids and family shows and movies appropriate for children."),
                new AiPlayoutDaypart(34, 37, "Tween Hour",
                    "Tween and teen-friendly shows; still no adult content."),
                new AiPlayoutDaypart(38, 47, "Family Primetime",
                    "Family-friendly primetime movies and shows; avoid adult-only titles.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "movie-marathon",
            Name = "Movie Marathon",
            Description = "Long movie blocks with minimal daypart variation.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 47, "All Day Movies",
                    "Schedule movies from the catalog; use spanSlots based on runtime (long features get multi-slot blocks).", maxSpanSlots: 8)
            ]
        }
    ];

    public static IReadOnlyList<AiPlayoutTemplate> ListAll() => All;

    public static AiPlayoutTemplate? Get(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId) || templateId.Equals(NoneId, StringComparison.OrdinalIgnoreCase))
        {
            return All[0];
        }

        return All.FirstOrDefault(t => t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
    }

    public static AiPlayoutTemplate Resolve(Channel channel)
        => Get(channel.AiPlayoutTemplateId) ?? All[0];

    public static string? GetDaypartNameForSlot(AiPlayoutTemplate? template, int slotIndex)
    {
        if (template is null || template.Dayparts.Count == 0)
        {
            return null;
        }

        foreach (var daypart in template.Dayparts)
        {
            if (daypart.ContainsSlot(slotIndex))
            {
                return daypart.Name;
            }
        }

        return null;
    }

    public static string BuildPromptSection(AiPlayoutTemplate template)
    {
        if (template.Dayparts.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            $"Playout template: {template.Name} ({template.Id})",
            "Assign catalog items only to slots within each daypart range:",
        };

        foreach (var daypart in template.Dayparts)
        {
            var spanHint = daypart.MaxSpanSlots.HasValue
                ? $"; max spanSlots {daypart.MaxSpanSlots.Value}"
                : string.Empty;
            lines.Add($"- {daypart.Name} slots {daypart.FormatSlotRange()}: {daypart.Brief}{spanHint}");
        }

        lines.Add("- Do not place kids content in Late Night or adult-only dayparts.");
        lines.Add("- Do not place adult-only titles in Morning Cartoons or After School blocks.");
        return string.Join('\n', lines);
    }
}

public class AiPlayoutTemplate
{
    public string Id { get; set; } = AiPlayoutTemplates.NoneId;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public IReadOnlyList<AiPlayoutDaypart> Dayparts { get; set; } = Array.Empty<AiPlayoutDaypart>();
}

public class AiPlayoutDaypart
{
    public AiPlayoutDaypart()
    {
    }

    public AiPlayoutDaypart(int startSlotIndex, int endSlotIndex, string name, string brief, int? maxSpanSlots = null)
    {
        StartSlotIndex = startSlotIndex;
        EndSlotIndex = endSlotIndex;
        Name = name;
        Brief = brief;
        MaxSpanSlots = maxSpanSlots;
    }

    public string Name { get; set; } = string.Empty;

    public int StartSlotIndex { get; set; }

    public int EndSlotIndex { get; set; }

    public string Brief { get; set; } = string.Empty;

    public int? MaxSpanSlots { get; set; }

    public bool ContainsSlot(int slotIndex)
    {
        if (StartSlotIndex <= EndSlotIndex)
        {
            return slotIndex >= StartSlotIndex && slotIndex <= EndSlotIndex;
        }

        return slotIndex >= StartSlotIndex || slotIndex <= EndSlotIndex;
    }

    public string FormatSlotRange()
    {
        if (StartSlotIndex <= EndSlotIndex)
        {
            return $"{StartSlotIndex}-{EndSlotIndex}";
        }

        return $"{StartSlotIndex}-47 and 0-{EndSlotIndex}";
    }
}
