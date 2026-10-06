using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;

namespace ScheduledRestart.Tests
{
    [TestClass]
    public class AppConstantsTests
    {
        [TestMethod]
        public void WebsiteUrl_IsExactConstant()
        {
            Assert.AreEqual("https://ib-fix.com", AppConstants.WebsiteUrl);
        }
    }
}
