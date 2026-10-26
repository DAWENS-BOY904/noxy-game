using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Noxy.IntegrationTests
{
    /// <summary>Source-level guards for rules that are easy to break by accident.</summary>
    [TestFixture]
    public class ArchitectureTests
    {
        private static string Scripts() =>
            Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "Assets", "_Project", "Scripts"));

        [Test]
        public void LookSensitivityIsAppliedInOnePlaceOnly()
        {
            // SensitivityModel.LookDelta may only be called by LookPipeline (Settings.cs). Anything else scaling look input again
            // would apply sensitivity twice.
            var offenders = Directory.GetFiles(Scripts(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(Path.Combine("Core", "Settings.cs")))
                .Where(f => File.ReadAllText(f).Contains("SensitivityModel.LookDelta("))
                .Select(f => f.Substring(Scripts().Length)).ToList();
            Assert.IsEmpty(offenders, "these files scale look input themselves instead of using LookPipeline: " + string.Join(", ", offenders));
        }

        [Test]
        public void ControllersFeedLookInputThroughThePipeline()
        {
            var controller = File.ReadAllText(Path.Combine(Scripts(), "Lobby", "Training", "PlayerController.cs"));
            StringAssert.Contains("lookPipeline.Process(", controller);
        }

        [Test]
        public void NoOnlineGameplayClientEverSendsADamageValue()
        {
            // the Battle Royale client may only send a direction; "damage", "kill" and "target" must never appear in a request body
            var src = File.ReadAllText(Path.Combine(Scripts(), "Core", "BattleRoyale.cs"));
            int fire = src.IndexOf("public void Fire(float dx", StringComparison.Ordinal);
            Assert.Greater(fire, 0);
            string call = src.Substring(fire, 400);
            StringAssert.DoesNotContain("damage", call.ToLowerInvariant());
            StringAssert.DoesNotContain("\"kill\"", call);
            StringAssert.DoesNotContain("\"target\"", call);
        }
    }
}
