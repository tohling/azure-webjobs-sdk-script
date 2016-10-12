// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Azure.WebJobs.Script.Settings;
using Microsoft.CodeAnalysis;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    [CLSCompliant(false)]
    public sealed class DotNetCompilationServiceFactory : ICompilationServiceFactory
    {
        private static readonly ImmutableArray<ScriptType> SupportedScriptTypes = new[] { ScriptType.CSharp, ScriptType.FSharp }.ToImmutableArray();

        private static OptimizationLevel? _optimizationLevel;

        ImmutableArray<ScriptType> ICompilationServiceFactory.SupportedScriptTypes
        {
            get
            {
                return SupportedScriptTypes;
            }
        }

        private static OptimizationLevel OptimizationLevel
        {
            get
            {
                if (_optimizationLevel == null)
                {
                    string releaseModeSetting = ScriptSettingsManager.Instance.GetEnvironmentSetting(DotNetConstants.CompilationReleaseMode);

                    _optimizationLevel = string.Equals(releaseModeSetting, bool.TrueString, StringComparison.OrdinalIgnoreCase)
                        ? OptimizationLevel.Release
                        : OptimizationLevel.Debug;
                }

                return _optimizationLevel.Value;
            }
        }

        public ICompilationService CreateService(ScriptType scriptType, IFunctionMetadataResolver metadataResolver)
        {
            switch (scriptType)
            {
                case ScriptType.CSharp:
                    return new CSharpCompilationService(metadataResolver, OptimizationLevel);
                case ScriptType.FSharp:
                    return new FSharpCompiler(metadataResolver, OptimizationLevel);
                default:
                    throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, 
                        "The script type {0} is not supported by the {1}", scriptType, typeof(DotNetCompilationServiceFactory).Name));
            }
        }
    }
}
