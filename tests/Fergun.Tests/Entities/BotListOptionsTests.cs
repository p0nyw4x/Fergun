﻿using AutoBogus;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Fergun.Tests.Entities;

public class BotListOptionsTests
{
    [Theory]
    [MemberData(nameof(GetBotListOptionsTestData))]
    public void BotListOptions_Properties_Has_Expected_Values(BotListOptions options)
    {
        var other = new BotListOptions
        {
            UpdatePeriod = options.UpdatePeriod,
            Tokens = options.Tokens
        };

        Assert.Equal(options.UpdatePeriod, other.UpdatePeriod);
        Assert.True(options.Tokens.SequenceEqual(other.Tokens));
    }

    public static IEnumerable<object[]> GetBotListOptionsTestData()
        => AutoFaker.Generate<BotListOptions>(10).Select(x => new object[] { x });
}