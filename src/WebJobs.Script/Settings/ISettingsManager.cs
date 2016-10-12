// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Microsoft.Azure.WebJobs.Script.Settings
{
    public interface ISettingsManager
    {
        void Clear();
        string GetEnvironmentSetting(string environmentSettingKey);
        void SetEnvironmentSetting(string environmentSettingKey, string environmentSettingValue);
    }
}