using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetShare.Core.Sharing;

namespace NetShare.Tests
{
    [TestClass]
    public class ShareManagerTests
    {
        [TestMethod]
        public void AddShare_WithExplicitId_PreservesId()
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetShareTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var mgr = new ShareManager();
                var id = Guid.NewGuid().ToString();

                var s = mgr.AddShare(dir, readOnly: true, shareId: id, name: "TestShare");

                Assert.AreEqual(id, s.ShareId);
                Assert.AreEqual("TestShare", s.Name);
                Assert.IsTrue(s.ReadOnly);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void AddShare_SamePath_DedupesAndKeepsOriginalId()
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetShareTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var mgr = new ShareManager();
                var first = mgr.AddShare(dir, readOnly: false, shareId: Guid.NewGuid().ToString(), name: "First");

                var second = mgr.AddShare(dir, readOnly: true, shareId: Guid.NewGuid().ToString(), name: "Second");

                Assert.AreEqual(first.ShareId, second.ShareId, "Same local path should not create a second share.");
                Assert.IsTrue(second.ReadOnly, "Second add should update read-only flag.");
                Assert.AreEqual("Second", second.Name, "Second add should update name.");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void AddShare_SameId_UpdatesExisting()
        {
            var dir1 = Path.Combine(Path.GetTempPath(), "NetShareTests_" + Guid.NewGuid().ToString("N"));
            var dir2 = Path.Combine(Path.GetTempPath(), "NetShareTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir1);
            Directory.CreateDirectory(dir2);
            try
            {
                var mgr = new ShareManager();
                var id = Guid.NewGuid().ToString();

                var a = mgr.AddShare(dir1, readOnly: false, shareId: id, name: "A");
                var b = mgr.AddShare(dir2, readOnly: true, shareId: id, name: "B");

                Assert.AreEqual(id, a.ShareId);
                Assert.AreEqual(id, b.ShareId);
                Assert.AreEqual(Path.GetFullPath(dir2), b.LocalPath);
                Assert.IsTrue(b.ReadOnly);
                Assert.AreEqual("B", b.Name);
            }
            finally
            {
                try { Directory.Delete(dir1, recursive: true); } catch { }
                try { Directory.Delete(dir2, recursive: true); } catch { }
            }
        }
    }
}
