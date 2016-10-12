﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs.Script.Settings;

namespace Microsoft.Azure.WebJobs.Script
{
    public interface IScriptHostFactory
    {
        ScriptHost Create(ISettingsManager settingsManager, ScriptHostConfiguration config);
    }
}
