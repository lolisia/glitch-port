using System.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace GlitchPort.Unity.Tests.Editor
{
    /// <summary>
    /// Verifies that the package was imported by the Unity Package Manager.
    /// </summary>
    public sealed class PackageSmokeTests
    {
        private const string PackageName = "com.glitchport.unity";
        private const string ExpectedVersion = "0.1.0";

        [Test]
        public void PackageIsRegisteredWithExpectedVersion()
        {
            PackageInfo package = PackageInfo.GetAllRegisteredPackages()
                .SingleOrDefault(candidate => candidate.name == PackageName);

            Assert.That(package, Is.Not.Null, PackageName + " is not registered in this project.");
            Assert.That(package.version, Is.EqualTo(ExpectedVersion));
        }
    }
}
