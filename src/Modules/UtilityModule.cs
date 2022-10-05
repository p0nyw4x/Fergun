﻿using System.Diagnostics;
using System.Globalization;
using Discord;
using Discord.Interactions;
using Fergun.Apis.Wikipedia;
using Fergun.Apis.WolframAlpha;
using Fergun.Extensions;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Fergun.Modules.Handlers;
using GTranslate;
using GTranslate.Results;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using YoutubeExplode.Common;
using YoutubeExplode.Search;
using Color = Discord.Color;

namespace Fergun.Modules;

public class UtilityModule : InteractionModuleBase
{
    private readonly ILogger<UtilityModule> _logger;
    private readonly IFergunLocalizer<UtilityModule> _localizer;
    private readonly FergunOptions _fergunOptions;
    private readonly SharedModule _shared;
    private readonly InteractiveService _interactive;
    private readonly IFergunTranslator _translator;
    private readonly SearchClient _searchClient;
    private readonly IWikipediaClient _wikipediaClient;
    private readonly IWolframAlphaClient _wolframAlphaClient;

    private static readonly DrawingOptions _cachedDrawingOptions = new();
    private static readonly PngEncoder _cachedPngEncoder = new() { CompressionLevel = PngCompressionLevel.BestCompression, IgnoreMetadata = true };
    private static readonly Lazy<Language[]> _lazyFilteredLanguages = new(() => Language.LanguageDictionary
        .Values
        .Where(x => x.SupportedServices == (TranslationServices.Google | TranslationServices.Bing | TranslationServices.Yandex | TranslationServices.Microsoft))
        .ToArray());

    public UtilityModule(ILogger<UtilityModule> logger, IFergunLocalizer<UtilityModule> localizer, IOptionsSnapshot<FergunOptions> fergunOptions,
        SharedModule shared, InteractiveService interactive, IFergunTranslator translator, SearchClient searchClient, IWikipediaClient wikipediaClient,
        IWolframAlphaClient wolframAlphaClient)
    {
        _logger = logger;
        _localizer = localizer;
        _fergunOptions = fergunOptions.Value;
        _shared = shared;
        _interactive = interactive;
        _translator = translator;
        _searchClient = searchClient;
        _wikipediaClient = wikipediaClient;
        _wolframAlphaClient = wolframAlphaClient;
    }

    public override void BeforeExecute(ICommandInfo command) => _localizer.CurrentCulture = CultureInfo.GetCultureInfo(Context.Interaction.GetLanguageCode());

    [UserCommand("Avatar")]
    public async Task<RuntimeResult> AvatarUserCommandAsync(IUser user)
        => await AvatarAsync(user);

    [SlashCommand("avatar", "Displays the avatar of a user.")]
    public async Task<RuntimeResult> AvatarAsync([Summary(description: "The user.")] IUser user,
        [Summary(description: "An specific avatar type.")] AvatarType type = AvatarType.FirstAvailable)
    {
        string? url;
        string title;

        switch (type)
        {
            case AvatarType.FirstAvailable:
                url = (user as IGuildUser)?.GetGuildAvatarUrl(size: 2048) ?? user.GetAvatarUrl(size: 2048) ?? user.GetDefaultAvatarUrl();
                title = user.ToString()!;
                break;

            case AvatarType.Server:
                url = (user as IGuildUser)?.GetGuildAvatarUrl(size: 2048);
                if (url is null)
                {
                    return FergunResult.FromError(_localizer["{0} doesn't have a server avatar.", user]);
                }

                title = $"{user} ({_localizer["Server"]})";
                break;

            case AvatarType.Global:
                url = user.GetAvatarUrl(size: 2048);
                if (url is null)
                {
                    return FergunResult.FromError(_localizer["{0} doesn't have a global (main) avatar.", user]);
                }

                title = $"{user} ({_localizer["Global"]})";
                break;

            default:
                url = user.GetDefaultAvatarUrl();
                title = $"{user} ({_localizer["Default"]})";
                break;
        }

        var builder = new EmbedBuilder
        {
            Title = title,
            ImageUrl = url,
            Color = Color.Orange
        };

        await Context.Interaction.RespondAsync(embed: builder.Build());

        return FergunResult.FromSuccess();
    }

    [MessageCommand("Bad Translator")]
    public async Task<RuntimeResult> BadTranslatorAsync(IMessage message)
        => await BadTranslatorAsync(message.GetText());

    [SlashCommand("bad-translator", "Passes a text through multiple, different translators.")]
    public async Task<RuntimeResult> BadTranslatorAsync([Summary(description: "The text to use.")] string text,
        [Summary(description: "The amount of times to translate the text (2-10).")] [MinValue(2)] [MaxValue(10)] int chainCount = 8)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return FergunResult.FromError(_localizer["The text must not be empty."], true);
        }

        if (chainCount is < 2 or > 10)
        {
            return FergunResult.FromError(_localizer["The chain count must be between 2 and 10 (inclusive)."], true);
        }
        
        await Context.Interaction.DeferAsync();

        _translator.Randomize();

        var languageChain = new List<ILanguage>(chainCount + 1);
        ILanguage? source = null;
        for (int i = 0; i < chainCount; i++)
        {
            ILanguage target;
            if (i == chainCount - 1)
            {
                target = source!;
            }
            else
            {
                // Get unique and random languages.
                do
                {
                    target = _lazyFilteredLanguages.Value[Random.Shared.Next(_lazyFilteredLanguages.Value.Length)];
                } while (languageChain.Contains(target));
            }

            ITranslationResult result;
            try
            {
                _logger.LogInformation("Translating to: {target}", target.ISO6391);
                result = await _translator.TranslateAsync(text, target);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error translating text {text} ({source} -> {target})", text, source?.ISO6391 ?? "auto", target.ISO6391);
                return FergunResult.FromError(e.Message);
            }

            // Switch the translators to avoid spamming them and get more variety
            _translator.Next();

            if (i == 0)
            {
                source = result.SourceLanguage;
                _logger.LogDebug("Badtranslator: Original language: {source}", source.ISO6391);
                languageChain.Add(source);
            }

            _logger.LogDebug("Badtranslator: Translated from {source} to {target}, Service: {service}", result.SourceLanguage.ISO6391, result.TargetLanguage.ISO6391, result.Service);

            text = result.Translation;
            languageChain.Add(target);
        }

        string embedText = $"**{_localizer["Language Chain"]}**\n{string.Join(" -> ", languageChain.Select(x => x.ISO6391))}\n\n**{_localizer["Result"]}**\n";

        var embed = new EmbedBuilder()
            .WithTitle("Bad translator")
            .WithDescription($"{embedText}{text.Truncate(EmbedBuilder.MaxDescriptionLength - embedText.Length)}")
            .WithThumbnailUrl(Constants.BadTranslatorLogoUrl)
            .WithColor(Color.Orange)
            .Build();

        await Context.Interaction.FollowupAsync(embed: embed);

        return FergunResult.FromSuccess();
    }

    [SlashCommand("color", "Displays a color.")]
    public async Task<RuntimeResult> ColorAsync([Summary(description: "A color name, hex string or raw value. Leave empty to get a random color.")]
        System.Drawing.Color color = default)
    {
        if (color.IsEmpty)
        {
            color = System.Drawing.Color.FromArgb(Random.Shared.Next((int)(Color.MaxDecimalValue + 1)));
        }

        using var image = new Image<Rgba32>(500, 500);

        image.Mutate(x => x.Fill(_cachedDrawingOptions, SixLabors.ImageSharp.Color.FromRgb(color.R, color.G, color.B)));
        await using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream, _cachedPngEncoder);
        stream.Seek(0, SeekOrigin.Begin);

        string hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";

        var builder = new EmbedBuilder()
            .WithTitle($"#{hex}{(color.IsNamedColor ? $" ({color.Name})" : "")}")
            .WithImageUrl($"attachment://{hex}.png")
            .WithFooter($"R: {color.R}, G: {color.G}, B: {color.B}")
            .WithColor((Color)color);

        await Context.Interaction.RespondWithFileAsync(new FileAttachment(stream, $"{hex}.png"), embed: builder.Build());

        return FergunResult.FromSuccess();
    }

    [SlashCommand("help", "Information about Fergun 2.")]
    public async Task<RuntimeResult> HelpAsync()
    {
        MessageComponent? components = null;
        string description = _localizer["Fergun2Info", "https://github.com/d4n3436/Fergun/wiki/Command-removal-notice"];
        var url = _fergunOptions.SupportServerUrl;

        if (url is not null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            description += $"\n\n{_localizer["Fergun2SupportInfo"]}";
            components = new ComponentBuilder()
                .WithButton(_localizer["Support Server"], style: ButtonStyle.Link, url: url.AbsoluteUri)
                .Build();
        }

        var embed = new EmbedBuilder()
            .WithTitle("Fergun 2")
            .WithDescription(description)
            .WithColor(Color.Orange)
            .Build();

        await Context.Interaction.RespondAsync(embed: embed, components: components);

        return FergunResult.FromSuccess();
    }

    [SlashCommand("ping", "Sends the response time of the bot.")]
    public async Task<RuntimeResult> PingAsync()
    {
        var embed = new EmbedBuilder()
            .WithDescription("Pong!")
            .WithColor(Color.Orange)
            .Build();

        var sw = Stopwatch.StartNew();
        await Context.Interaction.RespondAsync(embed: embed);
        sw.Stop();

        embed = new EmbedBuilder()
            .WithDescription($"Pong! {sw.ElapsedMilliseconds}ms")
            .WithColor(Color.Orange)
            .Build();

        await Context.Interaction.ModifyOriginalResponseAsync(x => x.Embed = embed);

        return FergunResult.FromSuccess();
    }
    
    [SlashCommand("say", "Says something.")]
    public async Task<RuntimeResult> SayAsync([Summary(description: "The text to send.")] string text)
    {
        await Context.Interaction.RespondAsync(text.Truncate(DiscordConfig.MaxMessageSize), allowedMentions: AllowedMentions.None);

        return FergunResult.FromSuccess();
    }

    [MessageCommand("Translate Text")]
    public async Task<RuntimeResult> TranslateAsync(IMessage message)
        => await TranslateAsync(message.GetText(), Context.Interaction.GetLanguageCode());

    [SlashCommand("translate", "Translates a text.")]
    public async Task<RuntimeResult> TranslateAsync([Summary(description: "The text to translate.")] string text,
        [Autocomplete(typeof(TranslateAutocompleteHandler))] [Summary(description: "Target language (name, code or alias).")] string target,
        [Autocomplete(typeof(TranslateAutocompleteHandler))] [Summary(description: "Source language (name, code or alias).")] string? source = null,
        [Summary(description: "Whether to respond ephemerally.")] bool ephemeral = false)
        => await _shared.TranslateAsync(Context.Interaction, text, target, source, ephemeral);

    [UserCommand("User Info")]
    [SlashCommand("user", "Gets information about a user.")]
    public async Task<RuntimeResult> UserInfoAsync([Summary(description: "The user.")] IUser user)
    {
        string activities = "";
        if (user.Activities.Count > 0)
        {
            activities = string.Join('\n', user.Activities.Select(x =>
                x.Type == ActivityType.CustomStatus
                    ? ((CustomStatusGame)x).ToString()
                    : $"{x.Type} {x.Name}"));
        }

        if (string.IsNullOrWhiteSpace(activities))
            activities = $"({_localizer["None"]})";

        string clients = "?";
        if (user.ActiveClients.Count > 0)
        {
            clients = string.Join(' ', user.ActiveClients.Select(x =>
                x switch
                {
                    ClientType.Desktop => "🖥",
                    ClientType.Mobile => "📱",
                    ClientType.Web => "🌐",
                    _ => ""
                }));
        }

        if (string.IsNullOrWhiteSpace(clients))
            clients = "?";

        var guildUser = user as IGuildUser;
        string avatarUrl = guildUser?.GetGuildAvatarUrl(size: 2048) ?? user.GetAvatarUrl(ImageFormat.Auto, 2048) ?? user.GetDefaultAvatarUrl();

        var builder = new EmbedBuilder()
            .WithTitle(_localizer["User Info"])
            .AddField(_localizer["Name"], user.ToString())
            .AddField("Nickname", guildUser?.Nickname ?? $"({_localizer["None"]})")
            .AddField("ID", user.Id)
            .AddField(_localizer["Activities"], activities, true)
            .AddField(_localizer["Active Clients"], clients, true)
            .AddField(_localizer["Is Bot"], user.IsBot)
            .AddField(_localizer["Created At"], GetTimestamp(user.CreatedAt))
            .AddField(_localizer["Server Join Date"], GetTimestamp(guildUser?.JoinedAt))
            .AddField(_localizer["Boosting Since"], GetTimestamp(guildUser?.PremiumSince))
            .WithThumbnailUrl(avatarUrl)
            .WithColor(Color.Orange);

        await Context.Interaction.RespondAsync(embed: builder.Build());

        return FergunResult.FromSuccess();

        static string GetTimestamp(DateTimeOffset? dateTime)
            => dateTime == null ? "N/A" : $"{dateTime.Value.ToDiscordTimestamp()} ({dateTime.Value.ToDiscordTimestamp('R')})";
    }

    [SlashCommand("wikipedia", "Searches for Wikipedia articles.")]
    public async Task<RuntimeResult> WikipediaAsync([Autocomplete(typeof(WikipediaAutocompleteHandler))] [Summary(description: "The search query.")] string query)
    {
        await Context.Interaction.DeferAsync();

        var articles = (await _wikipediaClient.GetArticlesAsync(query, Context.Interaction.GetLanguageCode())).ToArray();

        if (articles.Length == 0)
        {
            return FergunResult.FromError(_localizer["No results."]);
        }

        var paginator = new LazyPaginatorBuilder()
            .AddUser(Context.User)
            .WithPageFactory(GeneratePage)
            .WithActionOnCancellation(ActionOnStop.DisableInput)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .WithMaxPageIndex(articles.Length - 1)
            .WithFooter(PaginatorFooter.None)
            .WithFergunEmotes(_fergunOptions)
            .WithLocalizedPrompts(_localizer)
            .Build();

        await _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);

        return FergunResult.FromSuccess();

        PageBuilder GeneratePage(int index)
        {
            var article = articles[index];

            var page = new PageBuilder()
                .WithTitle(article.Title.Truncate(EmbedBuilder.MaxTitleLength))
                .WithUrl($"https://{Context.Interaction.GetLanguageCode()}.wikipedia.org/?curid={article.Id}")
                .WithThumbnailUrl($"https://commons.wikimedia.org/w/index.php?title=Special:Redirect/file/Wikipedia-logo-v2-{Context.Interaction.GetLanguageCode()}.png")
                .WithDescription(article.Extract.Truncate(EmbedBuilder.MaxDescriptionLength))
                .WithFooter(_localizer["Wikipedia Search | Page {0} of {1}", index + 1, articles.Length])
                .WithColor(Color.Orange);

            if (Context.Channel.IsNsfw() && article.Image is not null)
            {
                if (article.Image.Width >= 500 && article.Image.Height >= 500)
                {
                    page.WithImageUrl(article.Image.Url);
                }
                else
                {
                    page.WithThumbnailUrl(article.Image.Url);
                }
            }

            return page;
        }
    }

    [SlashCommand("wolfram", "Asks Wolfram|Alpha about something.")]
    public async Task<RuntimeResult> WolframAlpha([Autocomplete(typeof(WolframAlphaAutocompleteHandler))] [Summary(description: "Something to calculate or know about.")] string input)
    {
        await Context.Interaction.DeferAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await _wolframAlphaClient.GetResultsAsync(input, Context.Interaction.GetLanguageCode(), cts.Token);

        if (result.Type == WolframAlphaResultType.Error)
        {
            return FergunResult.FromError(_localizer["Failed to get the results. Status code: {0}. Error message: {1}", result.StatusCode!, result.ErrorMessage!]);
        }

        if (result.Type == WolframAlphaResultType.DidYouMean)
        {
            return FergunResult.FromError(_localizer["No results found. Did you mean...{0}", $"\n- {string.Join("\n- ", result.DidYouMean)}"]);
        }

        if (result.Type == WolframAlphaResultType.FutureTopic)
        {
            var embed = new EmbedBuilder()
                .WithTitle(result.FutureTopic!.Topic)
                .WithDescription(result.FutureTopic!.Message)
                .WithThumbnailUrl(Constants.WolframAlphaLogoUrl)
                .WithColor(Color.Red)
                .Build();

            await Context.Interaction.FollowupAsync(embed: embed);
            return FergunResult.FromSuccess();
        }

        if (result.Pods.Count == 0 || result.Type == WolframAlphaResultType.NoResult)
        {
            return FergunResult.FromError(_localizer["Wolfram|Alpha doesn't understand your query."]);
        }

        var builders = new List<EmbedBuilder>();
        var images = new Dictionary<int, byte[]>();
        var cachedAttachments = new Dictionary<int, string>();

        var topEmbed = new EmbedBuilder()
            .WithTitle(_localizer["Wolfram|Alpha Results"])
            .WithThumbnailUrl(Constants.WolframAlphaLogoUrl)
            .WithColor(Color.Red);

        // TODO: Add warnings and assumptions
        foreach (var pod in result.Pods)
        {
            if (pod.SubPods.Count == 0) // Shouldn't happen
                continue;

            if (pod.SubPods.Count == 1)
            {
                var subPod = pod.SubPods[0];

                // If there's data in plain text and there isn't a newline, use that instead
                if (!string.IsNullOrEmpty(subPod.PlainText) && !subPod.PlainText.Contains('\n'))
                {
                    topEmbed.AddField(pod.Title, subPod.PlainText, true);
                }
                else
                {
                    var builder = new EmbedBuilder()
                        .WithDescription(Format.Bold(pod.Title));

                    if (subPod.Image.IsDataPresent)
                    {
                        builder.WithImageUrl($"attachment://{builders.Count + 1}.gif");
                        images.Add(builders.Count, subPod.Image.Data);
                    }
                    else
                    {
                        builder.WithImageUrl(subPod.Image.SourceUrl);
                    }

                    builder.WithColor(Color.Red);
                    builders.Add(builder);
                }
            }
            else
            {
                // TODO: Merge subpods images
            }
        }

        var paginator = new WolframAlphaPaginatorBuilder()
            .AddUser(Context.User)
            .WithPageFactory(GeneratePage)
            .WithActionOnCancellation(ActionOnStop.DisableInput)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .WithMaxPageIndex(builders.Count == 0 ? 0 : builders.Count - 1)
            .WithFooter(PaginatorFooter.None)
            .WithFergunEmotes(_fergunOptions)
            .WithCacheLoadedPages(false)
            .WithLocalizedPrompts(_localizer)
            .Build();

        await _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10),
            InteractionResponseType.DeferredChannelMessageWithSource, cancellationToken: CancellationToken.None);

        return FergunResult.FromSuccess();

        IPageBuilder GeneratePage(int index, int lastIndex, IUserMessage? message)
        {
            var builder = new MultiEmbedPageBuilder();
            if (topEmbed.Fields.Count == 0) // No info in plain text
            {
                builders[index].WithTitle(_localizer["Wolfram|Alpha Results"])
                    .WithThumbnailUrl(Constants.WolframAlphaLogoUrl);
            }
            else
            {
                builder.AddBuilder(topEmbed);
            }

            if (builders.Count == 0)
            {
                topEmbed.WithFooter(_localizer["WolframAlpha Results | Page {0} of {1}", index + 1, 1], Constants.WolframAlphaLogoUrl);

                return builder;
            }

            if (cachedAttachments.TryGetValue(lastIndex, out string? imageUrl))
            {
                if (images.Remove(lastIndex))
                {
                    builders[lastIndex].WithImageUrl(imageUrl);
                }
            }
            else
            {
                var first = message?.Embeds?.FirstOrDefault(x => x.Image.HasValue && x.Image.Value.Url.StartsWith("https://cdn.discordapp.com/attachments"));

                if (first is not null)
                {
                    cachedAttachments.Add(lastIndex, first.Image!.Value.Url);
                }
            }

            return builder
                .AddBuilder(builders[index].WithFooter(_localizer["WolframAlpha Results | Page {0} of {1}", index + 1, builders.Count], Constants.WolframAlphaLogoUrl))
                .WithAttachmentsFactory(() => GetAttachments(index));
        }

        IEnumerable<FileAttachment> GetAttachments(int index)
        {
            return images.TryGetValue(index, out byte[]? bytes)
                ? new FileAttachment[] { new(new MemoryStream(bytes), $"{index + 1}.gif") }
                : Enumerable.Empty<FileAttachment>();
        }
    }

    [SlashCommand("youtube", "Sends a paginator containing YouTube videos.")]
    public async Task<RuntimeResult> YouTubeAsync([Autocomplete(typeof(YouTubeAutocompleteHandler))] [Summary(description: "The search query.")] string query)
    {
        await Context.Interaction.DeferAsync();

        var videos = await _searchClient.GetVideosAsync(query).Take(10);

        switch (videos.Count)
        {
            case 0:
                return FergunResult.FromError(_localizer["No results."]);

            case 1:
                await Context.Interaction.FollowupAsync(videos[0].Url);
                break;
                
            default:
                var paginator = new LazyPaginatorBuilder()
                    .AddUser(Context.User)
                    .WithPageFactory(GeneratePage)
                    .WithActionOnCancellation(ActionOnStop.DisableInput)
                    .WithActionOnTimeout(ActionOnStop.DisableInput)
                    .WithMaxPageIndex(videos.Count - 1)
                    .WithFooter(PaginatorFooter.None)
                    .WithFergunEmotes(_fergunOptions)
                    .WithLocalizedPrompts(_localizer)
                    .Build();

                await _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);
                break;
        }

        return FergunResult.FromSuccess();
        
        PageBuilder GeneratePage(int index) => new PageBuilder().WithText($"{videos[index].Url}\n{_localizer["Page {0} of {1}", index + 1, videos.Count]}");
    }
}