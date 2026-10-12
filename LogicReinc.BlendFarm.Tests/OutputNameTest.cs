using LogicReinc.BlendFarm.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Linq;

namespace LogicReinc.BlendFarm.Tests
{
    /// <summary>
    /// Tests for output-filename templating (OutputNaming).
    /// </summary>
    [TestClass]
    public class OutputNameTest
    {
        [TestMethod]
        public void HashBehaviorUnchanged()
        {
            // Existing contract: a run of '#' is replaced with the zero-padded frame number.
            Assert.AreEqual("007.png", OutputNaming.ApplyFrameHashes("###.png", 7, 0, 250));
            // A single '#' still pads to a minimum width of 3 (matches legacy behaviour).
            Assert.AreEqual("001.png", OutputNaming.ApplyFrameHashes("#.png", 1, 1, 1));
            // No '#' present: format returned unchanged.
            Assert.AreEqual("still.png", OutputNaming.ApplyFrameHashes("still.png", 5, 1, 10));
        }

        [TestMethod]
        public void TokensExpand()
        {
            OutputNameContext ctx = new OutputNameContext
            {
                Scene = "Shot",
                Samples = 128,
                Engine = "Cycles",
                Width = 1920,
                Height = 1080,
                Frame = 12,
                StartFrame = 1,
                EndFrame = 100
            };
            Assert.AreEqual("Shot_c128_1920x1080_012.png",
                OutputNaming.FormatOutputFileName("{scene}_c{samples}_{width}x{height}_###.png", ctx));
        }

        [TestMethod]
        public void InvalidCharsInTokenValuesSanitized()
        {
            OutputNameContext ctx = new OutputNameContext
            {
                Scene = "a/b:c",
                Frame = 1,
                StartFrame = 1,
                EndFrame = 1
            };
            string name = OutputNaming.FormatOutputFileName("{scene}_#.png", ctx);
            Assert.IsFalse(Path.GetInvalidFileNameChars().Any(c => name.Contains(c)));
        }

        [TestMethod]
        public void TokensAreCaseInsensitive()
        {
            OutputNameContext ctx = new OutputNameContext
            {
                Scene = "X",
                Frame = 1,
                StartFrame = 1,
                EndFrame = 1
            };
            Assert.AreEqual("X_001.png", OutputNaming.FormatOutputFileName("{SCENE}_#.png", ctx));
        }

        [TestMethod]
        public void ValidationRejectsInvalidChars()
        {
            Assert.IsTrue(OutputNaming.TryValidateTemplate("{scene}_###.png", out _));
            Assert.IsFalse(OutputNaming.TryValidateTemplate("bad:name_#.png", out string error));
            Assert.IsNotNull(error);
        }
    }
}
