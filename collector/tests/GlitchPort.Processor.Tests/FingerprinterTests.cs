using System.Security.Cryptography;
using System.Text;
using GlitchPort.Processor.Grouping;
using Xunit;

namespace GlitchPort.Processor.Tests;

public sealed class FingerprinterTests
{
    private const string Sha256HexPattern = "^[0-9a-f]{64}$";

    [Fact]
    public void Compute_WithCustomFingerprint_JoinsEntriesWithNewlineAndSkipsOtherSources()
    {
        var input = Input(
            exceptions: [ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game")])],
            message: "ignored message",
            logger: "ignored logger",
            custom: ["{{ default }}", "checkout"]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("custom:{{ default }}\ncheckout"), result.Fingerprint);
        Assert.Matches(Sha256HexPattern, result.Fingerprint);
    }

    [Fact]
    public void Compute_WithEmptyCustomFingerprint_FallsBackToException()
    {
        var input = Input(
            exceptions: [ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game")])],
            custom: []);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("exception:NullReferenceException\nGame.Crash"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithException_UsesOnlyInAppFrames()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "NullReferenceException",
                    "boom",
                    [
                        Frame("EngineTick", "Engine", inApp: false),
                        Frame("Crash", "Game"),
                        Frame("Broadcast", "Engine", inApp: false),
                    ]),
            ],
            message: "ignored message",
            logger: "ignored logger");

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("exception:NullReferenceException\nGame.Crash"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithNestedExceptions_UsesTheLastOutermostException()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith("InvalidOperationException", "inner", [Frame("Inner", "Game")]),
                ExceptionWith("NullReferenceException", "outer", [Frame("Crash", "Game")]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("exception:NullReferenceException\nGame.Crash"), result.Fingerprint);
        Assert.Equal("NullReferenceException: outer", result.Title);
        Assert.Equal("Game.Crash", result.Culprit);
    }

    [Fact]
    public void Compute_WithExceptionWithoutInAppFrames_UsesEveryFrame()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "InvalidOperationException",
                    "boom",
                    [
                        Frame("Main", "Game", inApp: false),
                        Frame("Tick", "Engine", inApp: false),
                    ]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(
            Hash("exception:InvalidOperationException\nGame.Main\nEngine.Tick"),
            result.Fingerprint);
    }

    [Fact]
    public void Compute_WithManyInAppFrames_UsesLastFiveCrashSideFrames()
    {
        var frames = Enumerable
            .Range(0, 7)
            .Select(index => Frame($"Function{index}", "Game"))
            .ToArray();

        var input = Input(exceptions: [ExceptionWith("InvalidOperationException", "boom", frames)]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(
            Hash(
                "exception:InvalidOperationException\n" +
                "Game.Function2\nGame.Function3\nGame.Function4\nGame.Function5\nGame.Function6"),
            result.Fingerprint);
    }

    [Fact]
    public void Compute_WithFramesOutsideTheCrashSideWindow_IgnoresThem()
    {
        var first = Enumerable
            .Range(0, 2)
            .Select(index => Frame($"Outer{index}", "Game"))
            .Concat(Enumerable.Range(0, 5).Select(index => Frame($"Crash{index}", "Game")))
            .ToArray();

        var second = Enumerable
            .Range(0, 2)
            .Select(index => Frame($"Different{index}", "Game"))
            .Concat(Enumerable.Range(0, 5).Select(index => Frame($"Crash{index}", "Game")))
            .ToArray();

        var firstResult = Fingerprinter.Compute(
            Input(exceptions: [ExceptionWith("InvalidOperationException", "boom", first)]));
        var secondResult = Fingerprinter.Compute(
            Input(exceptions: [ExceptionWith("InvalidOperationException", "boom", second)]));

        Assert.Equal(
            Hash(
                "exception:InvalidOperationException\n" +
                "Game.Crash0\nGame.Crash1\nGame.Crash2\nGame.Crash3\nGame.Crash4"),
            firstResult.Fingerprint);
        Assert.Equal(firstResult.Fingerprint, secondResult.Fingerprint);
    }

    [Fact]
    public void Compute_WithExceptionWithoutFrames_UsesNormalizedValue()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "TimeoutException",
                    "Request 123e4567-e89b-12d3-a456-426614174000 failed after   7 tries",
                    frames: null),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(
            Hash("exception:TimeoutException\nRequest <uuid> failed after <n> tries"),
            result.Fingerprint);
    }

    [Fact]
    public void Compute_WithExceptionWithoutFramesOrValue_UsesTypeOnlyMaterial()
    {
        var input = Input(exceptions: [ExceptionWith("TimeoutException", frames: null)]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("exception:TimeoutException\n"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithMessage_UsesNormalizedFirstLineAndLogger()
    {
        var input = Input(
            message: "Something broke\nsecond line is ignored",
            logger: "checkout");

        var result = Fingerprinter.Compute(input);

        Assert.Equal(Hash("message:Something broke\ncheckout"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithMessageAndNoLogger_AppendsEmptyLogger()
    {
        var result = Fingerprinter.Compute(Input(message: "Something broke"));

        Assert.Equal(Hash("message:Something broke\n"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithNoData_ProducesStableFingerprint()
    {
        var result = Fingerprinter.Compute(Input());

        Assert.Equal(Hash("message:\n"), result.Fingerprint);
        Assert.Equal(string.Empty, result.Title);
        Assert.Null(result.Culprit);
    }

    [Theory]
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000", "id <uuid>")]
    [InlineData("pointer 0xDEADBEEF leaked", "pointer <hex> leaked")]
    [InlineData("bad read at 0x0000DEAD (id 42)", "bad read at <hex> (id <n>)")]
    [InlineData("hash 0123456789abcdef0123", "hash <hex>")]
    [InlineData("short deadbeefdeadbee stays", "short deadbeefdeadbee stays")]
    [InlineData("code 42 at 7", "code <n> at <n>")]
    [InlineData("  spaced \t\t out  ", "spaced out")]
    public void Compute_WithMessage_NormalizesVolatileValues(string message, string expected)
    {
        var result = Fingerprinter.Compute(Input(message: message));

        Assert.Equal(Hash($"message:{expected}\n"), result.Fingerprint);
    }

    [Fact]
    public void Compute_WithValuesThatNormalizeIdentically_ProducesTheSameFingerprint()
    {
        var first = Fingerprinter.Compute(
            Input(message: "Object 123e4567-e89b-12d3-a456-426614174000 at 0xDEADBEEF"));
        var second = Fingerprinter.Compute(
            Input(message: "Object 987fcdeb-51a2-43f1-9876-543210fedcba at 0x00000001"));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Theory]
    [InlineData("Crash", "Game", "crash.cs", "Game.Crash")]
    [InlineData(" Crash ", " Game ", "crash.cs", "Game.Crash")]
    [InlineData("Crash", null, "crash.cs", "Crash")]
    [InlineData("Crash", "  ", "crash.cs", "Crash")]
    [InlineData(null, "Game", "crash.cs", "crash.cs")]
    [InlineData(null, null, "crash.cs", "crash.cs")]
    public void Compute_WithFrame_FallsBackFromModuleFunctionToFunctionToFilename(
        string? function,
        string? module,
        string? filename,
        string expectedFrame)
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith("InvalidOperationException", "boom", [Frame(function, module, filename)]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(
            Hash($"exception:InvalidOperationException\n{expectedFrame}"),
            result.Fingerprint);
    }

    [Fact]
    public void Compute_WithEventsDifferingOnlyInLineAndColumn_ProducesTheSameFingerprint()
    {
        var first = Fingerprinter.Compute(
            Input(exceptions:
            [
                ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game", "crash.cs", 10, 2)]),
            ]));
        var second = Fingerprinter.Compute(
            Input(exceptions:
            [
                ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game", "crash.cs", 99, 7)]),
            ]));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(Hash("exception:NullReferenceException\nGame.Crash"), first.Fingerprint);
    }

    [Fact]
    public void Compute_CalledTwice_ReturnsTheSameFingerprint()
    {
        var input = Input(
            exceptions: [ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game")])]);

        var first = Fingerprinter.Compute(input);
        var second = Fingerprinter.Compute(input);

        Assert.Equal(first, second);
        Assert.Matches(Sha256HexPattern, first.Fingerprint);
        Assert.Equal(first.Fingerprint, first.Fingerprint.ToLowerInvariant());
    }

    [Fact]
    public void Compute_WithNullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Fingerprinter.Compute(null!));
    }

    [Fact]
    public void Compute_WithException_BuildsTitleFromTypeAndFirstValueLine()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "NullReferenceException",
                    "Object reference not set\r\nat Foo()"),
            ],
            message: "ignored message");

        var result = Fingerprinter.Compute(input);

        Assert.Equal("NullReferenceException: Object reference not set", result.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\nstack trace")]
    public void Compute_WithExceptionAndBlankValue_UsesTypeAsTitle(string value)
    {
        var input = Input(exceptions: [ExceptionWith("NullReferenceException", value)]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal("NullReferenceException", result.Title);
    }

    [Fact]
    public void Compute_WithLongExceptionValue_TruncatesTitleToTwoHundredCharacters()
    {
        var input = Input(
            exceptions: [ExceptionWith("LongError", new string('x', 250))]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal(200, result.Title.Length);
        Assert.StartsWith("LongError: ", result.Title);
        Assert.Equal(new string('x', 189), result.Title["LongError: ".Length..]);
    }

    [Fact]
    public void Compute_WithMessageOnly_BuildsTitleFromFirstLine()
    {
        var result = Fingerprinter.Compute(Input(message: "First line\nsecond line"));

        Assert.Equal("First line", result.Title);
    }

    [Fact]
    public void Compute_WithLongMessage_TruncatesTitleToTwoHundredCharacters()
    {
        var result = Fingerprinter.Compute(Input(message: new string('y', 300)));

        Assert.Equal(200, result.Title.Length);
        Assert.Equal(new string('y', 200), result.Title);
    }

    [Fact]
    public void Compute_WithException_UsesLastInAppFrameForCulprit()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "NullReferenceException",
                    "boom",
                    [
                        Frame("Main", "Game"),
                        Frame("Crash", "Engine"),
                        Frame("Internal", "Engine", inApp: false),
                    ]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal("Engine.Crash", result.Culprit);
    }

    [Fact]
    public void Compute_WithInAppFrameMissingModule_SkipsItForCulprit()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "NullReferenceException",
                    "boom",
                    [
                        Frame("Main", "Game"),
                        Frame("Crash", module: null, filename: "crash.cs"),
                    ]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal("Game.Main", result.Culprit);
    }

    [Fact]
    public void Compute_WithoutInAppFrameWithModuleAndFunction_ReturnsNullCulprit()
    {
        var input = Input(
            exceptions:
            [
                ExceptionWith(
                    "NullReferenceException",
                    "boom",
                    [
                        Frame("Main", "Game", inApp: false),
                        Frame(null, "Game", "crash.cs"),
                    ]),
            ]);

        var result = Fingerprinter.Compute(input);

        Assert.Null(result.Culprit);
    }

    [Fact]
    public void Compute_WithMessageOnly_ReturnsNullCulprit()
    {
        var result = Fingerprinter.Compute(Input(message: "Something broke"));

        Assert.Null(result.Culprit);
    }

    [Fact]
    public void Compute_WithCustomFingerprint_StillReportsCulprit()
    {
        var input = Input(
            exceptions: [ExceptionWith("NullReferenceException", "boom", [Frame("Crash", "Game")])],
            custom: ["checkout"]);

        var result = Fingerprinter.Compute(input);

        Assert.Equal("Game.Crash", result.Culprit);
    }

    private static FingerprintInput Input(
        IReadOnlyList<EventException>? exceptions = null,
        string? message = null,
        string? logger = null,
        IReadOnlyList<string>? custom = null) =>
        new(exceptions ?? Array.Empty<EventException>(), message, logger, custom);

    private static EventException ExceptionWith(
        string type,
        string? value = null,
        IReadOnlyList<EventFrame>? frames = null,
        string? module = null) =>
        new(type, value, module, frames ?? Array.Empty<EventFrame>());

    private static EventFrame Frame(
        string? function,
        string? module = null,
        string? filename = null,
        int? lineNo = null,
        int? colNo = null,
        bool inApp = true) =>
        new(function, module, filename, lineNo, colNo, inApp);

    private static string Hash(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}
