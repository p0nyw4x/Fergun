﻿using System;
using System.Linq;
using System.Text;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Discord;

namespace Fergun.Apis.Dictionary;

/// <summary>
/// Contains methods that help formatting dictionary entries into markdown text that will be sent in a Discord embed.
/// </summary>
public static class DictionaryFormatter
{
    private static ReadOnlySpan<char> SuperscriptDigits => "\u2070\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079";

    private static ReadOnlySpan<char> SmallCapsChars => "ᴀʙᴄᴅᴇꜰɢʜɪᴊᴋʟᴍɴᴏᴘꞯʀꜱᴛᴜᴠᴡxʏᴢ";

    /// <summary>
    /// Formats the title of an entry.
    /// </summary>
    /// <param name="entry">The dictionary entry.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatEntry(IDictionaryEntry entry)
    {
        var builder = new StringBuilder($"**{entry.Entry}**");

        if (entry.Homograph is not null)
        {
            builder.Append(SuperscriptDigits[entry.Homograph.Value]);
        }

        builder.Append(' ');
        builder.AppendJoin(' ', entry.EntryVariants.Select(x => FormatHtml(x)));

        if (entry.Pronunciation is not null)
        {
            if (entry.Pronunciation.Spell.Count > 0)
            {
                builder.AppendJoin(' ', entry.Pronunciation.Spell.Select(x => $"[ {FormatHtml(x).Trim()} ]"));
            }
            else if (!string.IsNullOrEmpty(entry.Pronunciation.Ipa))
            {
                builder.Append($"/ {FormatHtml(entry.Pronunciation.Ipa).Trim()} /");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a part of speech block.
    /// </summary>
    /// <param name="block">The part of speech block.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatPartOfSpeechBlock(IDictionaryEntryBlock block)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(block.PartOfSpeech))
        {
            builder.Append(FormatHtml(block.PartOfSpeech));

            if (!string.IsNullOrEmpty(block.SupplementaryInfo))
            {
                builder.Append($" {FormatHtml(block.SupplementaryInfo)}");
            }

            builder.Append("\n\n");
        }

        foreach (var def in block.Definitions)
        {
            var definition = new StringBuilder($"{def.Order}. ");
            if (!string.IsNullOrEmpty(def.PredefinitionContent))
            {
                definition.Append($"{FormatHtml(def.PredefinitionContent)} ");
            }

            definition.Append(FormatHtml(def.Definition, true));

            if (!string.IsNullOrEmpty(def.PostdefinitionContent))
            {
                definition.Append($": {FormatHtml(def.PostdefinitionContent)}");
            }

            foreach (var subDefinition in def.Subdefinitions)
            {
                definition.Append($"\n\u200b \u200b \u200b \u200b - {FormatHtml(subDefinition.Definition, true)}");
            }

            definition.Append("\n\n");

            if (builder.Length + definition.Length <= EmbedBuilder.MaxDescriptionLength)
            {
                builder.Append(definition);
            }
            else
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the extra information of a dictionary entry.
    /// </summary>
    /// <param name="entry">The dictionary entry.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatExtraInformation(IDictionaryEntry entry)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(entry.Origin))
        {
            builder.Append($"**Origin of {entry.Entry}**\n{FormatHtml(entry.Origin)}\n\n");
        }

        foreach (var note in entry.SupplementaryNotes)
        {
            builder.Append($"**{note.Type}**\n");
            builder.AppendJoin('\n', note.Content.Select(x => FormatHtml(x)));
            builder.Append("\n\n");
        }

        foreach (string spelling in entry.VariantSpellings)
        {
            builder.Append(FormatHtml(spelling));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatHtml(string htmlText, bool newLineExample = false)
    {
        if (string.IsNullOrEmpty(htmlText))
            return htmlText;

        var parser = new HtmlParser();
        using var document = parser.ParseDocument(htmlText);

        if (!document.Body!.ChildNodes.Any())
        {
            return htmlText;
        }

        var builder = new StringBuilder();

        foreach (var element in document.Body!.ChildNodes)
        {
            if (element is IHtmlSpanElement span)
            {
                string className = span.ClassName ?? string.Empty;
                string content = span.TextContent;

                if (className == "luna-example italic" && newLineExample)
                {
                    builder.Append($"\n> *{content}*");
                } // Sometimes there's text instead of numbers in a superscript class (e.g., satire)
                else if (className == "superscript" && content.Length == 1 && content[0] >= '0' && content[0] <= '9')
                {
                    builder.Append(SuperscriptDigits[content[0] - '0']);
                }
                else if (className == "luna-wud small-caps")
                {
                    builder.Append(string.Create(content.Length, content, (converted, state) =>
                    {
                        for (int i = 0; i < converted.Length; i++)
                        {
                            converted[i] = state[i] >= 'a' && state[i] <= 'z' ? SmallCapsChars[state[i] - 'a'] : state[i];
                        }
                    }));
                }
                else if (className.EndsWith("italic") || className.EndsWith("pos"))
                {
                    builder.Append($"*{content}*");
                }
                else if (className.EndsWith("bold"))
                {
                    builder.Append($"**{content}**");
                }
                else if (className == "luna-def-number")
                {
                    builder.Append($"\n**{content}**");
                }
                else
                {
                    builder.Append(content);
                }
            }
            else if (element is IHtmlAnchorElement anchor)
            {
                builder.Append($"[{anchor.Text}](https://dictionary.com{anchor.GetAttribute("href")})");
            }
            else
            {
                builder.Append(element.TextContent);
            }
        }

        return builder.ToString();
    }
}