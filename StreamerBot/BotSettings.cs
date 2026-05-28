namespace StreamerBot;

public class BotSettings
{
    public const string SectionName = "BotSettings";

    public int GuestSlotCount { get; set; } = 2;

    public int GuestTimeoutMinutes { get; set; } = 30;

    public ulong StreamerRoleId { get; set; }

    public ulong ModRoleId { get; set; }

    public ulong MutedRoleId { get; set; }

    public ulong ChannelId { get; set; }

    public ulong DashboardChannelId { get; set; }
}
