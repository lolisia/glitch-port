using NUnit.Framework;
using UnityEngine;

namespace GlitchPort.Unity.Tests.Runtime
{
    /// <summary>
    /// Verifies that the package's PlayMode test assembly runs in the player loop.
    /// </summary>
    public sealed class PackageSmokeTests
    {
        [Test]
        public void PlayModeTestRunsWhilePlaying()
        {
            Assert.That(Application.isPlaying, Is.True, "PlayMode tests must run while Application.isPlaying is true.");
        }
    }
}
