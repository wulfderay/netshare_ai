using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetShare.Core.Sharing;

namespace NetShare.Tests
{
    [TestClass]
    public class SafePathTests
    {
        [TestMethod]
        public void CombineAndValidate_Allows_Normal_Relative()
        {
            var root = "C:\\ShareRoot";
            var full = SafePath.CombineAndValidate(root, "folder/file.txt");
            StringAssert.Contains(full.ToLowerInvariant(), "shareroot");
        }

        [TestMethod]
        public void CombineAndValidate_Allows_Empty_Relative_For_ShareRoot()
        {
            var root = "C:\\ShareRoot";
            var full = SafePath.CombineAndValidate(root, "");
            Assert.AreEqual(System.IO.Path.GetFullPath(root), full);
        }

        [TestMethod]
        [ExpectedException(typeof(System.InvalidOperationException))]
        public void CombineAndValidate_Blocks_Traversal()
        {
            SafePath.CombineAndValidate("C:\\ShareRoot", "..\\Windows\\system.ini");
        }
    }
}
