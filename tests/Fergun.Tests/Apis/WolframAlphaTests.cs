﻿using Fergun.Apis.WolframAlpha;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using Moq;
using Xunit;
using System.Threading;
using System.Text.Json;

namespace Fergun.Tests.Apis;

public class WolframAlphaTests
{
    private readonly IWolframAlphaClient _wolframAlphaClient = new WolframAlphaClient();

    [Theory]
    [InlineData("2 +")]
    [InlineData("1/6")]
    public async Task GetAutocompleteResultsAsync_Returns_Valid_Results(string input)
    {
        var results = await _wolframAlphaClient.GetAutocompleteResultsAsync(input, CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.All(results, Assert.NotEmpty);
    }

    [Fact]
    public async Task GetAutocompleteResultsAsync_Throws_OperationCanceledException_With_Canceled_CancellationToken()
    {
        var cts = new CancellationTokenSource(0);
        await Assert.ThrowsAsync<OperationCanceledException>(() => _wolframAlphaClient.GetAutocompleteResultsAsync(It.IsAny<string>(), cts.Token));
    }

    [Fact]
    public async Task SendQueryAsync_Returns_Successful_Result()
    {
        var result = await _wolframAlphaClient.SendQueryAsync("Chicag", "en");

        Assert.Equal(WolframAlphaResultType.Success, result.Type);
        Assert.NotEmpty(result.Warnings);
        Assert.All(result.Warnings, warning => Assert.NotEmpty(warning.Text));

        Assert.NotEmpty(result.Pods);

        foreach (var pod in result.Pods)
        {
            Assert.NotEmpty(pod.SubPods);
            Assert.All(pod.SubPods, Assert.NotNull);
            Assert.NotEmpty(pod.Title);
            Assert.NotEmpty(pod.Id);
            Assert.True(pod.Position > 0);

            foreach (var subPod in pod.SubPods)
            {
                Assert.NotNull(subPod.PlainText);
                Assert.NotNull(subPod.Title);
                Assert.NotNull(subPod.Image);

                Assert.True(Uri.IsWellFormedUriString(subPod.Image.SourceUrl, UriKind.Absolute));
                Assert.True(subPod.Image.Height > 0);
                Assert.True(subPod.Image.Width > 0);
                Assert.NotEmpty(subPod.Image.ContentType);
            }
        }
    }

    [Fact]
    public async Task SendQueryAsync_Returns_DidYouMean_Result()
    {
        var result = await _wolframAlphaClient.SendQueryAsync("kitten danger", "en", false);

        Assert.Equal(WolframAlphaResultType.DidYouMean, result.Type);
        Assert.NotEmpty(result.DidYouMeans);

        foreach (var suggestion in result.DidYouMeans)
        {
            Assert.InRange(suggestion.Score, 0, 1);
            Assert.Contains(suggestion.Level, new[] { "low", "medium", "high" });
            Assert.NotEmpty(suggestion.Value);
        }
    }

    [Fact]
    public async Task SendQueryAsync_Returns_FutureTopic_Result()
    {
        var result = await _wolframAlphaClient.SendQueryAsync("Microsoft Windows", "en");

        Assert.Equal(WolframAlphaResultType.FutureTopic, result.Type);
        Assert.NotNull(result.FutureTopic);
        Assert.NotEmpty(result.FutureTopic.Topic);
        Assert.NotEmpty(result.FutureTopic.Message);
    }

    [Fact]
    public async Task SendQueryAsync_Returns_No_Result()
    {
        var result = await _wolframAlphaClient.SendQueryAsync("oadf lds", "en");

        Assert.Equal(WolframAlphaResultType.NoResult, result.Type);
    }

    [Fact]
    public async Task SendQueryAsync_Returns_Error()
    {
        var result = await _wolframAlphaClient.SendQueryAsync(string.Empty, "en");

        Assert.Equal(WolframAlphaResultType.Error, result.Type);
        Assert.NotNull(result.ErrorInfo);
        Assert.Equal(1000, result.ErrorInfo.StatusCode);
        Assert.NotEmpty(result.ErrorInfo.Message);
    }

    [Fact]
    public async Task SendQueryAsync_Throws_OperationCanceledException_With_Canceled_CancellationToken()
    {
        var cts = new CancellationTokenSource(0);
        await Assert.ThrowsAsync<OperationCanceledException>(() => _wolframAlphaClient.SendQueryAsync("test", "en", It.IsAny<bool>(), cts.Token));
    }

    [Fact]
    public async Task Disposed_WolframAlphaClient_Usage_Throws_ObjectDisposedException()
    {
        (_wolframAlphaClient as IDisposable)?.Dispose();
        (_wolframAlphaClient as IDisposable)?.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _wolframAlphaClient.GetAutocompleteResultsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _wolframAlphaClient.SendQueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [MemberData(nameof(GetWolframAlphaErrorInfoConverterData))]
    public void WolframAlphaErrorInfoConverter_Returns_Expected_Results(string input, WolframAlphaErrorInfo? expectedResult)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new WolframAlphaErrorInfoConverter());

        var result = JsonSerializer.Deserialize<WolframAlphaErrorInfo?>(input, options);

        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [MemberData(nameof(ArrayOrObjectConverterData))]
    public void ArrayOrObjectConverter_Returns_Expected_Results(string input, IReadOnlyList<WolframAlphaWarning> expectedResult)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ArrayOrObjectConverter<WolframAlphaWarning>());

        var result = JsonSerializer.Deserialize<IReadOnlyList<WolframAlphaWarning>>(input, options);

        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void WolframAlphaErrorInfoConverter_Throws_NotSupportedException()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new WolframAlphaErrorInfoConverter());

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new WolframAlphaErrorInfo(0, "test"), options));
    }

    [Fact]
    public void ArrayOrObjectConverter_Throws_Exceptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ArrayOrObjectConverter<string>());

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IReadOnlyList<string>>("true", options));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<IReadOnlyList<string>>(new[] { "test" }, options));
    }

    public static IEnumerable<object?[]> GetWolframAlphaErrorInfoConverterData()
    {
        yield return new object?[] {"true", null };
        yield return new object?[] { "false", null };
        yield return new object?[] { "{\"code\":\"1000\",\"msg\":\"error\"}", new WolframAlphaErrorInfo(1000, "error") };
    }

    public static IEnumerable<object[]> ArrayOrObjectConverterData()
    {
        const string json = "{\"text\":\"Error message\"}";
        var suggestions = new[] { new WolframAlphaWarning("Error message") };

        yield return new object[] { json, suggestions };
        yield return new object[] { $"[{json}]", suggestions };
    }
}