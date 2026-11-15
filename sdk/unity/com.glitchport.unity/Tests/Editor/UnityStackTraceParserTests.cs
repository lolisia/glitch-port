#nullable enable
using NUnit.Framework;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>
    /// Verifies that Unity conditions and stack traces become the protocol v1
    /// exception chain with the documented ordering and in-app rules.
    /// </summary>
    /// <remarks>
    /// The "<c>LogExceptionCallbackShape*</c>", "<c>LogErrorCallbackShape*</c>"
    /// and "<c>RethrowChain*</c>" fixtures reproduce the condition and stack
    /// trace strings that Unity 6000.6.8f1 hands to
    /// <c>Application.logMessageReceivedThreaded</c> for
    /// <c>Debug.LogException</c> and <c>Debug.LogError</c>, with the script
    /// names replaced by game code. They are kept verbatim in shape because the
    /// parser's chain order and log-frame filtering depend on it:
    /// <code>
    /// ArgumentException: inner failure            &lt;- condition: innermost exception
    /// Game.Inventory.Add (System.String) (at Assets/Scripts/Inventory.cs:52)
    /// Rethrow as InvalidOperationException: ...   &lt;- next outer exception
    /// Game.Player.PickUp (System.String) (at Assets/Scripts/Player.cs:92)
    /// UnityEngine.Debug:LogException(Exception)   &lt;- log wrapper
    /// Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:44)
    ///                                             &lt;- log call site, not exception frames
    /// </code>
    /// The .NET <c>---&gt;</c> notation is the opposite order (outer exception
    /// first, inner cause below) and is covered separately.
    /// </remarks>
    [TestFixture]
    public sealed class UnityStackTraceParserTests
    {
        [Test]
        public void MonoEditorFrameWithAssetsPathIsParsed()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "NullReferenceException: Object reference not set to an instance of an object",
                "PlayerController.Update () (at Assets/Scripts/PlayerController.cs:42)");

            Assert.That(exceptions, Has.Count.EqualTo(1));

            var exception = exceptions[0];
            Assert.That(exception.Type, Is.EqualTo("NullReferenceException"));
            Assert.That(exception.Value, Is.EqualTo("Object reference not set to an instance of an object"));
            Assert.That(exception.Frames, Has.Count.EqualTo(1));

            var frame = exception.Frames[0];
            Assert.That(frame.Function, Is.EqualTo("Update"));
            Assert.That(frame.Module, Is.EqualTo("PlayerController"));
            Assert.That(frame.FileName, Is.EqualTo("Assets/Scripts/PlayerController.cs"));
            Assert.That(frame.LineNumber, Is.EqualTo(42));
            Assert.That(frame.InApp, Is.True);
        }

        [Test]
        public void PackageCacheColonFrameIsParsedOutsideApp()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "System.MissingMethodException: Method not found",
                "Namespace.Class:Method(System.String) (at ./Library/PackageCache/com.foo@1.0.0/Runtime/Bar.cs:10)");

            var frame = exceptions[0].Frames[0];
            Assert.That(frame.Function, Is.EqualTo("Method"));
            Assert.That(frame.Module, Is.EqualTo("Namespace.Class"));
            Assert.That(frame.FileName, Is.EqualTo("./Library/PackageCache/com.foo@1.0.0/Runtime/Bar.cs"));
            Assert.That(frame.LineNumber, Is.EqualTo(10));
            Assert.That(frame.InApp, Is.False);
        }

        [Test]
        public void Il2CppFrameWithoutFileIsParsed()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "System.NullReferenceException: Object reference not set to an instance of an object",
                "Namespace.Class.Method (System.String,System.Int32)");

            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));

            var frame = exceptions[0].Frames[0];
            Assert.That(frame.Function, Is.EqualTo("Method"));
            Assert.That(frame.Module, Is.EqualTo("Namespace.Class"));
            Assert.That(frame.FileName, Is.Null);
            Assert.That(frame.LineNumber, Is.Null);
            Assert.That(frame.InApp, Is.True);
        }

        [Test]
        public void FrameWithFileButWithoutLineKeepsTheFileName()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "System.Exception: boom",
                "Game.Update () (at Assets/Game.cs)");

            var frame = exceptions[0].Frames[0];
            Assert.That(frame.FileName, Is.EqualTo("Assets/Game.cs"));
            Assert.That(frame.LineNumber, Is.Null);
        }

        [Test]
        public void FramesAreReversedSoTheCrashSiteIsLast()
        {
            const string stackTrace =
                "Game.Crash () (at Assets/Game.cs:30)\n" +
                "Game.Update () (at Assets/Game.cs:20)\n" +
                "Game.Start () (at Assets/Game.cs:10)";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: boom", stackTrace);

            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(3));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Start"));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(10));
            Assert.That(exceptions[0].Frames[1].Function, Is.EqualTo("Update"));
            Assert.That(exceptions[0].Frames[1].LineNumber, Is.EqualTo(20));
            Assert.That(exceptions[0].Frames[2].Function, Is.EqualTo("Crash"));
            Assert.That(exceptions[0].Frames[2].LineNumber, Is.EqualTo(30));
            Assert.That(exceptions[0].Frames[2].InApp, Is.True);
        }

        [Test]
        public void LogExceptionCallbackShapeWithInnerExceptionReturnsInnerFirst()
        {
            // Application.logMessageReceivedThreaded, LogType.Exception:
            // Debug.LogException(new InvalidOperationException("pickup failed",
            //     new ArgumentException("inventory is full")))
            const string condition = "ArgumentException: inventory is full";
            const string stackTrace =
                "Game.Inventory.Add (System.String item) (at Assets/Scripts/Inventory.cs:52)\n" +
                "Game.Player.PickUp (System.String item) (at Assets/Scripts/Player.cs:88)\n" +
                "Rethrow as System.InvalidOperationException: pickup failed\n" +
                "Game.Player.PickUp (System.String item) (at Assets/Scripts/Player.cs:92)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:44)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));

            // The condition is the innermost exception and each "Rethrow as"
            // line introduces the next outer exception.
            var inner = exceptions[0];
            Assert.That(inner.Type, Is.EqualTo("ArgumentException"));
            Assert.That(inner.Value, Is.EqualTo("inventory is full"));
            Assert.That(inner.Frames, Has.Count.EqualTo(2));
            Assert.That(inner.Frames[0].Module, Is.EqualTo("Game.Player"));
            Assert.That(inner.Frames[0].Function, Is.EqualTo("PickUp"));
            Assert.That(inner.Frames[0].FileName, Is.EqualTo("Assets/Scripts/Player.cs"));
            Assert.That(inner.Frames[0].LineNumber, Is.EqualTo(88));
            Assert.That(inner.Frames[0].InApp, Is.True);
            Assert.That(inner.Frames[1].Module, Is.EqualTo("Game.Inventory"));
            Assert.That(inner.Frames[1].Function, Is.EqualTo("Add"));
            Assert.That(inner.Frames[1].FileName, Is.EqualTo("Assets/Scripts/Inventory.cs"));
            Assert.That(inner.Frames[1].LineNumber, Is.EqualTo(52));
            Assert.That(inner.Frames[1].InApp, Is.True);

            var outer = exceptions[1];
            Assert.That(outer.Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(outer.Value, Is.EqualTo("pickup failed"));
            Assert.That(outer.Frames, Has.Count.EqualTo(1));
            Assert.That(outer.Frames[0].Module, Is.EqualTo("Game.Player"));
            Assert.That(outer.Frames[0].LineNumber, Is.EqualTo(92));
            Assert.That(outer.Frames[0].InApp, Is.True);

            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogExceptionCallbackShapeDropsTheLogCallSiteFrames()
        {
            // Application.logMessageReceivedThreaded, LogType.Exception: the
            // throw site is inside a helper, so the trace continues with the
            // frames that called Debug.LogException after the wrapper.
            const string condition = "ArgumentException: deep inner";
            const string stackTrace =
                "Game.PlayerController.TakeDamage (System.Int32 amount) (at Assets/Scripts/PlayerController.cs:89)\n" +
                "Rethrow as InvalidOperationException: deep outer\n" +
                "Game.PlayerController.TakeDamage (System.Int32 amount) (at Assets/Scripts/PlayerController.cs:93)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.PlayerController:LogFatal(Exception) (at Assets/Scripts/PlayerController.cs:135)\n" +
                "Game.PlayerController:Update() (at Assets/Scripts/PlayerController.cs:130)\n" +
                "Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:98)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Type, Is.EqualTo("ArgumentException"));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(89));
            Assert.That(exceptions[1].Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].LineNumber, Is.EqualTo(93));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogExceptionCallbackShapeWithSingleExceptionDropsWrapperAndCallSiteFrames()
        {
            // Debug.LogException(thrownException) without an inner exception.
            const string condition = "InvalidOperationException: thrown then logged";
            const string stackTrace =
                "Game.EnemyBrain.Think () (at Assets/Scripts/EnemyBrain.cs:67)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.EnemyBrain:Update() (at Assets/Scripts/EnemyBrain.cs:74)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(1));

            var exception = exceptions[0];
            Assert.That(exception.Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exception.Value, Is.EqualTo("thrown then logged"));
            Assert.That(exception.Frames, Has.Count.EqualTo(1));
            Assert.That(exception.Frames[0].Module, Is.EqualTo("Game.EnemyBrain"));
            Assert.That(exception.Frames[0].Function, Is.EqualTo("Think"));
            Assert.That(exception.Frames[0].FileName, Is.EqualTo("Assets/Scripts/EnemyBrain.cs"));
            Assert.That(exception.Frames[0].LineNumber, Is.EqualTo(67));
            Assert.That(exception.Frames[0].InApp, Is.True);
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogExceptionCallbackShapeWithUnthrownOuterKeepsOnlyTheInnerFrames()
        {
            // Application.logMessageReceivedThreaded, LogType.Exception: the
            // inner exception was thrown, the outer only wraps it and was never
            // thrown, so the trace has no outer exception frames.
            const string condition = "ArgumentException: inner thrown, outer not thrown";
            const string stackTrace =
                "Game.Vendor.Buy (System.String item) (at Assets/Scripts/Vendor.cs:26)\n" +
                "Rethrow as InvalidOperationException: outer not thrown\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.Vendor:BuyItem() (at Assets/Scripts/Vendor.cs:34)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Type, Is.EqualTo("ArgumentException"));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Buy"));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(26));
            Assert.That(exceptions[1].Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exceptions[1].Value, Is.EqualTo("outer not thrown"));
            Assert.That(exceptions[1].Frames, Is.Empty);
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogExceptionCallbackShapeWithUnthrownInnerKeepsTheOuterFrames()
        {
            // The inner cause was created but never thrown, so it has no frames
            // of its own; the outer exception frames follow the Rethrow line.
            const string condition = "ArgumentException: inner not thrown";
            const string stackTrace =
                "Rethrow as InvalidOperationException: outer thrown, inner not thrown\n" +
                "Game.Inventory.Checkout () (at Assets/Scripts/Inventory.cs:40)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.Inventory:CheckoutClicked() (at Assets/Scripts/Inventory.cs:44)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Type, Is.EqualTo("ArgumentException"));
            Assert.That(exceptions[0].Value, Is.EqualTo("inner not thrown"));
            Assert.That(exceptions[0].Frames, Is.Empty);
            Assert.That(exceptions[1].Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].Function, Is.EqualTo("Checkout"));
            Assert.That(exceptions[1].Frames[0].LineNumber, Is.EqualTo(40));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void RethrowChainWithThreeExceptionsReturnsInnermostFirst()
        {
            // Application.logMessageReceivedThreaded, LogType.Exception:
            // level 1 wraps level 2, level 2 wraps level 3.
            const string condition = "NotSupportedException: level 3";
            const string stackTrace =
                "Game.Save.State.Save () (at Assets/Scripts/Save/State.cs:46)\n" +
                "Rethrow as InvalidOperationException: level 2\n" +
                "Game.Save.State.Save () (at Assets/Scripts/Save/State.cs:50)\n" +
                "Rethrow as ArgumentException: level 1\n" +
                "Game.Save.State.Save () (at Assets/Scripts/Save/State.cs:55)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.Save.State:Flush() (at Assets/Scripts/Save/State.cs:60)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(3));
            Assert.That(exceptions[0].Type, Is.EqualTo("NotSupportedException"));
            Assert.That(exceptions[0].Value, Is.EqualTo("level 3"));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(46));
            Assert.That(exceptions[1].Type, Is.EqualTo("InvalidOperationException"));
            Assert.That(exceptions[1].Value, Is.EqualTo("level 2"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].LineNumber, Is.EqualTo(50));
            Assert.That(exceptions[2].Type, Is.EqualTo("ArgumentException"));
            Assert.That(exceptions[2].Value, Is.EqualTo("level 1"));
            Assert.That(exceptions[2].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[2].Frames[0].LineNumber, Is.EqualTo(55));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogErrorCallbackShapeDropsTheLeadingWrapperAndKeepsCallers()
        {
            // Application.logMessageReceivedThreaded, LogType.Error:
            // Debug.LogError("no spawn point"); the wrapper is the innermost
            // frame and the callers follow it.
            const string stackTrace =
                "UnityEngine.Debug:LogError (object)\n" +
                "Game.GameManager.LogState () (at Assets/Scripts/GameManager.cs:30)\n" +
                "Game.UI.Hud:Update () (at Assets/Scripts/UI/Hud.cs:12)";

            var exceptions = UnityStackTraceParser.Parse("no spawn point", stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(1));

            var exception = exceptions[0];
            Assert.That(exception.Type, Is.EqualTo("Error"));
            Assert.That(exception.Value, Is.EqualTo("no spawn point"));
            Assert.That(exception.Frames, Has.Count.EqualTo(2));
            Assert.That(exception.Frames[0].Module, Is.EqualTo("Game.UI.Hud"));
            Assert.That(exception.Frames[0].Function, Is.EqualTo("Update"));
            Assert.That(exception.Frames[0].LineNumber, Is.EqualTo(12));
            Assert.That(exception.Frames[0].InApp, Is.True);
            Assert.That(exception.Frames[1].Module, Is.EqualTo("Game.GameManager"));
            Assert.That(exception.Frames[1].Function, Is.EqualTo("LogState"));
            Assert.That(exception.Frames[1].LineNumber, Is.EqualTo(30));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void LogErrorCallbackShapeDropsTheWholeHandlerWrapperChain()
        {
            // Player builds put the log handler chain in front of the caller.
            const string stackTrace =
                "UnityEngine.DebugLogHandler:Internal_Log(LogType, LogOption, String, Object)\n" +
                "UnityEngine.DebugLogHandler:LogFormat(LogType, Object, String, Object[])\n" +
                "UnityEngine.Logger:Log(LogType, Object)\n" +
                "UnityEngine.Debug:LogError (object)\n" +
                "Game.GameManager:Update () (at Assets/Scripts/GameManager.cs:12)";

            var exceptions = UnityStackTraceParser.Parse("NullReferenceException: boom", stackTrace);

            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Module, Is.EqualTo("Game.GameManager"));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Update"));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(12));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void WrapperAfterExceptionFramesIsDroppedWithTheFollowingLogCallFrames()
        {
            // A wrapper that is not the innermost frame ends the exception
            // frames: the wrapper and everything after it belong to the log call.
            const string stackTrace =
                "Game.Helper.Boom () (at Assets/Scripts/Helper.cs:5)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.Helper.Run () (at Assets/Scripts/Helper.cs:9)\n" +
                "Game.Helper.Start () (at Assets/Scripts/Helper.cs:14)";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: boom", stackTrace);

            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Boom"));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(5));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void InnerExceptionMarkerChainIsReturnedInnermostFirst()
        {
            // .NET Exception.ToString order: outer exception first, inner cause
            // below it.
            const string stackTrace =
                "Game.Outer.Throw () (at Assets/Game/Outer.cs:10)\n" +
                "   ---> System.InvalidOperationException: inner failure\n" +
                "Game.Inner.Throw () (at Assets/Game/Inner.cs:20)\n" +
                "   --- End of inner exception stack trace ---";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: outer failure", stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));

            Assert.That(exceptions[0].Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(exceptions[0].Value, Is.EqualTo("inner failure"));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Throw"));
            Assert.That(exceptions[0].Frames[0].FileName, Is.EqualTo("Assets/Game/Inner.cs"));

            Assert.That(exceptions[1].Type, Is.EqualTo("System.Exception"));
            Assert.That(exceptions[1].Value, Is.EqualTo("outer failure"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].FileName, Is.EqualTo("Assets/Game/Outer.cs"));
        }

        [Test]
        public void NestedInnerExceptionMarkersAreReturnedInnermostFirst()
        {
            const string stackTrace =
                "Game.Outer.Throw () (at Assets/Game/Outer.cs:10)\n" +
                "---> System.InvalidOperationException: middle failure\n" +
                "Game.Middle.Throw () (at Assets/Game/Middle.cs:20)\n" +
                "---> System.ArgumentException: inner failure\n" +
                "Game.Inner.Throw () (at Assets/Game/Inner.cs:30)\n" +
                "--- End of inner exception stack trace ---\n" +
                "--- End of inner exception stack trace ---";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: outer failure", stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(3));
            Assert.That(exceptions[0].Type, Is.EqualTo("System.ArgumentException"));
            Assert.That(exceptions[0].Value, Is.EqualTo("inner failure"));
            Assert.That(exceptions[1].Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(exceptions[1].Value, Is.EqualTo("middle failure"));
            Assert.That(exceptions[2].Type, Is.EqualTo("System.Exception"));
            Assert.That(exceptions[2].Value, Is.EqualTo("outer failure"));
        }

        [Test]
        public void OuterFramesAfterTheEndMarkerBelongToTheOuterException()
        {
            const string stackTrace =
                "---> System.InvalidOperationException: inner failure\n" +
                "Game.Inner.Throw () (at Assets/Game/Inner.cs:20)\n" +
                "--- End of inner exception stack trace ---\n" +
                "Game.Outer.Run () (at Assets/Game/Outer.cs:10)";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: outer failure", stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Throw"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].Function, Is.EqualTo("Run"));
            Assert.That(exceptions[1].Frames[0].FileName, Is.EqualTo("Assets/Game/Outer.cs"));
        }

        [Test]
        public void DotNetInnerChainEndsTheOuterSegmentAtTheLogWrapper()
        {
            const string condition = "System.Exception: outer failure";
            const string stackTrace =
                "Game.Loader.Load () (at Assets/Scripts/Loader.cs:10)\n" +
                "   ---> System.InvalidOperationException: inner failure\n" +
                "Game.Library.Read () (at Assets/Scripts/Library.cs:20)\n" +
                "   --- End of inner exception stack trace ---\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.Bootstrap:Awake() (at Assets/Scripts/Bootstrap.cs:40)";

            var exceptions = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(exceptions, Has.Count.EqualTo(2));
            Assert.That(exceptions[0].Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].FileName, Is.EqualTo("Assets/Scripts/Library.cs"));
            Assert.That(exceptions[1].Type, Is.EqualTo("System.Exception"));
            Assert.That(exceptions[1].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[1].Frames[0].FileName, Is.EqualTo("Assets/Scripts/Loader.cs"));
            Assert.That(exceptions[1].Frames[0].LineNumber, Is.EqualTo(10));
            AssertNoLogWrapperOrCallSiteFrames(exceptions);
        }

        [Test]
        public void ParsedFramesFollowTheInAppRules()
        {
            const string stackTrace =
                "Game.PlayerController.Update () (at Assets/Scripts/PlayerController.cs:12)\n" +
                "UnityEngine.EventSystems.EventSystem:Update () (at ./Library/PackageCache/com.unity.ugui/Runtime/EventSystem.cs:60)\n" +
                "System.IO.File:ReadAllText (System.String path)\n" +
                "UnityEditor.EditorApplication:Update ()\n" +
                "Packages/com.foo/Runtime/ThirdParty.Invoke () (at Packages/com.foo/Runtime/ThirdParty.cs:7)";

            var exceptions = UnityStackTraceParser.Parse("System.Exception: boom", stackTrace);

            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(5));
            Assert.That(exceptions[0].Frames[0].Module, Is.EqualTo("Packages/com.foo/Runtime/ThirdParty"));
            Assert.That(exceptions[0].Frames[0].InApp, Is.True);
            Assert.That(exceptions[0].Frames[1].Module, Is.EqualTo("UnityEditor.EditorApplication"));
            Assert.That(exceptions[0].Frames[1].InApp, Is.False);
            Assert.That(exceptions[0].Frames[2].Module, Is.EqualTo("System.IO.File"));
            Assert.That(exceptions[0].Frames[2].InApp, Is.False);
            Assert.That(exceptions[0].Frames[3].Module, Is.EqualTo("UnityEngine.EventSystems.EventSystem"));
            Assert.That(exceptions[0].Frames[3].InApp, Is.False);
            Assert.That(exceptions[0].Frames[4].Module, Is.EqualTo("Game.PlayerController"));
            Assert.That(exceptions[0].Frames[4].InApp, Is.True);
        }

        [Test]
        public void AssetsPathsAreInAppEvenForFrameworkModules()
        {
            Assert.That(UnityStackTraceParser.IsInApp("System.SomeHelper", "Assets/Scripts/Helper.cs"), Is.True);
            Assert.That(UnityStackTraceParser.IsInApp(null, "Assets/Scripts/Helper.cs"), Is.True);
            Assert.That(UnityStackTraceParser.IsInApp(string.Empty, "Assets/Scripts/Helper.cs"), Is.True);
        }

        [Test]
        public void PackageCachePathsAreNeverInApp()
        {
            Assert.That(UnityStackTraceParser.IsInApp("Game.PlayerController", "./Library/PackageCache/com.foo/Bar.cs"), Is.False);
            Assert.That(UnityStackTraceParser.IsInApp(null, "./Library/PackageCache/com.foo/Bar.cs"), Is.False);
            Assert.That(UnityStackTraceParser.IsInApp("System.IO.File", "./Library/PackageCache/com.foo/Bar.cs"), Is.False);
        }

        [TestCase("Game.PlayerController", null, true)]
        [TestCase("Newtonsoft.Json", null, true)]
        [TestCase("UnityEngine.Debug", null, false)]
        [TestCase("UnityEditor.EditorWindow", null, false)]
        [TestCase("Unity.SomeType", null, false)]
        [TestCase("System.IO.File", null, false)]
        [TestCase("Mono.Cecil", null, false)]
        [TestCase("GlitchPort.Core", null, false)]
        [TestCase("", null, false)]
        [TestCase(null, null, false)]
        [TestCase(null, "Packages/com.foo/Runtime/Bar.cs", false)]
        public void IsInAppFollowsTheDocumentedRules(string? module, string? fileName, bool expected)
        {
            Assert.That(UnityStackTraceParser.IsInApp(module, fileName), Is.EqualTo(expected));
        }

        [Test]
        public void SystemQualifiedConditionKeepsFullTypeName()
        {
            var exceptions = UnityStackTraceParser.Parse("System.InvalidOperationException: Cannot open save file", null);

            Assert.That(exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Type, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(exceptions[0].Value, Is.EqualTo("Cannot open save file"));
            Assert.That(exceptions[0].Frames, Is.Empty);
        }

        [Test]
        public void ConditionWithoutColonFallsBackToDefaultType()
        {
            var exceptions = UnityStackTraceParser.Parse("Boss phase two failed", null);

            Assert.That(exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Type, Is.EqualTo("Error"));
            Assert.That(exceptions[0].Value, Is.EqualTo("Boss phase two failed"));
        }

        [Test]
        public void ConditionWithoutColonFallsBackToExplicitDefaultType()
        {
            var exceptions = UnityStackTraceParser.Parse("Boss phase two failed", null, "UnityError");

            Assert.That(exceptions[0].Type, Is.EqualTo("UnityError"));
            Assert.That(exceptions[0].Value, Is.EqualTo("Boss phase two failed"));
        }

        [Test]
        public void MultiLineConditionKeepsExtraLinesInValue()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "System.Exception: save failed\n  retrying once\n  giving up",
                null);

            Assert.That(exceptions[0].Type, Is.EqualTo("System.Exception"));
            Assert.That(exceptions[0].Value, Is.EqualTo("save failed\n  retrying once\n  giving up"));
        }

        [Test]
        public void NullConditionAndStackTraceProduceTheDefaultException()
        {
            var exceptions = UnityStackTraceParser.Parse(null, null);

            Assert.That(exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Type, Is.EqualTo("Error"));
            Assert.That(exceptions[0].Value, Is.Null);
            Assert.That(exceptions[0].Frames, Is.Empty);
        }

        [Test]
        public void EmptyConditionAndStackTraceProduceTheDefaultException()
        {
            var exceptions = UnityStackTraceParser.Parse(string.Empty, string.Empty);

            Assert.That(exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Type, Is.EqualTo("Error"));
            Assert.That(exceptions[0].Value, Is.Empty);
            Assert.That(exceptions[0].Frames, Is.Empty);
        }

        [Test]
        public void BlankAndMarkerOnlyStackTraceLinesAreIgnored()
        {
            var exceptions = UnityStackTraceParser.Parse(
                "System.Exception: boom",
                "\n  \n--- End of inner exception stack trace ---\nGame.Update () (at Assets/Game.cs:9)\n");

            Assert.That(exceptions, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames, Has.Count.EqualTo(1));
            Assert.That(exceptions[0].Frames[0].Function, Is.EqualTo("Update"));
            Assert.That(exceptions[0].Frames[0].LineNumber, Is.EqualTo(9));
        }

        [Test]
        public void ParsingIsDeterministicAcrossRepeatedCalls()
        {
            const string condition = "ArgumentException: inventory is full";
            const string stackTrace =
                "Game.Inventory.Add () (at Assets/Scripts/Inventory.cs:52)\n" +
                "Rethrow as InvalidOperationException: pickup failed\n" +
                "Game.Player.PickUp () (at Assets/Scripts/Player.cs:92)\n" +
                "UnityEngine.Debug:LogException(Exception)\n" +
                "Game.GameLoop:Tick() (at Assets/Scripts/GameLoop.cs:44)";

            var first = UnityStackTraceParser.Parse(condition, stackTrace);
            var second = UnityStackTraceParser.Parse(condition, stackTrace);

            Assert.That(second, Has.Count.EqualTo(first.Count));
            for (var i = 0; i < first.Count; i++)
            {
                Assert.That(second[i].Type, Is.EqualTo(first[i].Type));
                Assert.That(second[i].Value, Is.EqualTo(first[i].Value));
                Assert.That(second[i].Frames, Has.Count.EqualTo(first[i].Frames.Count));
                for (var frameIndex = 0; frameIndex < first[i].Frames.Count; frameIndex++)
                {
                    Assert.That(second[i].Frames[frameIndex].Function, Is.EqualTo(first[i].Frames[frameIndex].Function));
                    Assert.That(second[i].Frames[frameIndex].Module, Is.EqualTo(first[i].Frames[frameIndex].Module));
                    Assert.That(second[i].Frames[frameIndex].LineNumber, Is.EqualTo(first[i].Frames[frameIndex].LineNumber));
                    Assert.That(second[i].Frames[frameIndex].InApp, Is.EqualTo(first[i].Frames[frameIndex].InApp));
                }
            }
        }

        private static void AssertNoLogWrapperOrCallSiteFrames(System.Collections.Generic.IList<ExceptionInfo> exceptions)
        {
            for (var i = 0; i < exceptions.Count; i++)
            {
                var frames = exceptions[i].Frames;
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    Assert.That(frame.Module, Is.Not.EqualTo("UnityEngine.Debug"));
                    Assert.That(frame.Module, Is.Not.EqualTo("UnityEngine.Logger"));
                    Assert.That(frame.Module, Is.Not.EqualTo("UnityEngine.DebugLogHandler"));
                }
            }
        }
    }
}
