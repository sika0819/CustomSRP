using NUnit.Framework;

namespace Game.Tests
{
    public sealed class AmbienceProximityBankPolicyTests
    {
        [Test]
        public void PriorityKinds_RiverWaterfallCampfire()
        {
            Assert.IsTrue(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.River));
            Assert.IsTrue(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.Waterfall));
            Assert.IsTrue(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.Campfire));
            Assert.IsFalse(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.Ocean));
            Assert.IsFalse(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.Lake));
            Assert.IsFalse(AmbienceProximityBankPolicy.IsPriorityKind(AmbienceEmitterKind.Forest));
        }

        [Test]
        public void Resolve_Loaded_IsNone()
        {
            Assert.AreEqual(
                AmbienceProximityBankPolicy.Action.None,
                AmbienceProximityBankPolicy.Resolve(
                    isPriorityKind: true, engineLoaded: true, canResolveOrLocalReady: false));
        }

        [Test]
        public void Resolve_LocalReady_Loads()
        {
            Assert.AreEqual(
                AmbienceProximityBankPolicy.Action.LoadLocal,
                AmbienceProximityBankPolicy.Resolve(
                    isPriorityKind: true, engineLoaded: false, canResolveOrLocalReady: true));
        }

        [Test]
        public void Resolve_Missing_Downloads()
        {
            Assert.AreEqual(
                AmbienceProximityBankPolicy.Action.DownloadAndLoad,
                AmbienceProximityBankPolicy.Resolve(
                    isPriorityKind: true, engineLoaded: false, canResolveOrLocalReady: false));
        }

        [Test]
        public void Resolve_NonPriority_IsNone()
        {
            Assert.AreEqual(
                AmbienceProximityBankPolicy.Action.None,
                AmbienceProximityBankPolicy.Resolve(
                    isPriorityKind: false, engineLoaded: false, canResolveOrLocalReady: false));
        }
    }
}
