using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class EconomyTests
    {
        [Test]
        public void PromoDisclaimerSaysNoCashValue()
        {
            StringAssert.Contains("cash", EconomyText.PromoDisclaimer.ToLowerInvariant());
        }

        [Test]
        public void StatusLabelIsNeverEmpty()
        {
            foreach (var s in new[] { "pending", "succeeded", "cancelled", "failed", "weird" })
                Assert.IsNotEmpty(EconomyText.StatusLabel(s));
        }

        [Test]
        public void FriendlyErrorFallsBackWhenCodeUnknown()
        {
            Assert.AreEqual("fallback", EconomyText.FriendlyPaymentError("zzz_unknown", "fallback"));
        }

        [Test]
        public void OfflineEconomyIsUnavailable()
        {
            var e = new OfflineEconomyService();
            Assert.IsFalse(e.IsAvailable);
            Assert.IsNotEmpty(e.UnavailableReason);
        }
    }
}
