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
            Description = "OpenSwim: Nickelodeon, Disney, Fox Kids, and Cartoon Network style kids programming all day.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight",
                    "Gentle Nick/Disney preschool and cartoon reruns; no adult themes."),
                new AiPlayoutDaypart(12, 33, "Kids Block",
                    "Nickelodeon, Disney Channel, Fox Kids, and Cartoon Network style cartoons and live-action kids shows."),
                new AiPlayoutDaypart(34, 37, "Tween Hour",
                    "Tween-friendly Nick and Disney live-action and animated series."),
                new AiPlayoutDaypart(38, 47, "Family Primetime",
                    "Family-friendly kids movies and flagship cartoon blocks; avoid adult-only titles.")
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
                    "Schedule movies back-to-back from slot 0 with no gaps. Each movie starts immediately after the previous ends. Use spanSlots from runtime.", maxSpanSlots: 8)
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "music-videos",
            Name = "Music Video Rotation",
            Description = "MTV-style blocks grouped by genre or artist with heavier rotation in prime time.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Mix",
                    "Deep cuts and mellow tracks; group 2-4 short videos per block by artist or genre."),
                new AiPlayoutDaypart(12, 17, "Morning Flow",
                    "Mainstream pop and hits; most videos are 3-5 minutes so several can share a 30-minute slot when spanSlots=1."),
                new AiPlayoutDaypart(18, 33, "Afternoon Genre Blocks",
                    "Group consecutive slots by genre (rock, pop, hip hop, comedy/parody) for themed blocks."),
                new AiPlayoutDaypart(34, 43, "Prime Video Hour",
                    "Flagship videos and artist marathons; use spanSlots for long performances or extended mixes.", maxSpanSlots: 4),
                new AiPlayoutDaypart(44, 47, "Late Night",
                    "Alternative, deep cuts, or comedy/parody as appropriate to channel rules.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "youtube-pbs",
            Name = "YouTube TV · PBS Style",
            Description = "YouTube TV library only: public-television pacing with morning how-to, daytime docs, and evening prestige.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 5, "Overnight Encore",
                    "Gentle reruns of documentaries or educational series."),
                new AiPlayoutDaypart(6, 11, "Morning PBS",
                    "How-to, cooking, crafts, and children's educational shorts."),
                new AiPlayoutDaypart(12, 23, "Daytime Documentary",
                    "History, science, and nature documentaries in themed consecutive blocks."),
                new AiPlayoutDaypart(24, 29, "Afternoon Arts",
                    "Performing arts, music appreciation, and cultural programs grouped together."),
                new AiPlayoutDaypart(30, 37, "Early Evening PBS",
                    "News-magazine tone, current affairs, and public-interest programming."),
                new AiPlayoutDaypart(38, 43, "Masterpiece Hour",
                    "Long-form prestige content; use spanSlots for multi-part or long episodes.", maxSpanSlots: 4),
                new AiPlayoutDaypart(44, 47, "Late Night Encore",
                    "Repeat standout docs or shorter educational pieces.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "winning-game-shows",
            Name = "Winning · Game Show Blocks",
            Description = "126.1 Winning: group game shows by format and match block length to typical episode runtime.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Reruns",
                    "Classic quiz and panel reruns; standard 30-minute episodes use spanSlots=1."),
                new AiPlayoutDaypart(12, 17, "Morning Quick Games",
                    "Fast-paced 22-30 minute game shows; one show per slot."),
                new AiPlayoutDaypart(18, 29, "Daytime Blocks",
                    "Group related daytime game shows (wordplay block, trivia block, panel block) back-to-back."),
                new AiPlayoutDaypart(30, 33, "Afternoon Marathon",
                    "Run 2-3 episodes of the same show when episodes are ~30 minutes; hour-long episodes use spanSlots=2.", maxSpanSlots: 4),
                new AiPlayoutDaypart(34, 41, "Prime Game Hour",
                    "Flagship prime-time game shows; hour-long episodes use spanSlots=2.", maxSpanSlots: 4),
                new AiPlayoutDaypart(42, 47, "Late Night Games",
                    "Panel games, comedy quizzes, or reruns matching late-night tone.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "get-learneded",
            Name = "GET LEARNEDED · Ed TV Blocks",
            Description = "126.2 GET LEARNEDED: group educational TV and documentaries by subject across consecutive slots.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Encore",
                    "Gentle science or nature reruns."),
                new AiPlayoutDaypart(12, 17, "Morning Discovery",
                    "Science and nature for a general audience; keep the same subject across 2-4 consecutive slots."),
                new AiPlayoutDaypart(18, 23, "History Block",
                    "History Channel-style series grouped together."),
                new AiPlayoutDaypart(24, 29, "Science & Tech",
                    "Discovery, engineering, and space documentaries as a block."),
                new AiPlayoutDaypart(30, 33, "Nature Hour",
                    "Wildlife and ecology programming grouped together."),
                new AiPlayoutDaypart(34, 37, "Afternoon Deep Dive",
                    "Long documentaries; use spanSlots for feature-length content.", maxSpanSlots: 4),
                new AiPlayoutDaypart(38, 43, "Primetime Learning",
                    "Prestige docs and educational movies; longer spans allowed.", maxSpanSlots: 6),
                new AiPlayoutDaypart(44, 47, "Late Night Encore",
                    "Calm educational reruns.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "past-tense-news",
            Name = "Past Tense News · Breaking Chronology",
            Description = "124.1 Past Tense News: chronological event order presented as live breaking coverage.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 47, "Chronological News Day",
                    "Order news clips by the historical timeline of the events they cover — earliest events in morning slots, progressing through the day. Present every story as if it is breaking live right now, not archival footage. Group all coverage of the same event consecutively before advancing to the next historical moment. Match spanSlots to segment length; typical news blocks are 30-60 minutes.", maxSpanSlots: 4)
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "slappy-comedy",
            Name = "Slappy · Comedy + Slappy's Toon Takeover",
            Description = "124.3 Slappy: comedy all day with Slappy's Toon Takeover adult animation block at 6pm.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Comedy",
                    "Sitcom and comedy reruns; lighter live-action comedy."),
                new AiPlayoutDaypart(12, 17, "Morning Laughs",
                    "Sitcom blocks and comedy series reruns."),
                new AiPlayoutDaypart(18, 29, "Daytime Comedy",
                    "Comedy talk, sketch, and sitcom blocks grouped by show."),
                new AiPlayoutDaypart(30, 35, "Afternoon Comedy",
                    "Comedy movies or multi-episode sitcom marathons before primetime.", maxSpanSlots: 4),
                new AiPlayoutDaypart(36, 41, "Slappy's Toon Takeover",
                    "6pm-9pm block (slots 36-41): stack adult animated comedy back-to-back (e.g. Family Guy, American Dad, Bob's Burgers, The Simpsons, Futurama). Run 2-3 episodes of the same series consecutively; use spanSlots=2 for hour-long episodes.", maxSpanSlots: 4),
                new AiPlayoutDaypart(42, 47, "Late Night Comedy",
                    "Edgier animated or live-action comedy; adult sitcoms and late-night style comedy.")
            ]
        },
        new AiPlayoutTemplate
        {
            Id = "holiday-channel",
            Name = "Holiday Channel · Seasonal Marathon",
            Description = "203.4 The Holiday Channel: themed marathons during an active holiday window.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 47, "Holiday Marathon",
                    "Only schedule content matching the active holiday. Group episodes of the same show in blocks. Use spanSlots from movie runtime. Vary order like cable TV holiday marathons with smart rotation.", maxSpanSlots: 8)
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
            return UsesSeriesEpisodeBlocking(template)
                ? BuildSeriesEpisodeBlockingSection(template)
                : string.Empty;
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

        if (template.Id is "classic-cable" or "kids-all-day")
        {
            lines.Add("- Do not place kids content in Late Night or adult-only dayparts.");
            lines.Add("- Do not place adult-only titles in Morning Cartoons or After School blocks.");
        }

        if (template.Id is "kids-all-day")
        {
            lines.Add("- No release year cap; classic and modern kid-rated titles are equally eligible.");
            lines.Add("- Prefer Nickelodeon, Disney Channel, Fox Kids, and Cartoon Network style cartoons and live-action kids shows.");
        }

        if (template.Id is "movie-marathon" or "holiday-channel")
        {
            lines.Add("- Pack titles back-to-back from slot 0 with zero empty slots between features.");
            lines.Add("- FinTV repacks movies first in release chronological order (earliest year/date first), then other catalog items.");
        }
        else if (template.Id is not "past-tense-news")
        {
            lines.Add("- Within each daypart, schedule movies in release chronological order (earliest catalog year first).");
        }

        if (UsesSeriesEpisodeBlocking(template))
        {
            lines.Add(string.Empty);
            lines.Add(BuildSeriesEpisodeBlockingSection(template));
        }

        return string.Join('\n', lines);
    }

    private static bool UsesSeriesEpisodeBlocking(AiPlayoutTemplate template)
        => template.Id is not ("movie-marathon" or "holiday-channel" or "music-videos" or "past-tense-news");

    private static string BuildSeriesEpisodeBlockingSection(AiPlayoutTemplate template)
    {
        var marathonSlots = GetMarathonDaypartHint(template);
        return string.Join('\n', new[]
        {
            "Series episode blocking:",
            "- For TV series, use consecutive slots with the same jellyfinItemId; FinTV plays the next episode in order for each consecutive slot.",
            "- Typical blocks: 1-4 consecutive episodes of the same series (1-4 back-to-back slots with the same jellyfinItemId). Use spanSlots=1 per slot for ~30-minute episodes, or spanSlots=2 for hour-long episodes.",
            "- Mini-marathon: include exactly ONE mini-marathon per lineup — 5-6 consecutive slots (max 6 episodes) of the same series. " + marathonSlots,
            "- Keep mini-marathons rare and special (about 1-2 per week channel-wide). On this daily template include one; use lineup overrides on other weekdays if you want a second weekly marathon or none.",
            "- Between blocks, switch to a different series or movie; do not repeat the same series later the same day unless it is a different block separated by other shows.",
            "- Movies are single entries: one jellyfinItemId with spanSlots from runtime, not multi-slot episode blocks."
        });
    }

    private static string GetMarathonDaypartHint(AiPlayoutTemplate template)
    {
        var premium = template.Dayparts.FirstOrDefault(d =>
            d.Name.Contains("primetime", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("prime", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("toon takeover", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("family primetime", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("after school", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("tween", StringComparison.OrdinalIgnoreCase)
            || d.Name.Contains("kids block", StringComparison.OrdinalIgnoreCase));

        return premium is null
            ? "Place it in the channel's best flagship daypart (late afternoon or primetime)."
            : $"Place it in {premium.Name} (slots {premium.FormatSlotRange()}) or another flagship daypart.";
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
