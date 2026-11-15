using System;
using System.Collections.Generic;
using Xunit;

namespace GlitchPort.Tests.Scopes
{
    public sealed class ScopeTests
    {
        [Fact]
        public void NewScope_StartsEmpty()
        {
            var scope = new Scope();

            Assert.Empty(scope.Tags);
            Assert.Null(scope.User);
            Assert.Empty(scope.Contexts);
            Assert.Empty(scope.Breadcrumbs);
            Assert.Empty(scope.Fingerprint);
            Assert.Null(scope.Level);
        }

        [Fact]
        public void SetTag_ReplacesTheValueForTheSameKey()
        {
            var scope = new Scope();

            scope.SetTag("scene", "Stage1");
            scope.SetTag("scene", "Stage2");

            Assert.Equal("Stage2", Assert.Single(scope.Tags).Value);
            Assert.Equal("Stage2", scope.Tags["scene"]);
        }

        [Fact]
        public void SetTag_WithNullKey_ThrowsArgumentNullException()
        {
            var scope = new Scope();

            Assert.Throws<ArgumentNullException>(() => scope.SetTag(null!, "value"));
        }

        [Fact]
        public void SetUser_ReplacesThePreviousUserAndAllowsClearing()
        {
            var scope = new Scope();
            var user = new UserInfo { Id = "player-1" };

            scope.SetUser(user);
            Assert.Same(user, scope.User);

            scope.SetUser(null);
            Assert.Null(scope.User);
        }

        [Fact]
        public void SetContext_StoresTheNamespace()
        {
            var scope = new Scope();
            var context = new Dictionary<string, object?> { ["scene"] = "Stage1" };

            scope.SetContext("game", context);

            Assert.Same(context, scope.Contexts["game"]);
        }

        [Fact]
        public void SetContext_WithNullArguments_ThrowsArgumentNullException()
        {
            var scope = new Scope();

            Assert.Throws<ArgumentNullException>(() => scope.SetContext(null!, new Dictionary<string, object?>()));
            Assert.Throws<ArgumentNullException>(() => scope.SetContext("game", null!));
        }

        [Fact]
        public void AddBreadcrumb_AppendsInChronologicalOrder()
        {
            var scope = new Scope();
            var first = new Breadcrumb { Message = "first" };
            var second = new Breadcrumb { Message = "second" };

            scope.AddBreadcrumb(first);
            scope.AddBreadcrumb(second);

            Assert.Equal(new[] { first, second }, scope.Breadcrumbs);
        }

        [Fact]
        public void AddBreadcrumb_WithNull_ThrowsArgumentNullException()
        {
            var scope = new Scope();

            Assert.Throws<ArgumentNullException>(() => scope.AddBreadcrumb(null!));
        }

        [Fact]
        public void LevelAndFingerprint_AreMutable()
        {
            var scope = new Scope
            {
                Level = Level.Warning,
            };
            scope.Fingerprint.Add("custom");

            Assert.Equal(Level.Warning, scope.Level);
            Assert.Equal("custom", Assert.Single(scope.Fingerprint));
        }
    }
}
