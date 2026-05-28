using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

namespace StreamerBot;

public class DashboardService(
    GatewayClient gatewayClient,
    RestClient restClient,
    GuestQueueService guestQueueService,
    GuestStageManager guestStageManager,
    IOptions<BotSettings> botSettings,
    ILogger<DashboardService> logger) : IReadyGatewayHandler, IInteractionCreateGatewayHandler
{
    private const string AddGuestCustomId = "dashboard:add-guest";
    private const string RemoveGuestCustomIdPrefix = "dashboard:remove-guest:";

    private readonly BotSettings _botSettings = botSettings.Value;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private ulong? _dashboardMessageId;

    public async ValueTask HandleAsync(ReadyEventArgs args)
    {
        await EnsureDashboardMessageAsync();
    }

    public async ValueTask HandleAsync(Interaction interaction)
    {
        if (interaction is ButtonInteraction buttonInteraction &&
            buttonInteraction.Data.CustomId.StartsWith(RemoveGuestCustomIdPrefix, StringComparison.Ordinal))
        {
            await HandleRemoveGuestAsync(buttonInteraction);
            return;
        }

        if (interaction is UserMenuInteraction userMenuInteraction &&
            userMenuInteraction.Data.CustomId == AddGuestCustomId)
        {
            await HandleAddGuestsAsync(userMenuInteraction);
        }
    }

    public async Task EnsureDashboardMessageAsync(CancellationToken cancellationToken = default)
    {
        var guildId = await GetDashboardGuildIdAsync(cancellationToken);
        if (guildId is null)
        {
            logger.LogWarning("Dashboard channel {DashboardChannelId} could not be resolved.",
                _botSettings.DashboardChannelId);
            return;
        }

        var botUserId = gatewayClient.Cache.User?.Id;
        if (botUserId is null)
            return;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var messages = new List<RestMessage>();
            await foreach (var message in restClient.GetMessagesAsync(
                               _botSettings.DashboardChannelId,
                               new PaginationProperties<ulong>().WithBatchSize(100)))
            {
                messages.Add(message);
            }

            if (messages is [{ Author.Id: var authorId } existing] && authorId == botUserId.Value)
            {
                await ModifyDashboardMessageAsync(existing.Id, guildId.Value, cancellationToken);
                _dashboardMessageId = existing.Id;
                return;
            }

            foreach (var message in messages)
            {
                try
                {
                    await restClient.DeleteMessageAsync(
                        _botSettings.DashboardChannelId,
                        message.Id,
                        cancellationToken: cancellationToken);
                }
                catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
                {
                    // Message was already removed while the dashboard was being rebuilt.
                }
            }

            var dashboardMessage = await restClient.SendMessageAsync(
                _botSettings.DashboardChannelId,
                BuildDashboardMessage(guildId.Value),
                cancellationToken: cancellationToken);
            _dashboardMessageId = dashboardMessage.Id;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task RefreshDashboardAsync(CancellationToken cancellationToken = default)
    {
        var guildId = await GetDashboardGuildIdAsync(cancellationToken);
        if (guildId is null)
            return;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_dashboardMessageId is { } messageId)
            {
                try
                {
                    await ModifyDashboardMessageAsync(messageId, guildId.Value, cancellationToken);
                    return;
                }
                catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
                {
                    _dashboardMessageId = null;
                }
            }
        }
        finally
        {
            _sync.Release();
        }

        await EnsureDashboardMessageAsync(cancellationToken);
    }

    private async Task HandleRemoveGuestAsync(ButtonInteraction interaction)
    {
        if (!await EnsureInvokerAuthorizedAsync(interaction))
            return;

        if (!ulong.TryParse(
                interaction.Data.CustomId[RemoveGuestCustomIdPrefix.Length..],
                out var guestUserId))
        {
            await ReplyAsync(interaction, "That dashboard action is no longer valid.");
            return;
        }

        var guildId = interaction.GuildId;
        if (guildId is null)
        {
            await ReplyAsync(interaction, "Guild not found.");
            return;
        }

        var removed = guestQueueService.RemoveQueuedGuest(guildId.Value, guestUserId);
        if (!removed)
        {
            await RefreshDashboardAsync();
            await ReplyAsync(interaction, "That guest is not currently in a slot.");
            return;
        }

        await guestStageManager.SuppressGuestAsync(guildId.Value, guestUserId);
        await RefreshDashboardAsync();
        await ReplyAsync(interaction, $"Removed <@{guestUserId}> from the guest slots.");
    }

    private async Task HandleAddGuestsAsync(UserMenuInteraction interaction)
    {
        if (!await EnsureInvokerAuthorizedAsync(interaction))
            return;

        var guild = interaction.Guild;
        if (guild is null)
        {
            await ReplyAsync(interaction, "Guild not found.");
            return;
        }

        var added = new List<string>();
        var skipped = new List<string>();

        foreach (var selectedUser in interaction.Data.SelectedValues)
        {
            guild.Users.TryGetValue(selectedUser.Id, out var targetUser);
            targetUser ??= await guild.GetUserAsync(selectedUser.Id);

            if (!IsGuest(targetUser))
            {
                skipped.Add($"{selectedUser.Username} is a mod or streamer");
                continue;
            }

            if (targetUser.RoleIds.Contains(_botSettings.MutedRoleId))
            {
                skipped.Add($"{selectedUser.Username} is muted");
                continue;
            }

            guestStageManager.ReconcileGuestSpeaker(guild.Id, selectedUser.Id);
            var addResult = guestQueueService.TryAddGuest(guild.Id, selectedUser.Id);
            switch (addResult)
            {
                case GuestQueueAddResult.Added:
                    if (!await guestStageManager.UnsuppressGuestAsync(guild.Id, selectedUser.Id))
                        await guestStageManager.EnsureGuestSpeakersAsync(guild.Id);

                    added.Add(selectedUser.Username);
                    break;
                case GuestQueueAddResult.AlreadyQueued:
                    await guestStageManager.UnsuppressGuestAsync(guild.Id, selectedUser.Id);
                    skipped.Add($"{selectedUser.Username} is already in a slot");
                    break;
                case GuestQueueAddResult.AlreadySpeaking:
                    skipped.Add($"{selectedUser.Username} is already speaking");
                    break;
                case GuestQueueAddResult.SlotsFull:
                    skipped.Add("all guest slots are full");
                    break;
            }
        }

        await RefreshDashboardAsync();

        var response = added.Count > 0
            ? $"Added {string.Join(", ", added)}."
            : "No guests were added.";

        if (skipped.Count > 0)
            response += $" Skipped: {string.Join("; ", skipped)}.";

        await ReplyAsync(interaction, response);
    }

    private async Task<bool> EnsureInvokerAuthorizedAsync(Interaction interaction)
    {
        var guild = interaction.Guild;
        if (guild is null)
        {
            await ReplyAsync(interaction, "Guild not found.");
            return false;
        }

        guild.Users.TryGetValue(interaction.User.Id, out var invoker);
        invoker ??= await guild.GetUserAsync(interaction.User.Id);

        if (invoker.RoleIds.Contains(_botSettings.ModRoleId) ||
            invoker.RoleIds.Contains(_botSettings.StreamerRoleId))
        {
            return true;
        }

        await ReplyAsync(interaction, "You must have the mod or streamer role to use this dashboard.");
        return false;
    }

    private MessageProperties BuildDashboardMessage(ulong guildId)
    {
        return new MessageProperties()
            .WithFlags(MessageFlags.IsComponentsV2)
            .WithAllowedMentions(AllowedMentionsProperties.None)
            .WithComponents(BuildDashboardComponents(guildId));
    }

    private IReadOnlyList<IMessageComponentProperties> BuildDashboardComponents(ulong guildId)
    {
        var guests = guestQueueService.GetGuestEntries(guildId);
        var occupiedSlots = guests.Count;
        var availableSlots = Math.Max(0, _botSettings.GuestSlotCount - occupiedSlots);

        var container = new ComponentContainerProperties()
            .WithAccentColor(new Color(46, 204, 113));

        container.Add(new TextDisplayProperties(
            $"# Streamer Dashboard\nGuest slots: **{occupiedSlots}/{_botSettings.GuestSlotCount}**"));
        container.Add(new ComponentSeparatorProperties());

        if (guests.Count == 0)
        {
            container.Add(new TextDisplayProperties("No guests are currently in slots."));
        }
        else
        {
            var slotNumber = 1;
            foreach (var guest in guests)
            {
                container.Add(new ComponentSectionProperties(
                    new ButtonProperties(
                        $"{RemoveGuestCustomIdPrefix}{guest.GuestUserId}",
                        "Remove",
                        ButtonStyle.Danger))
                    .AddComponents([
                        new TextDisplayProperties(
                            $"**Slot {slotNumber}**\n<@{guest.GuestUserId}>")
                    ]));
                slotNumber++;
            }
        }

        container.Add(new ComponentSeparatorProperties());
        container.Add(new UserMenuProperties(AddGuestCustomId)
            .WithPlaceholder(availableSlots > 0 ? "Add guests" : "Guest slots are full")
            .WithMinValues(1)
            .WithMaxValues(Math.Clamp(availableSlots, 1, 25))
            .WithDisabled(availableSlots == 0));

        return [container];
    }

    private async Task ModifyDashboardMessageAsync(
        ulong messageId,
        ulong guildId,
        CancellationToken cancellationToken)
    {
        await restClient.ModifyMessageAsync(
            _botSettings.DashboardChannelId,
            messageId,
            options => options
                .WithContent(null!)
                .WithEmbeds([])
                .WithFlags(MessageFlags.IsComponentsV2)
                .WithAllowedMentions(AllowedMentionsProperties.None)
                .WithComponents(BuildDashboardComponents(guildId)),
            cancellationToken: cancellationToken);
    }

    private async Task<ulong?> GetDashboardGuildIdAsync(CancellationToken cancellationToken)
    {
        var cachedGuild = gatewayClient.Cache.Guilds.Values.FirstOrDefault(guild =>
            guild.Channels.ContainsKey(_botSettings.DashboardChannelId));
        if (cachedGuild is not null)
            return cachedGuild.Id;

        try
        {
            var channel = await restClient.GetChannelAsync(
                _botSettings.DashboardChannelId,
                cancellationToken: cancellationToken);
            if (channel is IGuildChannel guildChannel)
                return guildChannel.GuildId;

            logger.LogWarning("Dashboard channel {DashboardChannelId} is not a guild channel.",
                _botSettings.DashboardChannelId);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogWarning(ex, "Could not fetch dashboard channel {DashboardChannelId}.",
                _botSettings.DashboardChannelId);
        }

        return null;
    }

    private bool IsGuest(GuildUser guildUser)
    {
        return !guildUser.RoleIds.Contains(_botSettings.StreamerRoleId) &&
               !guildUser.RoleIds.Contains(_botSettings.ModRoleId);
    }

    private static Task ReplyAsync(Interaction interaction, string message)
    {
        return interaction.SendResponseAsync(
            InteractionCallback.Message(new InteractionMessageProperties()
                .WithContent(message)
                .WithFlags(MessageFlags.Ephemeral)));
    }
}
