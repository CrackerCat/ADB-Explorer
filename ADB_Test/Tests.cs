using ADB_Explorer.Converters;
using ADB_Explorer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace ADB_Test
{
    [TestClass]
    public class Tests
    {
        [TestMethod]
        public void ToSizeTest()
        {
            var testVals = new Dictionary<ulong, string>()
            {
                { 0, "0B" },
                { 300, "300B" },
                { 33000, "32.2KB" }, // 32.226
                { 500690, "489KB" }, // 488.955
                { 1024204, "1MB" }, // 1.0002
                { 1200100, "1.1MB" }, // 1.145
                { 3400200100, "3.2GB" }, // 1.667
                { 1200300400500, "1.1TB" } // 1.092
            };

            foreach (var item in testVals)
            {
                Assert.IsTrue(item.Key.ToSize() == item.Value);
            }
        }

        [TestMethod]
        public void GeneratePathTest()
        {
            string[] paths = { "/mnt/sdcard/Download", "/dev/block" };

            var path0 = CrumbPath.GeneratePath(paths[0], AdbExplorerConst.SPECIAL_FOLDERS_PRETTY_NAMES);

            Assert.AreEqual("Internal Storage", path0[0].DisplayName);
            Assert.AreEqual("Download", path0[1].DisplayName);
            Assert.AreEqual(paths[0], path0[1].Path);

            var path1 = CrumbPath.GeneratePath(paths[1]);

            Assert.AreEqual("Root", path1[0].DisplayName);
            Assert.AreEqual("/", path1[0].Path);
            Assert.AreEqual("/dev", path1[1].Path);
        }
    }
}
