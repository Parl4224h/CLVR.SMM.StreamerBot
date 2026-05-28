using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Services.ApplicationCommands;

namespace StreamerBot;

[SlashCommand("streamer", "Streamer-related commands")]
public class StreamerCommandModule : CommandModuleBase
{
    [SubSlashCommand("guest", "Manage guests")]
    public class GuestCommandModule(
        GuestQueueService guestQueueService,
        GuestStageManager guestStageManager,
        IOptions<BotSettings> botSettings
    ) : CommandModuleBase
    {
        private readonly BotSettings _botSettings = botSettings.Value;

        private async Task<Guild?> EnsureInvokerAuthorizedAsync()
        {
            var guild = Context.Guild;
            if (guild is null)
            {
                await ReplyAsync("Guild not found.", true);
                return null;
            }

            guild.Users.TryGetValue(Context.User.Id, out var invoker);
            invoker ??= await guild.GetUserAsync(Context.User.Id);

            var isAuthorized = invoker.RoleIds.Contains(_botSettings.ModRoleId) ||
                               invoker.RoleIds.Contains(_botSettings.StreamerRoleId);
            if (isAuthorized)
                return guild;

            await ReplyAsync("You must have the mod or streamer role to use this command.", true);
            return null;
        }

        [SubSlashCommand("add", "Add a guest to an open slot")]
        public async Task AddGuest(
            [SlashCommandParameter(Name = "guest", Description = "The user to add to an open slot")]
            User user
        )
        {
            var guild = await EnsureInvokerAuthorizedAsync();
            if (guild is null)
                return;

            guild.Users.TryGetValue(user.Id, out var targetUser);
            targetUser ??= await guild.GetUserAsync(user.Id);

            var isModOrStreamer = targetUser.RoleIds.Contains(_botSettings.ModRoleId) ||
                                  targetUser.RoleIds.Contains(_botSettings.StreamerRoleId);
            if (isModOrStreamer)
            {
                await ReplyAsync($"User {user.Username} is a mod or streamer and cannot be added as a guest.", true);
                return;
            }

            guestStageManager.ReconcileGuestSpeaker(guild.Id, user.Id);

            var addResult = guestQueueService.TryAddGuest(guild.Id, user.Id);
            switch (addResult)
            {
                case GuestQueueAddResult.Added:
                    if (!await guestStageManager.UnsuppressGuestAsync(guild.Id, user.Id))
                        await guestStageManager.EnsureGuestSpeakersAsync(guild.Id);

                    await ReplyAsync($"Guest {user.Username} added to an open slot.", true);
                    return;
                case GuestQueueAddResult.SlotsFull:
                    await ReplyAsync($"All {_botSettings.GuestSlotCount} guest slots are full.", true);
                    return;
                case GuestQueueAddResult.AlreadyQueued:
                    await guestStageManager.UnsuppressGuestAsync(guild.Id, user.Id);
                    await ReplyAsync("The guest is already in a slot.", true);
                    return;
                case GuestQueueAddResult.AlreadySpeaking:
                    await ReplyAsync("The guest is already speaking.", true);
                    return;
            }
        }

        [SubSlashCommand("remove", "Remove a guest from a slot")]
        public async Task RemoveGuest(
            [SlashCommandParameter(Name = "guest", Description = "The user to remove from a slot")]
            User user
        )
        {
            var guild = await EnsureInvokerAuthorizedAsync();
            if (guild is null)
                return;

            var removed = guestQueueService.RemoveQueuedGuest(guild.Id, user.Id);
            if (removed)
            {
                await guestStageManager.SuppressGuestAsync(guild.Id, user.Id);
                await ReplyAsync($"Guest {user.Username} removed from the slots.", true);
                return;
            }

            await ReplyAsync("That guest is not currently in a slot.", true);
        }

        [SubSlashCommand("list", "List all occupied guest slots")]
        public async Task ListGuests()
        {
            var guild = await EnsureInvokerAuthorizedAsync();
            if (guild is null)
                return;

            var occupiedSlots = guestQueueService.GetOccupiedSlotCount(guild.Id);
            var guests = guestQueueService.GetGuests(guild.Id);
            await ReplyAsync($"Guests in slots ({occupiedSlots}/{_botSettings.GuestSlotCount}):\n{guests}", true);
        }
    }
}
