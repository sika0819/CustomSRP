using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    public sealed class SceneBankGateTests
    {
        [Test]
        public void ExpandZoneBanks_AddsPresetPackageThatReferencesZone()
        {
            var forest = ScriptableObject.CreateInstance<IslandPresetAsset>();
            forest.BankNames = new[] { "Music5_3", "Boluodao_Forest" };
            var beach = ScriptableObject.CreateInstance<IslandPresetAsset>();
            beach.BankNames = new[] { "Music4_4", "Ocean_Calm_Quad" };
            var sharedMusic = ScriptableObject.CreateInstance<IslandPresetAsset>();
            sharedMusic.BankNames = new[] { "Music5_3", "Lake_Quad" };

            try
            {
                string[] banks = SceneBankGateSpawner.ExpandZoneBanks(
                    new[] { "Boluodao_Forest" },
                    new[] { forest, beach, sharedMusic });

                CollectionAssert.AreEquivalent(
                    new[] { "Boluodao_Forest", "Music5_3" },
                    banks);
            }
            finally
            {
                Object.DestroyImmediate(forest);
                Object.DestroyImmediate(beach);
                Object.DestroyImmediate(sharedMusic);
            }
        }

        [Test]
        public void ExpandZoneBanks_EmptyZone_StaysEmpty()
        {
            string[] banks = SceneBankGateSpawner.ExpandZoneBanks(null, null);
            Assert.AreEqual(0, banks.Length);
        }

        [Test]
        public void HorizontalExitPush_Box_LeavesInflatedTrigger()
        {
            SceneBankGate gate = CreateGate(
                SceneBankGateShape.Box,
                new Vector3(4f, 4f, 2f),
                0f);
            try
            {
                Vector3 eye = new Vector3(0f, 2f, 3f);
                Vector3 foot = new Vector3(0f, 0.2f, 3f);
                const float slop = 0.75f;
                Assert.IsTrue(gate.ContainsBody(eye, foot, slop));

                Vector3 push = gate.HorizontalExitPush(eye, foot, slop);
                Assert.Greater(push.z, 0f);
                Assert.AreEqual(0f, push.y, 0.001f);

                Assert.IsFalse(gate.ContainsBody(eye + push, foot + push, slop));
            }
            finally
            {
                Object.DestroyImmediate(gate.gameObject);
            }
        }

        [Test]
        public void HorizontalExitPush_Outside_IsZero()
        {
            SceneBankGate gate = CreateGate(
                SceneBankGateShape.Sphere,
                Vector3.one,
                8f);
            try
            {
                Vector3 eye = new Vector3(0f, 2f, 40f);
                Vector3 foot = new Vector3(0f, 0.2f, 40f);
                Assert.IsFalse(gate.ContainsBody(eye, foot, 0.75f));
                Assert.AreEqual(Vector3.zero, gate.HorizontalExitPush(eye, foot, 0.75f));
            }
            finally
            {
                Object.DestroyImmediate(gate.gameObject);
            }
        }

        [Test]
        public void SetSampledInside_KeepsLatchUntilAsked()
        {
            SceneBankGate gate = CreateGate(
                SceneBankGateShape.Box,
                new Vector3(4f, 4f, 2f),
                0f);
            try
            {
                gate.PromptLatched = true;
                gate.SetSampledInside(true);
                gate.SetSampledInside(false, clearLatch: false);
                Assert.IsFalse(gate.PlayerInside);
                Assert.IsTrue(gate.PromptLatched);

                gate.SetSampledInside(false, clearLatch: true);
                Assert.IsFalse(gate.PromptLatched);
            }
            finally
            {
                Object.DestroyImmediate(gate.gameObject);
            }
        }

        static SceneBankGate CreateGate(SceneBankGateShape shape, Vector3 boxSize, float radius)
        {
            var go = new GameObject("gate-test");
            var gate = go.AddComponent<SceneBankGate>();
            gate.Setup(1, "test", new[] { "Bank" }, boxSize, radius, shape, blocked: true);
            return gate;
        }
    }
}
