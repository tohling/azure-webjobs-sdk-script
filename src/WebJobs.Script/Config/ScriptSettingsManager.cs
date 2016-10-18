// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;

namespace Microsoft.Azure.WebJobs.Script.Config
{
    public sealed class ScriptSettingsManager
    {
        private static readonly ScriptSettingsManager _instance = new ScriptSettingsManager();

        private static ConcurrentDictionary<string, string> _settings;

        public static ScriptSettingsManager Instance
        {
            get { return _instance; }
        }

        private ScriptSettingsManager()
        {
            _settings = new ConcurrentDictionary<string, string>();
        }

        public void Reset()
        {
            _settings.Clear();
        }

        public string GetSetting(string settingKey)
        {
            string envSettingValue = Environment.GetEnvironmentVariable(settingKey);
            string settingValue = _settings.GetOrAdd(settingKey, envSettingValue);
            return settingValue;
        }

        public void SetSetting(string settingKey, string settingValue)
        {
            _settings.AddOrUpdate(settingKey, settingValue, (key, existingValue) => settingValue);
        }
    }
}