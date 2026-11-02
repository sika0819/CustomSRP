using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 沿海岸海浪连贯性：高度门控不得在贴岸带 SoftStop；远内陆爬高仍掐。
    /// </summary>
    public sealed class OceanShoreAmbienceTests
    {
        [Test]
        public void HeightGate_NearShoreCliff_StaysAudible()
        {
            var cliff = new Vector3(600f, AmbienceZoneCatalog.OceanMaxPlayerY + 10f, 700f);
            Assert.IsFalse(
                AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(cliff, 12f),
                "贴岸悬崖不应因绝对高度掐海浪");
        }

        [Test]
        public void HeightGate_InlandHigh_Mutes()
        {
            var hill = new Vector3(500f, AmbienceZoneCatalog.OceanMaxPlayerY + 5f, 500f);
            Assert.IsTrue(
                AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(
                    hill, AmbienceZoneCatalog.OceanHeightGateMinShellMeters + 1f),
                "离岸较远的内陆爬高应掐海浪");
        }

        [Test]
        public void HeightGate_LowPlayer_NeverMutes()
        {
            var beach = new Vector3(600f, AmbienceZoneCatalog.OceanSurfaceY + 2f, 700f);
            Assert.IsFalse(
                AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(beach, 50f));
        }

        [Test]
        public void HeightGate_BoundaryShell_StaysAudibleWhenHigh()
        {
            // shell == MinShell → 仍算贴岸，不掐。
            var cliff = new Vector3(600f, AmbienceZoneCatalog.OceanMaxPlayerY + 20f, 700f);
            Assert.IsFalse(
                AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(
                    cliff, AmbienceZoneCatalog.OceanHeightGateMinShellMeters));
        }

        [Test]
        public void HeightGate_JustInsideMaxY_NeverMutes()
        {
            var shore = new Vector3(600f, AmbienceZoneCatalog.OceanMaxPlayerY, 700f);
            Assert.IsFalse(
                AmbienceZoneCatalog.IsOceanMutedByPlayerHeight(
                    shore, AmbienceZoneCatalog.OceanHeightGateMinShellMeters + 10f));
        }

        [Test]
        public void ShoreStickBias_IsPositive()
        {
            Assert.Greater(AmbienceZoneCatalog.OceanShoreStickBiasMeters, 0f);
            Assert.Greater(AmbienceZoneCatalog.NearSurfaceMeters, 0f);
        }
    }
}
