﻿namespace Redpoint.Uet.SdkManagement.Tests
{
    using Redpoint.Uet.SdkManagement;
    using System.Runtime.Versioning;

    public class AndroidEnvironmentSetupTests
    {
        private const string _androidCodeFragment = @"
namespace TestNamespace
{
	partial class TestClass
	{
		public override string GetPlatformSpecificVersion(string VersionType)
		{
			switch (VersionType.ToLower())
			{
				case ""platforms"": return ""android-32"";
				case ""build-tools"": return ""30.0.3"";
				case ""cmake"": return ""3.10.2.4988404"";
				case ""ndk"": return ""25.1.8937393"";
			}

			return """";
		}
	}
}
";

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task CanParseVersions()
        {
            Assert.Equal("android-32", await AndroidManualSdkSetup.ParseVersion(_androidCodeFragment, "platforms"));
            Assert.Equal("30.0.3", await AndroidManualSdkSetup.ParseVersion(_androidCodeFragment, "build-tools"));
            Assert.Equal("3.10.2.4988404", await AndroidManualSdkSetup.ParseVersion(_androidCodeFragment, "cmake"));
            Assert.Equal("25.1.8937393", await AndroidManualSdkSetup.ParseVersion(_androidCodeFragment, "ndk"));
        }
    }
}