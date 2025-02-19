﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using Discord;
using Discord.Addons.Hosting;
using Discord.Interactions;
using Discord.WebSocket;
using Fergun;
using Fergun.Apis.Bing;
using Fergun.Apis.Dictionary;
using Fergun.Apis.Genius;
using Fergun.Apis.Google;
using Fergun.Apis.Musixmatch;
using Fergun.Apis.Urban;
using Fergun.Apis.Wikipedia;
using Fergun.Apis.WolframAlpha;
using Fergun.Apis.Yandex;
using Fergun.Converters;
using Fergun.Data;
using Fergun.Extensions;
using Fergun.Interactive;
using Fergun.Modules;
using Fergun.Services;
using GScraper.DuckDuckGo;
using GScraper.Google;
using GTranslate.Translators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Sinks.SystemConsole.Themes;
using YoutubeExplode.Search;

// The current directory is changed so the SQLite database is stored in the current folder
// instead of the project folder (if the data source path is relative).
Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

var host = Host.CreateDefaultBuilder()
    .UseConsoleLifetime()
    .UseContentRoot(AppDomain.CurrentDomain.BaseDirectory)
    .ConfigureServices((context, services) =>
    {
        TypeDescriptor.AddAttributes(typeof(IEmote), new TypeConverterAttribute(typeof(EmoteConverter)));
        services.AddOptions<StartupOptions>()
            .BindConfiguration(StartupOptions.Startup)
            .PostConfigure(startup =>
            {
                if (startup.MobileStatus)
                {
                    MobilePatcher.Patch();
                }
            });

        services.AddOptions<BotListOptions>()
            .BindConfiguration(BotListOptions.BotList);

        services.AddOptions<FergunOptions>()
            .BindConfiguration(FergunOptions.Fergun);

        services.AddSqlite<FergunContext>(context.Configuration.GetConnectionString("FergunDatabase"));
    })
    .ConfigureDiscordShardedHost((context, config) =>
    {
        config.SocketConfig = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Verbose,
            GatewayIntents = GatewayIntents.Guilds,
            UseInteractionSnowflakeDate = false,
            LogGatewayIntentWarnings = false,
            SuppressUnknownDispatchWarnings = true,
            FormatUsersInBidirectionalUnicode = false
        };

        config.Token = context.Configuration.GetSection(StartupOptions.Startup).Get<StartupOptions>().Token;
    })
    .UseInteractionService((_, config) =>
    {
        config.LogLevel = LogSeverity.Critical;
        config.DefaultRunMode = RunMode.Async;
        config.UseCompiledLambda = false;
    })
    .ConfigureLogging(logging => logging.ClearProviders())
    .UseSerilog((context, config) =>
    {
        config.MinimumLevel.Debug()
            .Filter.ByExcluding(e => e.Level == LogEventLevel.Debug && Matching.FromSource("Discord.WebSocket.DiscordShardedClient").Invoke(e) && e.MessageTemplate.Render(e.Properties).ContainsAny("Connected to", "Disconnected from"))
            .Filter.ByExcluding(e => e.Level <= LogEventLevel.Debug && (Matching.FromSource("Microsoft.Extensions.Http").Invoke(e) || Matching.FromSource("Microsoft.Extensions.Localization").Invoke(e)))
            .Filter.ByExcluding(e => e.Level <= LogEventLevel.Information && Matching.FromSource("Microsoft.EntityFrameworkCore").Invoke(e))
            .WriteTo.Console(LogEventLevel.Debug, theme: AnsiConsoleTheme.Literate)
            .WriteTo.Async(logger => logger.File($"{context.HostingEnvironment.ContentRootPath}logs/log-.txt", LogEventLevel.Debug, rollingInterval: RollingInterval.Day, retainedFileCountLimit: null));
    })
    .ConfigureServices(services =>
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddTransient(typeof(IFergunLocalizer<>), typeof(FergunLocalizer<>));
        services.AddSingleton<FergunLocalizationManager>();
        services.AddHostedService<InteractionHandlingService>();
        services.AddHostedService<BotListService>();
        services.AddSingleton(new InteractiveConfig { ReturnAfterSendingPaginator = true, DeferStopSelectionInteractions = false });
        services.AddSingleton<InteractiveService>();
        services.AddHostedService<InteractiveServiceLoggerHost>();
        services.AddSingleton<MusixmatchClientState>();
        services.AddFergunPolicies();

        services.AddHttpClient<IBingVisualSearch, BingVisualSearch>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IYandexImageSearch, YandexImageSearch>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IGoogleLensClient, GoogleLensClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IUrbanDictionary, UrbanDictionary>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IWikipediaClient, WikipediaClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IDictionaryClient, DictionaryClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IWolframAlphaClient, WolframAlphaClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IGeniusClient, GeniusClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IMusixmatchClient, MusixmatchClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<ITranslator, GoogleTranslator>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<ITranslator, GoogleTranslator2>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        // Registered twice so the one added as "itself" can be used in SharedModule
        services.AddHttpClient<GoogleTranslator2>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<ITranslator, YandexTranslator>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<ITranslator, MicrosoftTranslator>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        // Singleton used in TtsModule and MicrosoftVoiceConverter
        services.AddSingleton(s => new MicrosoftTranslator(s.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MicrosoftTranslator))));

        services.AddHttpClient<SearchClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<OtherModule>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient(nameof(GoogleScraper))
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient(nameof(DuckDuckGoScraper))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient("autocomplete", client => client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.ChromeUserAgent))
            .SetHandlerLifetime(TimeSpan.FromMinutes(30));

        services.AddTransient<IFergunTranslator, FergunTranslator>();
        services.AddTransient(s => new GoogleScraper(s.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GoogleScraper))));
        services.AddTransient(s => new DuckDuckGoScraper(s.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DuckDuckGoScraper))));
        services.AddTransient<SharedModule>();
    })
    .UseFergunRequestLogging()
    .Build();

// Semi-automatic migration
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FergunContext>();
    int pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).Count();

    if (pendingMigrations > 0)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateAsync();
        logger.LogInformation("Applied {Count} pending database migration(s).", pendingMigrations);
    }
}

await host.RunAsync();