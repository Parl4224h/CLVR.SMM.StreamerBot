using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using Microsoft.Extensions.Options;
using System.Net;

namespace StreamerBot;

public class VoiceStateHandler(
    GatewayClient gatewayClient,
    RestClient restClient,
    GuestStageManager guestStageManager,
    DashboardService dashboardService,
    IOptions<BotSettings> botSettings) : IVoiceStateUpdateGatewayHandler
{
    private const int UnknownVoiceStateCode = 10065;
    private readonly BotSettings _botSettings = botSettings.Value;

    public async ValueTask HandleAsync(VoiceState newState)
    {
        var configuredChannelId = _botSettings.ChannelId;

        try
        {
            if (!gatewayClient.Cache.Guilds.TryGetValue(newState.GuildId, out var guild))
                return;

            var botUser = gatewayClient.Cache.User;
            if (botUser is null)
                return;

            var botUserId = botUser.Id;

            if (guild.VoiceStates.TryGetValue(botUserId, out var botState) && botState.ChannelId is { } botChannelId)
            {
                var updatedUserLeftBotChannel = newState.UserId != botUserId && newState.ChannelId != botChannelId;
                if (updatedUserLeftBotChannel)
                {
                    var hasEligibleHostInChannel =
                        await HasEligibleHostInChannelAsync(guild, botChannelId, botUserId, newState.UserId);

                    if (!hasEligibleHostInChannel)
                    {
                        await gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(newState.GuildId, null));
                        return;
                    }
                }
            }

            if (newState.ChannelId != configuredChannelId)
                return;

            if (newState.ChannelId is null)
                return;

            var channelId = newState.ChannelId.Value;
            if (!guild.Channels.TryGetValue(channelId, out var channel) || channel is not StageGuildChannel)
                return;

            var botInSameStage = guild.VoiceStates.TryGetValue(botUserId, out var botVoiceState) &&
                                 botVoiceState.ChannelId == channelId;
            var botIsSuppressedInStage = botInSameStage && botVoiceState?.Suppressed == true;
            if (botIsSuppressedInStage)
            {
                try
                {
                    await restClient.ModifyCurrentGuildUserVoiceStateAsync(
                        newState.GuildId,
                        options => options
                            .WithChannelId(channelId)
                            .WithSuppress(false));
                }
                catch (RestException ex) when (ex is { StatusCode: HttpStatusCode.NotFound, Error.Code: UnknownVoiceStateCode })
                {
                    // Discord can return Unknown Voice State briefly while state changes propagate.
                }
            }

            guild.Users.TryGetValue(newState.UserId, out var guildUser);
            guildUser ??= await guild.GetUserAsync(newState.UserId);

            var isStreamer = guildUser.RoleIds.Contains(_botSettings.StreamerRoleId);
            var isMod = guildUser.RoleIds.Contains(_botSettings.ModRoleId);
            if (!isStreamer && !isMod)
                return;

            if (newState.Suppressed)
            {
                await restClient.ModifyGuildUserVoiceStateAsync(
                    newState.GuildId,
                    channelId,
                    newState.UserId,
                    options => options.WithSuppress(false));
            }

            var usersInStage = guild.VoiceStates.Values.Count(vs => vs.ChannelId == channelId);
            var stageInstance = guild.StageInstances.Values.FirstOrDefault(si => si.ChannelId == channelId);
            var hasCurrentEvent = stageInstance is not null && !string.IsNullOrWhiteSpace(stageInstance.Topic);

            if (usersInStage <= 1 || !hasCurrentEvent)
            {
                var topic = $"{guildUser.Username}'s Match Stream";

                if (stageInstance is null)
                {
                    await restClient.CreateStageInstanceAsync(new StageInstanceProperties(channelId, topic));
                }
                else
                {
                    await restClient.ModifyStageInstanceAsync(channelId, options => options.WithTopic(topic));
                }
            }

            var botAlreadySelfMuted = botVoiceState?.IsSelfMuted == true;
            var botAlreadySelfDeafened = botVoiceState?.IsSelfDeafened == true;

            if (!botInSameStage || !botAlreadySelfMuted || !botAlreadySelfDeafened)
            {
                await gatewayClient.UpdateVoiceStateAsync(
                    new VoiceStateProperties(newState.GuildId, channelId)
                        .WithSelfMute()
                        .WithSelfDeaf());
            }

            if (botInSameStage && !botIsSuppressedInStage)
                return;

            try
            {
                await restClient.ModifyCurrentGuildUserVoiceStateAsync(
                    newState.GuildId,
                    options => options
                        .WithChannelId(channelId)
                        .WithSuppress(false));
            }
            catch (RestException ex) when (ex is { StatusCode: HttpStatusCode.NotFound, Error.Code: UnknownVoiceStateCode })
            {
                // Discord can return Unknown Voice State briefly while the bot join state propagates.
            }
        }
        finally
        {
            var isUpdateInConfiguredChannel = newState.ChannelId == configuredChannelId;
            if (isUpdateInConfiguredChannel)
            {
                await guestStageManager.HandleVoiceStateUpdatedAsync(newState);
                await dashboardService.RefreshDashboardAsync();
            }
        }
    }

    private async Task<bool> HasEligibleHostInChannelAsync(
        Guild guild,
        ulong channelId,
        ulong botUserId,
        ulong excludingUserId)
    {
        foreach (var voiceState in guild.VoiceStates.Values)
        {
            if (voiceState.ChannelId != channelId ||
                voiceState.UserId == botUserId ||
                voiceState.UserId == excludingUserId)
            {
                continue;
            }

            if (guild.Users.TryGetValue(voiceState.UserId, out var guildUser) ||
                (guildUser = await guild.GetUserAsync(voiceState.UserId)) is not null)
            {
                if (IsEligibleHost(guildUser))
                    return true;
            }
        }

        return false;
    }

    private bool IsEligibleHost(GuildUser guildUser)
    {
        return guildUser.RoleIds.Contains(_botSettings.StreamerRoleId) ||
               guildUser.RoleIds.Contains(_botSettings.ModRoleId);
    }
}
