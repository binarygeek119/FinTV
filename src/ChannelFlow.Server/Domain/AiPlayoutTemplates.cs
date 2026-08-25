namespace FinTv.Domain;

/// <summary>
/// Built-in AI playout templates defining daypart structure for 48-slot daily lineups.
/// </summary>
public static class AiPlayoutTemplates
{
    public const string NoneId = "none";

    public const string ClassicCableId = "classic-cable";
    public const string GetLearnededId = "get-learneded";
    public const string SlappyComedyId = "slappy-comedy";

    public const int LateNightStartSlot = 44;
    public const int LateNightEndSlot = 3;
    public const int EarlyBirdStartSlot = 4;
    public const int EarlyBirdEndSlot = 9;
    public const int BeforeSchoolStartSlot = 10;
    public const int BeforeSchoolEndSlot = 11;
    public const int MorningStartSlot = 12;
    public const int MorningEndSlot = 23;
    public const int MidDayStartSlot = 24;
    public const int MidDayEndSlot = 29;
    public const int AfterSchoolStartSlot = 30;
    public const int AfterSchoolEndSlot = 33;
    public const int TeenHourStartSlot = 34;
    public const int TeenHourEndSlot = 35;
    public const int PrimeTimeStartSlot = 36;
    public const int PrimeTimeEndSlot = 43;
    public const int ToonTakeoverStartSlot = 34;
    public const int ToonTakeoverEndSlot = 39;

    public static bool UsesNetworkClock(string? templateId)
        => templateId is ClassicCableId or GetLearnededId or SlappyComedyId;

    private static IReadOnlyList<AiPlayoutDaypart> CreateNetworkClockDayparts(
        string lateNight,
        string earlyBird,
        string beforeSchool,
        string morning,
        string midDay,
        string afterSchool,
        string teenHour,
        string primeTime,
        IReadOnlyList<AiPlayoutDaypart>? extras)
    {
        var dayparts = new List<AiPlayoutDaypart>
        {
            new(LateNightStartSlot, LateNightEndSlot, "Late Night", lateNight),
            new(EarlyBirdStartSlot, EarlyBirdEndSlot, "Early Bird Reruns", earlyBird),
            new(BeforeSchoolStartSlot, BeforeSchoolEndSlot, "Before School", beforeSchool),
            new(MorningStartSlot, MorningEndSlot, "Morning", morning, maxSpanSlots: 4),
            new(MidDayStartSlot, MidDayEndSlot, "Mid-Day", midDay),
            new(AfterSchoolStartSlot, AfterSchoolEndSlot, "After School", afterSchool),
            new(TeenHourStartSlot, TeenHourEndSlot, "Teen Hour", teenHour),
            new(PrimeTimeStartSlot, PrimeTimeEndSlot, "Prime Time", primeTime, maxSpanSlots: 8)
        };

        if (extras is { Count: > 0 })
        {
            dayparts.AddRange(extras);
        }

        return dayparts;
    }

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
            Id = ClassicCableId,
            Name = "Classic Cable Dayparts",
            Description = "Network clock: Early Bird encore of yesterday Prime Time, morning through teen blocks, 6–10pm flagship, and late night wrapping midnight.",
            Dayparts = CreateNetworkClockDayparts(
                lateNight: "22:00-02:00 uncut / edgy / experimental. Prefer TV-MA or UR series and R/UR movies. No kids content.",
                earlyBird: "02:00-05:00 encore. Mark as rerun slots (kind rerun). ChannelFlow repeats yesterday's Prime Time (18:00-22:00). Do not assign catalog titles.",
                beforeSchool: "05:00-06:00 high-energy cartoons to wake the audience. Prefer TV-Y7/TV-G and G/PG.",
                morning: "06:00-12:00 educational, family-friendly programming. Prefer TV-G/TV-PG and G/PG.",
                midDay: "12:00-15:00 thematic / casual viewing. No reruns. Prefer TV-PG/TV-14 and PG/PG-13.",
                afterSchool: "15:00-17:00 all-ages cartoons and broad-appeal animation. Prefer TV-Y7/TV-G and G/PG.",
                teenHour: "17:00-18:00 content for ages 13-19. Prefer TV-PG/TV-14 and PG/PG-13.",
                primeTime: "18:00-22:00 flagship shows and main-event programming. Prefer TV-14 to TV-MA and PG-13 to R.",
                extras: null)
        },
        new AiPlayoutTemplate
        {
            Id = "kids-all-day",
            Name = "Kids All Day",
            Description = "OpenSwim: Nickelodeon, Disney, Fox Kids, and Cartoon Network style kids programming all day.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Reruns",
                    "Midnight-6:00am. Mark as rerun slots (kind rerun). ChannelFlow repeats yesterday's kids primetime. Do not assign catalog titles."),
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
            Description = "Movie-network clock: daytime features, primetime double features, and sticky Friday/Saturday nights.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Features",
                    "Late-night and overnight movies. Shorter or cult titles are fine."),
                new AiPlayoutDaypart(12, 29, "Daytime Features",
                    "Afternoon movies. Keep a repeating weekday pattern when possible."),
                new AiPlayoutDaypart(30, 37, "Early Fringe",
                    "Lead-in features before primetime."),
                new AiPlayoutDaypart(38, 47, "Primetime Double Feature",
                    "Evening movies. Friday and Saturday nights should feel like event night. Use spanSlots from runtime.", maxSpanSlots: 8)
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
                    "Midnight-6:00am encore. Mark as rerun slots (kind rerun). ChannelFlow repeats yesterday's primetime game shows. Do not assign catalog titles."),
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
            Id = GetLearnededId,
            Name = "GET LEARNEDED · Ed TV Blocks",
            Description = "126.2 GET LEARNEDED: educational network clock from Early Bird lecture encore through late-night deep dives.",
            Dayparts = CreateNetworkClockDayparts(
                lateNight: "22:00-02:00 complex sociology, dark history, and metaphysical theories. Prefer TV-PG/TV-14.",
                earlyBird: "02:00-05:00 encore. Mark as rerun slots (kind rerun). ChannelFlow repeats yesterday's Prime Time lectures. Do not assign catalog titles.",
                beforeSchool: "05:00-06:00 Fact of the Day animated shorts. Prefer TV-Y/G.",
                morning: "06:00-12:00 science for kids and basic tutorials. Prefer TV-G/G.",
                midDay: "12:00-15:00 nature and history documentaries. No reruns. Prefer TV-G/PG.",
                afterSchool: "15:00-17:00 educational cartoons. Prefer TV-G/PG.",
                teenHour: "17:00-18:00 advanced study guides and philosophy. Prefer TV-PG/PG-13.",
                primeTime: "18:00-22:00 university-level lectures and deep-dive essays. Prefer TV-PG/PG.",
                extras: null)
        },
        new AiPlayoutTemplate
        {
            Id = "past-tense-news",
            Name = "Past Tense News · Breaking Shuffle",
            Description = "124.1 Past Tense News: random home-movie clips presented as live breaking coverage.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 47, "Breaking News Day",
                    "Fill every slot with random clips from the Past Tense News library. Do not keep chronological order. Present every story as if it is breaking live right now. Match spanSlots to clip length; pack multiple short clips into a 30-minute block.", maxSpanSlots: 2)
            ]
        },
        new AiPlayoutTemplate
        {
            Id = SlappyComedyId,
            Name = "Slappy · Comedy + Slappy's Toon Takeover",
            Description = "124.3 Slappy: comedy network clock with a Friday 5–8pm kid-cartoon Toon Takeover.",
            Dayparts = CreateNetworkClockDayparts(
                lateNight: "22:00-02:00 uncensored adult stand-up and surrealist experimental comedy. Prefer TV-MA/UR.",
                earlyBird: "02:00-05:00 encore. Mark as rerun slots (kind rerun). ChannelFlow repeats yesterday's Prime Time. Do not assign catalog titles.",
                beforeSchool: "05:00-06:00 classic slapstick animated shorts. Prefer TV-G/G.",
                morning: "06:00-12:00 clean stand-up and family sketches. Prefer TV-G/G.",
                midDay: "12:00-15:00 thematic sitcom marathons. No reruns. Prefer TV-PG/PG.",
                afterSchool: "15:00-17:00 all-ages comedy cartoons. Prefer TV-G/PG.",
                teenHour: "17:00-18:00 Mon-Thu teen sitcoms and edgy sketch comedy. Prefer TV-PG/TV-14. Friday 5-8pm is Toon Takeover instead.",
                primeTime: "18:00-22:00 Mon-Thu roast specials and uncensored sketch. Prefer TV-MA/R. Friday 5-8pm is Toon Takeover; 8-10pm continues uncensored comedy.",
                extras:
                [
                    new AiPlayoutDaypart(
                        ToonTakeoverStartSlot,
                        ToonTakeoverEndSlot,
                        "Slappy's Toon Takeover",
                        "Friday only, 5:00-8:00pm (slots 34-39): 3-hour kid-cartoon event (TV-Y7/TV-G, G/PG all-ages animation). Use days [\"fri\"]. Not adult animation and not Mon-Thu. Bumper plays Friday 5-8pm only.",
                        maxSpanSlots: 4)
                ])
        },
        new AiPlayoutTemplate
        {
            Id = "holiday-channel",
            Name = "Holiday Channel · Seasonal Marathon",
            Description = "Holiday TV episodes and movies mixed like a seasonal cable network, not a random dump.",
            Dayparts =
            [
                new AiPlayoutDaypart(0, 11, "Overnight Holiday",
                    "Late-night holiday episodes or movies matching the active holiday."),
                new AiPlayoutDaypart(12, 29, "Daytime Holiday",
                    "Holiday TV episode blocks (2-4) mixed with family holiday movies."),
                new AiPlayoutDaypart(30, 47, "Primetime Holiday",
                    "Flagship holiday movies and specials in the evening. Repeat titles at the same times through the week when it fits.", maxSpanSlots: 8)
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

    public static AiPlayoutDaypart? GetToonTakeoverDaypart(Channel channel)
    {
        var template = Resolve(channel);
        return template.Dayparts.FirstOrDefault(d =>
            d.Name.Contains("toon takeover", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsToonTakeoverSlot(Channel channel, int slotIndex, DateOnly? date = null)
    {
        var daypart = GetToonTakeoverDaypart(channel);
        if (daypart is null || !daypart.ContainsSlot(slotIndex))
        {
            return false;
        }

        return date?.DayOfWeek == DayOfWeek.Friday;
    }

    /// <summary>
    /// Primetime wall-clock slots for original-broadcast simulation.
    /// Names containing "prime" win; otherwise 18:00–22:00 (slots 36–43).
    /// </summary>
    public static (int StartSlotIndex, int EndSlotIndex) GetPrimetimeSlotRange(Channel channel)
    {
        var template = Resolve(channel);
        var prime = template.Dayparts.FirstOrDefault(d =>
            d.Name.Contains("prime", StringComparison.OrdinalIgnoreCase)
            && !d.Name.Contains("rerun", StringComparison.OrdinalIgnoreCase)
            && !d.Name.Contains("toon takeover", StringComparison.OrdinalIgnoreCase));
        if (prime is not null && prime.StartSlotIndex <= prime.EndSlotIndex)
        {
            return (prime.StartSlotIndex, prime.EndSlotIndex);
        }

        return (PrimeTimeStartSlot, PrimeTimeEndSlot);
    }

    public static bool IsPrimetimeSlot(int slotIndex, int startSlotIndex, int endSlotIndex)
        => slotIndex >= startSlotIndex && slotIndex <= endSlotIndex;

    public static string? GetDaypartNameForSlot(AiPlayoutTemplate? template, int slotIndex, DayOfWeek? day = null)
    {
        var daypart = GetDaypartForSlot(template, slotIndex, day);
        return daypart?.Name;
    }

    public static AiPlayoutDaypart? GetDaypartForSlot(AiPlayoutTemplate? template, int slotIndex, DayOfWeek? day = null)
    {
        if (template is null || template.Dayparts.Count == 0)
        {
            return null;
        }

        if (day == DayOfWeek.Friday)
        {
            var takeover = template.Dayparts.FirstOrDefault(d =>
                d.Name.Contains("toon takeover", StringComparison.OrdinalIgnoreCase));
            if (takeover?.ContainsSlot(slotIndex) == true)
            {
                return takeover;
            }
        }

        foreach (var candidate in template.Dayparts)
        {
            if (candidate.Name.Contains("toon takeover", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.ContainsSlot(slotIndex))
            {
                return candidate;
            }
        }

        return template.Dayparts.FirstOrDefault(d => d.ContainsSlot(slotIndex));
    }

    public static int SlotsRemainingInDaypart(AiPlayoutTemplate? template, int startSlotIndex, DayOfWeek? day = null)
    {
        var daypart = GetDaypartForSlot(template, startSlotIndex, day);
        if (daypart is null)
        {
            return 48 - startSlotIndex;
        }

        var remaining = 0;
        for (var i = startSlotIndex; i < 48; i++)
        {
            if (!daypart.ContainsSlot(i))
            {
                break;
            }

            remaining++;
        }

        return Math.Max(1, remaining);
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

        if (template.Dayparts.Any(d => d.Name.Contains("rerun", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add(UsesNetworkClock(template.Id)
                ? "- Early Bird Reruns must use kind \"rerun\" (no catalog titles). ChannelFlow fills them with yesterday's Prime Time (18:00-22:00)."
                : "- Overnight Reruns dayparts must use kind \"rerun\" (no catalog titles). ChannelFlow fills them with yesterday's primetime.");
        }

        if (UsesNetworkClock(template.Id))
        {
            lines.Add("- The table ratings are preferences, not hard catalog cuts. Prefer those ratings; if the library lacks them, pick the closest available title from this catalog. Do not invent titles.");
            lines.Add("- Mid-Day is original programming, not rerun slots.");
            lines.Add("- Do not place kids content in Late Night.");
            lines.Add("- Do not place adult-only titles in Before School, Morning, or After School.");
            lines.Add("- Do not run a series block across a daypart boundary. Typical series blocks are 1-2 episodes, not multi-hour dumps of the same show.");
            lines.Add("- Put movies in Prime Time when this channel's daypart guide asks for features/blockbusters. Do not fill Late Night with Saturday-morning cartoons.");
        }

        if (template.Id is "kids-all-day")
        {
            lines.Add("- Do not place kids content in Late Night or adult-only dayparts.");
            lines.Add("- Do not place adult-only titles in Morning Cartoons or After School blocks.");
            lines.Add("- No release year cap; classic and modern kid-rated titles are equally eligible.");
            lines.Add("- Prefer Nickelodeon, Disney Channel, Fox Kids, and Cartoon Network style cartoons and live-action kids shows.");
        }

        if (template.Id is "movie-marathon")
        {
            lines.Add("- Build a weekly movie grid with sticky clock times. Friday/Saturday nights are event double-features.");
            lines.Add("- Do not dump movies in chronological order from slot 0; schedule them like a movie channel.");
        }
        if (template.Id is "holiday-channel")
        {
            lines.Add("- Mix holiday TV episode blocks and holiday movies. Keep repeating titles at the same times.");
            lines.Add("- Only use catalog titles that match the active holiday.");
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
        => template.Id is not ("music-videos" or "past-tense-news");

    private static string BuildSeriesEpisodeBlockingSection(AiPlayoutTemplate template)
    {
        var marathonSlots = GetMarathonDaypartHint(template);
        return string.Join('\n', new[]
        {
            "Series episode blocking:",
            "- For TV series, use consecutive slots with the same jellyfinItemId; ChannelFlow plays the next episode in order for each consecutive slot.",
            "- Typical blocks: 1-2 consecutive episodes of the same series (1-2 back-to-back slots with the same jellyfinItemId). Use spanSlots=1 per slot for ~30-minute episodes, or spanSlots=2 for hour-long episodes. Do not cross a daypart boundary.",
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
