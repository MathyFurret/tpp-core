﻿using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ArgsParsing.TypeParsers;
using ArgsParsing.Types;
using Models;
using Moq;
using NUnit.Framework;
using Persistence.Repos;

namespace ArgsParsing.Tests
{
    public class TypeParsersTest
    {
        [Test]
        public async Task TestAnyOrderParser()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new AnyOrderParser(argsParser));
            argsParser.AddArgumentParser(new StringParser());
            argsParser.AddArgumentParser(new IntParser());

            var args1 = ImmutableList.Create("123", "foo");
            var args2 = ImmutableList.Create("foo", "123");
            (int int1, string string1) = (await argsParser.Parse<AnyOrder<int, string>>(args1)).AsTuple();
            (int int2, string string2) = (await argsParser.Parse<AnyOrder<int, string>>(args2)).AsTuple();
            Assert.AreEqual(123, int1);
            Assert.AreEqual(123, int2);
            Assert.AreEqual("foo", string1);
            Assert.AreEqual("foo", string2);

            var ex = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<AnyOrder<int, string>>(ImmutableList.Create("foo", "bar")));
            Assert.AreEqual("did not recognize 'foo' as a number", ex.Message);
        }

        [Test]
        public async Task TestDateTimeParser()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new DateTimeUtcParser());

            var result1 =
                await argsParser.Parse<DateTime>(args: ImmutableList.Create("2020-03-22", "01:59:20Z"));
            var result2 = await argsParser.Parse<DateTime>(args: ImmutableList.Create("2020-03-22T01:59:20Z"));

            var refDateTime = DateTime.SpecifyKind(DateTime.Parse("2020-03-22 01:59:20+00"), DateTimeKind.Utc);
            Assert.AreEqual(refDateTime, result1);
            Assert.AreEqual(refDateTime, result2);
            Assert.AreEqual(result1, result2);

            var ex1 = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<DateTime>(ImmutableList.Create("2020-03-22T01:59:20+02")));
            var ex2 = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<DateTime>(ImmutableList.Create("asdasdasd")));
            Assert.AreEqual("did not recognize '2020-03-22T01:59:20+02' as a UTC-datetime", ex1.Message);
            Assert.AreEqual("did not recognize 'asdasdasd' as a UTC-datetime", ex2.Message);
        }

        [Test]
        public async Task TestOneOfParser()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new OneOfParser(argsParser));
            argsParser.AddArgumentParser(new StringParser());
            argsParser.AddArgumentParser(new IntParser());

            OneOf<int, string> result1 = await argsParser.Parse<OneOf<int, string>>(ImmutableList.Create("123"));
            OneOf<int, string> result2 = await argsParser.Parse<OneOf<int, string>>(ImmutableList.Create("foo"));
            Assert.IsTrue(result1.Item1.IsPresent);
            Assert.IsFalse(result1.Item2.IsPresent);
            Assert.AreEqual(123, result1.Item1.Value);
            Assert.IsFalse(result2.Item1.IsPresent);
            Assert.IsTrue(result2.Item2.IsPresent);
            Assert.AreEqual("foo", result2.Item2.Value);

            var ex = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<OneOf<int, int>>(ImmutableList.Create("foo")));
            Assert.AreEqual("did not recognize 'foo' as a number", ex.Message);
        }

        [Test]
        public async Task TestOptionalParser()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new OptionalParser(argsParser));
            argsParser.AddArgumentParser(new StringParser());
            argsParser.AddArgumentParser(new IntParser());

            var result1 = await argsParser.Parse<Optional<int>>(args: ImmutableList.Create("123"));
            var result2 = await argsParser.Parse<Optional<int>>(args: ImmutableList.Create<string>());
            (var result3, string _) = await argsParser
                .Parse<Optional<int>, string>(args: ImmutableList.Create("foo"));
            Assert.IsTrue(result1.IsPresent);
            Assert.AreEqual(123, result1.Value);
            Assert.IsFalse(result2.IsPresent);
            Assert.IsFalse(result3.IsPresent);
        }

        [Test]
        public async Task TestPrefixedNumberParsers()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new PokeyenParser());
            argsParser.AddArgumentParser(new TokensParser());

            int result1 = await argsParser.Parse<Pokeyen>(args: ImmutableList.Create("P11"));
            int result2 = await argsParser.Parse<Tokens>(args: ImmutableList.Create("T22"));
            Assert.AreEqual(11, result1);
            Assert.AreEqual(22, result2);

            var ex = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<Pokeyen>(args: ImmutableList.Create("X33")));
            Assert.AreEqual("did not recognize 'X33' as a 'P'-prefixed number", ex.Message);
        }

        [Test]
        public async Task TestTimeSpanParser()
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new TimeSpanParser());

            var result1 = await argsParser.Parse<TimeSpan>(args: ImmutableList.Create("8w3d20h48m5s"));
            var result2 = await argsParser.Parse<TimeSpan>(args: ImmutableList.Create("90d"));

            var expected = new TimeSpan(
                days: 8 * 7 + 3,
                hours: 20,
                minutes: 48,
                seconds: 5);
            Assert.AreEqual(expected, result1);
            Assert.AreEqual(TimeSpan.FromDays(90), result2);

            var ex1 = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<TimeSpan>(args: ImmutableList.Create("5s3d")));
            var ex2 = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<TimeSpan>(args: ImmutableList.Create("asdasdasd")));
            Assert.IsTrue(ex1.Message.Contains("did not recognize '5s3d' as a duration"));
            Assert.IsTrue(ex2.Message.Contains("did not recognize 'asdasdasd' as a duration"));
        }

        [Test]
        public async Task TestUserParser()
        {
            const string username = "some_name";
            var origUser = new User(
                id: "1234567890", name: username, twitchDisplayName: username.ToUpper(), simpleName: username,
                color: null, firstActiveAt: DateTime.UnixEpoch, lastActiveAt: DateTime.UnixEpoch,
                lastMessageAt: null, pokeyen: 0, tokens: 0);
            var userRepoMock = new Mock<IUserRepo>();
            userRepoMock
                .Setup(r => r.FindBySimpleName(username))
                .ReturnsAsync(origUser);
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new UserParser(userRepoMock.Object));

            var resultUser = await argsParser.Parse<User>(args: ImmutableList.Create(username));
            Assert.AreEqual(origUser, resultUser);

            var ex = Assert.ThrowsAsync<ArgsParseFailure>(() => argsParser
                .Parse<User>(args: ImmutableList.Create("some_unknown_name")));
            Assert.AreEqual("did not recognize a user with the name 'some_unknown_name'", ex.Message);
        }
    }
}
