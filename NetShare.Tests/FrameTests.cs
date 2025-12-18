using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetShare.Core.Protocol;

namespace NetShare.Tests
{
    [TestClass]
    public class FrameTests
    {
        [TestMethod]
        public void Frame_RoundTrips_Json()
        {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var frame = new Frame(FrameKind.Json, payload);

            using (var ms = new MemoryStream())
            {
                var w = new FrameWriter(ms);
                w.WriteFrame(frame);
                ms.Position = 0;

                var r = new FrameReader(ms);
                var read = r.ReadFrame();

                Assert.IsNotNull(read);
                Assert.AreEqual(FrameKind.Json, read.Kind);
                CollectionAssert.AreEqual(payload, read.Payload);
            }
        }
    }
}
