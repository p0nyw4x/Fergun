﻿using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace Fergun.Apis.WolframAlpha;

/// <summary>
/// Represents the default WolframAlpha client.
/// </summary>
public sealed class WolframAlphaClient : IWolframAlphaClient, IDisposable
{
    private const string _defaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/105.0.0.0 Safari/537.36";
    private static readonly Uri _resultsUri = new("wss://www.wolframalpha.com/n/v1/api/fetcher/results");
    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WolframAlphaClient"/> class.
    /// </summary>
    public WolframAlphaClient()
        : this(new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WolframAlphaClient"/> class using the specified <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="httpClient">An instance of <see cref="HttpClient"/>.</param>
    public WolframAlphaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_defaultUserAgent);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetAutocompleteResultsAsync(string input, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = await _httpClient.GetStreamAsync(new Uri($"https://www.wolframalpha.com/n/v1/api/autocomplete/?i={Uri.EscapeDataString(input)}"), cancellationToken).ConfigureAwait(false);

        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

        return document
            .RootElement
            .GetProperty("results")
            .EnumerateArray()
            .Select(x => x.GetProperty("input").GetString()!)
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<IWolframAlphaResult> GetResultsAsync(string input, string language, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(language);
        cancellationToken.ThrowIfCancellationRequested();

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("User-Agent", _defaultUserAgent);

        await webSocket.ConnectAsync(_resultsUri, cancellationToken).ConfigureAwait(false);

        var encodedInput = JsonEncodedText.Encode(input);

        var bufferWriter = new ArrayBufferWriter<byte>(126 + language.Length * 2 + encodedInput.EncodedUtf8Bytes.Length);
        await using var writer = new Utf8JsonWriter(bufferWriter);

        writer.WriteStartObject();
        writer.WriteString("type", "init");
        writer.WriteString("lang", language);
        
        writer.WriteStartArray("messages");
        writer.WriteStartObject();
        writer.WriteString("type", "newQuery");
        writer.WriteNull("locationId");
        writer.WriteString("language", language);
        writer.WriteBoolean("requestSidebarAd", false);
        writer.WriteString("input", encodedInput);
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await webSocket.SendAsync(bufferWriter.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        var wolframResult = new WolframAlphaResult();
        var pods = new SortedSet<WolframAlphaPod>();
        var positions = new HashSet<int>();

        while (webSocket.State == WebSocketState.Open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var builder = new OwnedMemorySequenceBuilder<byte>();

            ValueWebSocketReceiveResult result;

            do
            {
                var owner = MemoryPool<byte>.Shared.Rent(4096);
                try
                {
                    result = await webSocket.ReceiveAsync(owner.Memory, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    owner.Dispose();
                    throw;
                }

                builder.Append(owner, owner.Memory[..result.Count]);
            } while (!result.EndOfMessage);

            var sequence = builder.Build();

            using var document = JsonDocument.Parse(sequence);
            string? type = document.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "pods":
                    foreach (var pod in document.RootElement.GetProperty("pods").EnumerateArray())
                    {
                        if (!pod.GetProperty("error").GetBoolean() &&
                            pod.TryGetProperty("numsubpods", out var subPodCount) && subPodCount.GetInt32() != 0 &&
                            positions.Add(pod.GetProperty("position").GetInt32()))
                        {
                            pods.Add(pod.Deserialize<WolframAlphaPod>()!);
                        }
                    }
                    break;

                case "didyoumean": // After queryComplete, the API returns info about one of the interpretations from didyoumean
                    wolframResult.Type = WolframAlphaResultType.DidYouMean;
                    string[] values = document
                        .RootElement
                        .GetProperty("didyoumean")
                        .EnumerateArray()
                        .Select(x => x.GetProperty("val").GetString()!)
                        .ToArray();

                    wolframResult.DidYouMean = Array.AsReadOnly(values);
                    break;

                case "futureTopic":
                    wolframResult.Type = WolframAlphaResultType.FutureTopic;
                    wolframResult.FutureTopic = document.RootElement.GetProperty("futureTopic").Deserialize<WolframAlphaFutureTopic>();
                    break;

                case "noResult":
                    wolframResult.Type = WolframAlphaResultType.NoResult;
                    break;

                case "queryComplete":
                    if (wolframResult.Type == WolframAlphaResultType.Unknown)
                    {
                        wolframResult.Type = WolframAlphaResultType.Success;
                    }
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "error":
                    wolframResult.Type = WolframAlphaResultType.Error;
                    wolframResult.StatusCode = document.RootElement.GetProperty("status").GetInt32();
                    wolframResult.ErrorMessage = document.RootElement.GetProperty("message").GetString();
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                    break;

            }
        }

        wolframResult.Pods = pods;

        return wolframResult;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WolframAlphaClient));
        }
    }
}