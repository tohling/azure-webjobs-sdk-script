// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;

namespace Microsoft.Azure.WebJobs.Script.Settings
{
    public sealed class ScriptSettingsManager : ISettingsManager
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

        public void Clear()
        {
            _settings.Clear();
        }

        public string GetEnvironmentSetting(string environmentSettingKey)
        {
            string settingValue = null;

            if (_settings.ContainsKey(environmentSettingKey))
            {
                settingValue = _settings[environmentSettingKey];
            }
            else
            {
                settingValue = Environment.GetEnvironmentVariable(environmentSettingKey);
                if (!string.IsNullOrEmpty(settingValue))
                {
                    _settings.TryAdd(environmentSettingKey, settingValue);
                }
            }

            return settingValue;
        }

        public void SetEnvironmentSetting(string environmentSettingKey, string environmentSettingValue)
        {
            Environment.SetEnvironmentVariable(environmentSettingKey, environmentSettingValue);
            if (_settings.ContainsKey(environmentSettingKey))
            {
                _settings[environmentSettingKey] = environmentSettingValue;
            }
            else
            {
                _settings.TryAdd(environmentSettingKey, environmentSettingValue);
            }
        }
    }
}