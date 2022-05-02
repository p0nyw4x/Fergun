﻿using Discord;
using Discord.Addons.Hosting;
using Discord.Interactions;
using Discord.WebSocket;
using Fergun;
using Fergun.Apis.Bing;
using Fergun.Apis.Urban;
using Fergun.Apis.Wikipedia;
using Fergun.Apis.Yandex;
using Fergun.Extensions;
using Fergun.Interactive;
using Fergun.Modules;
using Fergun.Services;
using GScraper.Brave;
using GScraper.DuckDuckGo;
using GScraper.Google;
using GTranslate.Translators;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Sinks.SystemConsole.Themes;
using YoutubeExplode.Search;

await Host.CreateDefaultBuilder()
    .UseContentRoot(AppDomain.CurrentDomain.BaseDirectory)
    .ConfigureDiscordShardedHost((context, config) =>
    {
        config.SocketConfig = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Verbose,
            GatewayIntents = GatewayIntents.Guilds,
            UseInteractionSnowflakeDate = false,
            LogGatewayIntentWarnings = false,
            SuppressUnknownDispatchWarnings = true
        };

        config.Token = context.Configuration.Get<FergunConfig>().Token;
    })
    .UseInteractionService((_, config) =>
    {
        config.LogLevel = LogSeverity.Verbose;
        config.DefaultRunMode = RunMode.Async;
        config.UseCompiledLambda = false;
    })
    .ConfigureLogging(logging => logging.ClearProviders())
    .UseSerilog((context, config) =>
    {
        config.MinimumLevel.Debug()
            .Filter.ByExcluding(e => e.Level == LogEventLevel.Debug && Matching.FromSource("Discord.WebSocket.DiscordShardedClient").Invoke(e) && e.MessageTemplate.Render(e.Properties).ContainsAny("Connected to", "Disconnected from"))
            .Filter.ByExcluding(e => e.Level <= LogEventLevel.Debug && (Matching.FromSource("Microsoft.Extensions.Http").Invoke(e) || Matching.FromSource("Microsoft.Extensions.Localization").Invoke(e)))
            .WriteTo.Console(LogEventLevel.Debug, theme: AnsiConsoleTheme.Literate)
            .WriteTo.Async(logger => logger.File($"{context.HostingEnvironment.ContentRootPath}logs/log-.txt", LogEventLevel.Debug, rollingInterval: RollingInterval.Day));
    })
    .ConfigureServices((context, services) =>
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddTransient(typeof(IFergunLocalizer<>), typeof(FergunLocalizer<>));
        services.AddHostedService<InteractionHandlingService>();
        services.AddHostedService<BotListService>();
        services.AddSingleton(new InteractiveConfig { ReturnAfterSendingPaginator = true, DeferStopSelectionInteractions = false });
        services.AddSingleton<InteractiveService>();
        services.AddFergunPolicies();
        services.Configure<BotListOptions>(context.Configuration.GetSection(BotListOptions.BotList));

        services.AddHttpClient<IBingVisualSearch, BingVisualSearch>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IYandexImageSearch, YandexImageSearch>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IUrbanDictionary, UrbanDictionary>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<IWikipediaClient, WikipediaClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<GoogleTranslator>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<GoogleTranslator2>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient<YandexTranslator>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        // We have to register the named client and service separately because Microsoft Translator isn't stateless,
        // It stores a token required to make API calls that is obtained once and updated occasionally, since AddHttpClient<TClient>
        // adds the services with a transient scope, this means that the token would be obtained every time the services are used.
        services.AddHttpClient(nameof(MicrosoftTranslator))
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

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

        services.AddHttpClient(nameof(BraveScraper))
            .SetHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddRetryPolicy();

        services.AddHttpClient("autocomplete", client => client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.ChromeUserAgent))
            .SetHandlerLifetime(TimeSpan.FromMinutes(30));

        services.AddTransient<IFergunTranslator, FergunTranslator>();
        services.AddSingleton(x => new GoogleScraper(x.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GoogleScraper))));
        services.AddSingleton(x => new DuckDuckGoScraper(x.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DuckDuckGoScraper))));
        services.AddSingleton(x => new BraveScraper(x.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BraveScraper))));
        services.AddTransient<SharedModule>();
    }).RunConsoleAsync();