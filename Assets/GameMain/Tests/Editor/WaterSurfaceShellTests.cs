using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 沿河 shell：离散样点不得让水平距离阶跃（否则增益忽大忽小）。
    /// </summary>
    public sealed class WaterSurfaceShellTests
    {
        [Test]
        public void MidpointBetweenSparseSamples_ProjectsToLateralDistance()
        {
            // 样点相距 20m；听者贴在中点外侧 3m。纯最近点会量到 ~10.4m，
            // 线段投影应回到约 3m（近场满音）。
            var flat = new[]
            {
                new Vector3(0f, 10f, 0f),
                new Vector3(20f, 10f, 0f),
            };
            var listener = new Vector3(10f, 12f, 3f);
            int sticky = -1;

            Assert.IsTrue(
                WaterSurfaceProbe.ResolveShell(flat, listener, ref sticky, out _, out float dist));
            Assert.That(dist, Is.EqualTo(3f).Within(0.05f));
        }

        [Test]
        public void WalkAlongRibbon_ShellStaysNearConstant()
        {
            var flat = new[]
            {
                new Vector3(0f, 10f, 0f),
                new Vector3(16f, 10f, 0f),
                new Vector3(32f, 10f, 0f),
                new Vector3(48f, 10f, 0f),
            };
            const float lateral = 4f;
            int sticky = -1;
            float prev = float.NaN;

            for (float x = 2f; x <= 46f; x += 2f)
            {
                var listener = new Vector3(x, 12f, lateral);
                Assert.IsTrue(
                    WaterSurfaceProbe.ResolveShell(
                        flat, listener, ref sticky, out _, out float dist));
                Assert.That(dist, Is.EqualTo(lateral).Within(0.15f),
                    $"x={x} shell should stay ~lateral");
                if (!float.IsNaN(prev))
                    Assert.That(Mathf.Abs(dist - prev), Is.LessThan(0.2f));
                prev = dist;
            }
        }

        [Test]
        public void Sticky_HoldsAcrossVoronoiBoundary()
        {
            var flat = new[]
            {
                new Vector3(0f, 10f, 0f),
                new Vector3(10f, 10f, 0f),
            };
            int sticky = 0;
            // 略靠近点 1，但仍在 sticky bias 内 → 保持点 0。
            var listener = new Vector3(5.5f, 12f, 1f);
            Assert.IsTrue(
                WaterSurfaceProbe.ResolveShell(flat, listener, ref sticky, out _, out _));
            Assert.AreEqual(0, sticky);
        }

        [Test]
        public void EmptyFlat_FailsAndClearsSticky()
        {
            int sticky = 3;
            Assert.IsFalse(
                WaterSurfaceProbe.ResolveShell(
                    System.Array.Empty<Vector3>(), Vector3.zero, ref sticky, out _, out _));
            Assert.AreEqual(-1, sticky);
        }
    }
}
