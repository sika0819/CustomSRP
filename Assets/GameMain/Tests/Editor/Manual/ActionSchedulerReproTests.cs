using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Game.Tests.Editor
{
    /// <summary>
    /// 手动复现：GetInstance + StartScheduler 让一条线程停在 SchedulerThreadFunc。
    /// Explicit，不进默认套件。第二次 StartScheduler 会 std::terminate，跑完必须 Stop。
    /// </summary>
    [Explicit("手动复现 ActionScheduler 线程。不要进默认测试。")]
    public sealed class ActionSchedulerReproTests
    {
        const string DllName = "WIAEngine";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "_ZN5WANOS4TASK15ActionScheduler11GetInstanceEv")]
        static extern IntPtr GetInstance();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "_ZN5WANOS4TASK15ActionScheduler14StartSchedulerEv")]
        static extern void StartScheduler(IntPtr self);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "_ZN5WANOS4TASK15ActionScheduler13StopSchedulerEv")]
        static extern void StopScheduler(IntPtr self);

        [Test]
        public void StartOnce_ThenStop()
        {
            IntPtr self = GetInstance();
            Assert.AreNotEqual(IntPtr.Zero, self);
            try
            {
                StartScheduler(self);
            }
            finally
            {
                StopScheduler(self);
            }
        }
    }
}
