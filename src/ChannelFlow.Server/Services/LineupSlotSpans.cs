using FinTv.Domain;

namespace FinTv.Services;

/// <summary>
/// Treats a lineup slot's span as covering following half-hours so movies
/// (and other items longer than 30 minutes) do not leave empty continuation cells
/// or allow another title to start in the middle of the runtime.
/// </summary>
internal static class LineupSlotSpans
{
    public const int SlotsPerDay = 48;

    public const int MinutesPerSlot = 30;

    public static int ClampSpan(int slotIndex, int spanSlots)
    {
        var index = Math.Clamp(slotIndex, 0, SlotsPerDay - 1);
        return Math.Clamp(spanSlots, 1, SlotsPerDay - index);
    }

    public static int SpanFromRuntimeMinutes(int runtimeMinutes, int maxSpan = 8)
    {
        if (runtimeMinutes <= 0)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Ceiling(runtimeMinutes / (double)MinutesPerSlot), 1, maxSpan);
    }

    public static bool IsCoveredByEarlierSpan(IEnumerable<LineupSlot> slots, int slotIndex)
        => IsCoveredByEarlierSpan(
            slots.Select(s => (s.SlotIndex, s.SpanSlots)),
            slotIndex);

    public static bool IsCoveredByEarlierSpan(IEnumerable<LineupSlotDto> slots, int slotIndex)
        => IsCoveredByEarlierSpan(
            slots.Select(s => (s.SlotIndex, s.SpanSlots)),
            slotIndex);

    public static List<LineupSlot> ExpandUsingRuntimes(
        IEnumerable<LineupSlot> slots,
        IReadOnlyDictionary<Guid, int> runtimeMinutesByItemId)
        => ExpandUsingRuntimes(
            slots,
            s => s.SlotIndex,
            s => s.SpanSlots,
            (s, span) => s.SpanSlots = span,
            s => FirstItemId(s.Candidates.Select(c => c.JellyfinItemId)),
            runtimeMinutesByItemId);

    public static List<LineupSlotDto> ExpandUsingRuntimes(
        IEnumerable<LineupSlotDto> slots,
        IReadOnlyDictionary<Guid, int> runtimeMinutesByItemId)
        => ExpandUsingRuntimes(
            slots,
            s => s.SlotIndex,
            s => s.SpanSlots,
            (s, span) => s.SpanSlots = span,
            s => FirstItemId((s.Candidates ?? []).Select(c => c.JellyfinItemId)),
            runtimeMinutesByItemId);

    public static List<LineupSlot> Compact(IEnumerable<LineupSlot> slots)
        => Compact(
            slots,
            s => s.SlotIndex,
            s => s.SpanSlots,
            (s, span) => s.SpanSlots = span);

    public static List<LineupSlotDto> Compact(IEnumerable<LineupSlotDto> slots)
        => Compact(
            slots,
            s => s.SlotIndex,
            s => s.SpanSlots,
            (s, span) => s.SpanSlots = span);

    private static Guid? FirstItemId(IEnumerable<Guid?> ids)
        => ids.FirstOrDefault(id => id.HasValue);

    private static List<T> ExpandUsingRuntimes<T>(
        IEnumerable<T> slots,
        Func<T, int> getIndex,
        Func<T, int> getSpan,
        Action<T, int> setSpan,
        Func<T, Guid?> getItemId,
        IReadOnlyDictionary<Guid, int> runtimeMinutesByItemId)
    {
        if (runtimeMinutesByItemId.Count == 0)
        {
            return Compact(slots, getIndex, getSpan, setSpan);
        }

        foreach (var slot in slots)
        {
            if (getItemId(slot) is not Guid itemId
                || !runtimeMinutesByItemId.TryGetValue(itemId, out var minutes)
                || minutes <= MinutesPerSlot)
            {
                continue;
            }

            var index = getIndex(slot);
            var fromRuntime = ClampSpan(index, SpanFromRuntimeMinutes(minutes));
            if (fromRuntime > getSpan(slot))
            {
                setSpan(slot, fromRuntime);
            }
        }

        return Compact(slots, getIndex, getSpan, setSpan);
    }

    private static bool IsCoveredByEarlierSpan(
        IEnumerable<(int SlotIndex, int SpanSlots)> slots,
        int slotIndex)
    {
        foreach (var (start, rawSpan) in slots)
        {
            if (start >= slotIndex)
            {
                continue;
            }

            var span = ClampSpan(start, rawSpan);
            if (span <= 1)
            {
                continue;
            }

            if (slotIndex < start + span)
            {
                return true;
            }
        }

        return false;
    }

    private static List<T> Compact<T>(
        IEnumerable<T> slots,
        Func<T, int> getIndex,
        Func<T, int> getSpan,
        Action<T, int> setSpan)
    {
        var occupied = new bool[SlotsPerDay];
        var result = new List<T>();
        foreach (var slot in slots.OrderBy(getIndex))
        {
            var index = getIndex(slot);
            if (index < 0 || index >= SlotsPerDay || occupied[index])
            {
                continue;
            }

            var span = ClampSpan(index, getSpan(slot));
            setSpan(slot, span);
            for (var i = index; i < index + span && i < SlotsPerDay; i++)
            {
                occupied[i] = true;
            }

            result.Add(slot);
        }

        return result;
    }
}
