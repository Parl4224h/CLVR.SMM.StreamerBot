using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Services.ApplicationCommands;
using StreamerBot;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<BotSettings>()
    .Bind(builder.Configuration.GetSection(BotSettings.SectionName))
    .Validate(settings => settings.GuestSlotCount > 0,
        $"{BotSettings.SectionName}:GuestSlotCount must be greater than 0.")
    .Validate(settings => settings.StreamerRoleId != 0, $"{BotSettings.SectionName}:StreamerRoleId must be configured.")
    .Validate(settings => settings.ModRoleId != 0, $"{BotSettings.SectionName}:ModRoleId must be configured.")
    .Validate(settings => settings.MutedRoleId != 0, $"{BotSettings.SectionName}:MutedRoleId must be configured.")
    .Validate(settings => settings.ChannelId != 0, $"{BotSettings.SectionName}:ChannelId must be configured.")
    .Validate(settings => settings.DashboardChannelId != 0,
        $"{BotSettings.SectionName}:DashboardChannelId must be configured.")
    .Validate(settings => settings.GuestTimeoutMinutes > 0,
        $"{BotSettings.SectionName}:GuestTimeoutMinutes must be greater than 0.")
    .ValidateOnStart();

builder.Services.AddSingleton<GuestQueueService>();
builder.Services.AddSingleton<GuestStageManager>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddHostedService<GuestSpeakerRotationService>();

builder.Services
    .AddDiscordGateway(opts =>
    {
        opts.Intents = GatewayIntents.GuildVoiceStates |
                       GatewayIntents.Guilds |
                       GatewayIntents.GuildUsers;
    })
    .AddGatewayHandler<VoiceStateHandler>()
    .AddGatewayHandler<DashboardService>(serviceProvider => serviceProvider.GetRequiredService<DashboardService>())
    .AddApplicationCommands<SlashCommandInteraction, SlashCommandContext>(opts =>
    {
        opts.AutoRegisterCommands = true;
        opts.ResultHandler = new ApplicationCommandResultHandler<SlashCommandContext>(MessageFlags.Ephemeral);
    });

var host = builder.Build();

host.AddModules(typeof(StreamerCommandModule).Assembly);

host.Run();
