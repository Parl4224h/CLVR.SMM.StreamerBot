using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace StreamerBot;

public enum GuestQueueAddResult
{
    Added,
    AlreadyQueued,
    AlreadySpeaking,
    SlotsFull
}

public readonly record struct GuestQueueEntry(ulong GuestUserId, DateTimeOffset AddedAt);

public readonly record struct GuestSpeakerSession(ulong GuildId, ulong ChannelId, ulong UserId, DateTimeOffset StartedAt);

public class GuestQueueService(IOptions<BotSettings> botSettings)
{
    private readonly BotSettings _botSettings = botSettings.Value;
    private readonly ConcurrentDictionary<ulong, GuildGuestState> _guildStates = new();

    public string GetGuests(ulong guildId)
    {
        var state = _guildStates.GetOrAdd(guildId, static _ => new GuildGuestState());
        lock (state.Sync)
        {
            if (state.Slots.Count == 0)
                return "No guests in slots.";

            return string.Join(", ", state.Slots.Select(entry => $"<@{entry.GuestUserId}>"));
        }
    }

    public IReadOnlyList<GuestQueueEntry> GetGuestEntries(ulong guildId)
    {
        var state = _guildStates.GetOrAdd(guildId, static _ => new GuildGuestState());
        lock (state.Sync)
        {
            return state.Slots.ToArray();
        }
    }

    public int GetOccupiedSlotCount(ulong guildId)
    {
        var state = _guildStates.GetOrAdd(guildId, static _ => new GuildGuestState());
        lock (state.Sync)
        {
            return state.Slots.Count;
        }
    }
    
    public GuestQueueAddResult TryAddGuest(ulong guildId, ulong guestUserId)
    {
        var state = _guildStates.GetOrAdd(guildId, static _ => new GuildGuestState());

        lock (state.Sync)
        {
            if (state.ActiveSpeakers.ContainsKey(guestUserId))
                return GuestQueueAddResult.AlreadySpeaking;

            if (state.Slots.Any(entry => entry.GuestUserId == guestUserId))
                return GuestQueueAddResult.AlreadyQueued;

            if (state.Slots.Count >= _botSettings.GuestSlotCount)
                return GuestQueueAddResult.SlotsFull;

            state.Slots.AddLast(new GuestQueueEntry(guestUserId, DateTimeOffset.UtcNow));
            return GuestQueueAddResult.Added;
        }
    }

    public bool RemoveQueuedGuest(ulong guildId, ulong guestUserId)
    {
        if (!_guildStates.TryGetValue(guildId, out var state))
            return false;

        lock (state.Sync)
        {
            var node = state.Slots.First;
            while (node is not null)
            {
                if (node.Value.GuestUserId == guestUserId)
                {
                    state.Slots.Remove(node);
                    return true;
                }

                node = node.Next;
            }

            return false;
        }
    }

    public bool TryGetNextGuestToPromote(ulong guildId, ISet<ulong> activeSpeakerUserIds, out GuestQueueEntry entry)
    {
        entry = default;

        if (!_guildStates.TryGetValue(guildId, out var state))
            return false;

        lock (state.Sync)
        {
            foreach (var slotEntry in state.Slots)
            {
                if (activeSpeakerUserIds.Contains(slotEntry.GuestUserId))
                    continue;

                entry = slotEntry;
                return true;
            }

            return false;
        }
    }

    public void MarkSpeakerStarted(ulong guildId, ulong channelId, ulong userId)
    {
        var state = _guildStates.GetOrAdd(guildId, static _ => new GuildGuestState());

        lock (state.Sync)
        {
            if (state.ActiveSpeakers.ContainsKey(userId))
                return;

            state.ActiveSpeakers[userId] = new GuestSpeakerSession(guildId, channelId, userId, DateTimeOffset.UtcNow);
        }
    }

    public bool TryGetSpeakerSession(ulong guildId, ulong userId, out GuestSpeakerSession session)
    {
        session = default;

        if (!_guildStates.TryGetValue(guildId, out var state))
            return false;

        lock (state.Sync)
        {
            return state.ActiveSpeakers.TryGetValue(userId, out session);
        }
    }

    public IReadOnlyList<GuestSpeakerSession> GetActiveSpeakers(ulong guildId)
    {
        if (!_guildStates.TryGetValue(guildId, out var state))
            return Array.Empty<GuestSpeakerSession>();

        lock (state.Sync)
        {
            return state.ActiveSpeakers.Values.ToArray();
        }
    }

    public void MarkSpeakerStopped(ulong guildId, ulong userId)
    {
        if (!_guildStates.TryGetValue(guildId, out var state))
            return;

        lock (state.Sync)
        {
            state.ActiveSpeakers.Remove(userId);
        }
    }

    public IReadOnlyList<GuestSpeakerSession> GetExpiredSpeakers(DateTimeOffset cutoff)
    {
        var expired = new List<GuestSpeakerSession>();

        foreach (var (_, state) in _guildStates)
        {
            lock (state.Sync)
            {
                expired.AddRange(state.ActiveSpeakers.Values.Where(session => session.StartedAt <= cutoff));
            }
        }

        return expired;
    }

    public IReadOnlyList<(ulong GuildId, ulong UserId)> RemoveExpiredSlots(DateTimeOffset cutoff)
    {
        var removed = new List<(ulong GuildId, ulong UserId)>();

        foreach (var (guildId, state) in _guildStates)
        {
            lock (state.Sync)
            {
                var node = state.Slots.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (node.Value.AddedAt <= cutoff)
                    {
                        var userId = node.Value.GuestUserId;
                        state.Slots.Remove(node);
                        state.ActiveSpeakers.Remove(userId);
                        removed.Add((guildId, userId));
                    }

                    node = next;
                }
            }
        }

        return removed;
    }

    public void ClearSlots(ulong guildId)
    {
        if (!_guildStates.TryGetValue(guildId, out var state))
            return;

        lock (state.Sync)
        {
            state.Slots.Clear();
        }
    }

    private sealed class GuildGuestState
    {
        public object Sync { get; } = new();

        public LinkedList<GuestQueueEntry> Slots { get; } = new();

        public Dictionary<ulong, GuestSpeakerSession> ActiveSpeakers { get; } = new();
    }
}
