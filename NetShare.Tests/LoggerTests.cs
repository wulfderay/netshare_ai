using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetShare.Core.Logging;

namespace NetShare.Tests
{
    [TestClass]
    public class LoggerTests
    {
        [TestMethod]
        public void Logger_RingBuffer_Keeps_Last_N_Entries_In_Order()
        {
            Logger.Clear();

            var total = Logger.DefaultRingBufferCapacity + 100;
            for (int i = 0; i < total; i++)
            {
                Logger.Info("Test", "m" + i);
            }

            var snap = Logger.Snapshot();
            Assert.AreEqual(Logger.DefaultRingBufferCapacity, snap.Count);

            // Oldest entry should be m100
            Assert.AreEqual("m100", snap[0].Message);
            Assert.AreEqual("m" + (total - 1), snap[snap.Count - 1].Message);

            for (int i = 1; i < snap.Count; i++)
            {
                Assert.IsTrue(snap[i].Sequence > snap[i - 1].Sequence);
            }
        }

        [TestMethod]
        public void Logger_Clear_Removes_All_Entries()
        {
            Logger.Info("Test", "hello");
            Logger.Clear();
            var snap = Logger.Snapshot();
            Assert.AreEqual(0, snap.Count);
        }
    }
}
