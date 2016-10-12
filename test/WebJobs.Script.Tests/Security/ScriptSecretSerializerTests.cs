// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Security
{
    public class ScriptSecretSerializerTests
    {
        private ScriptSettingsManager _settingsManager = ScriptSettingsManager.Instance;

        [Fact]
        public void DefaultSerializer_WhenMultiKeyFeatureIsEnabled_ReturnsV1Serializer()
        {
            using (var variables = new TestScopedEnvironmentVariables(_settingsManager, "AzureWebJobsEnableMultiKey", "true"))
            {
                Assert.Equal(typeof(ScriptSecretSerializerV1), ScriptSecretSerializer.DefaultSerializer?.GetType());
            }
        }

        [Fact]
        public void DefaultSerializer_WhenMultiKeyFeatureIsDisabled_ReturnsV0Serializer()
        {
            using (var variables = new TestScopedEnvironmentVariables(_settingsManager, "AzureWebJobsEnableMultiKey", "false"))
            {
                Assert.Equal(typeof(ScriptSecretSerializerV0), ScriptSecretSerializer.DefaultSerializer?.GetType());
            }
        }

        [Fact]
        public void DefaultSerializer_WhenMultiKeyFeatureIsNotSet_ReturnsV0Serializer()
        {
            Assert.Equal(typeof(ScriptSecretSerializerV0), ScriptSecretSerializer.DefaultSerializer?.GetType());
        }
    }
}
