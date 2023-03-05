﻿using Discord.Interactions;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Polly.Registry;
using Polly;
using Fergun.Apis.Dictionary;

namespace Fergun.Modules.Handlers;

public class DictionaryAutocompleteHandler : AutocompleteHandler
{
    /// <inheritdoc />
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        string? text = (autocompleteInteraction.Data.Current.Value as string)?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            return AutocompletionResult.FromSuccess();
        }

        await using var scope = services.CreateAsyncScope();

        var dictionaryClient = scope
            .ServiceProvider
            .GetRequiredService<IDictionaryClient>();

        var policy = scope
            .ServiceProvider
            .GetRequiredService<IReadOnlyPolicyRegistry<string>>()
            .Get<IAsyncPolicy<IReadOnlyList<IDictionaryWord>>>("DictionaryPolicy");

        var words = await policy.ExecuteAsync((_, ct) => dictionaryClient.GetSearchResultsAsync(text, ct), new Context(text), CancellationToken.None);

        var results = words
            .Where(x => x.Reference.Type == "definitions")
            .Take(25)
            .Select(x => new AutocompleteResult(x.DisplayText, x.Reference.Identifier));

        return AutocompletionResult.FromSuccess(results);
    }
}