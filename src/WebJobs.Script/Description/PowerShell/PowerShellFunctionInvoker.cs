// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings.Runtime;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description.PowerShell;
using Microsoft.Azure.WebJobs.Script.Diagnostics;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    public class PowerShellFunctionInvoker : FunctionInvokerBase
    {
        private readonly ScriptHost _host;
        private readonly string _scriptFilePath;
        private readonly string _functionName;

        private readonly Collection<FunctionBinding> _inputBindings;
        private readonly Collection<FunctionBinding> _outputBindings;
        private readonly IMetricsLogger _metrics;

        private string _script;
        private List<string> _moduleFiles;
        private Dictionary<string, string> _environmentVariables;
        private Action _reloadScript;

        private static readonly string[] WatchedFileTypes = { ".ps1", ".psm1", ".dll", ".psd1" };

        internal PowerShellFunctionInvoker(ScriptHost host, FunctionMetadata functionMetadata,
            Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings) : base(host, functionMetadata)
        {
            _host = host;
            _scriptFilePath = functionMetadata.Source;
            _functionName = functionMetadata.Name;
            _inputBindings = inputBindings;
            _outputBindings = outputBindings;
            _metrics = host.ScriptConfig.HostConfig.GetService<IMetricsLogger>();
            _environmentVariables = new Dictionary<string, string>();
            InitializeFileWatcherIfEnabled();
            _reloadScript = ReloadScript;
            _reloadScript = _reloadScript.Debounce();
        }

        public override async Task Invoke(object[] parameters)
        {
            object input = parameters[0];
            TraceWriter traceWriter = (TraceWriter)parameters[1];
            IBinderEx binder = (IBinderEx)parameters[2];
            ExecutionContext functionExecutionContext = (ExecutionContext)parameters[3];
            string invocationId = functionExecutionContext.InvocationId.ToString();

            FunctionStartedEvent startedEvent = new FunctionStartedEvent(functionExecutionContext.InvocationId, Metadata);
            _metrics.BeginEvent(startedEvent);

            try
            {
                object convertedInput = input;
                if (input != null)
                {
                    HttpRequestMessage request = input as HttpRequestMessage;
                    if (request != null)
                    {
                        // TODO: Handle other content types? (E.g. byte[])
                        if (request.Content != null && request.Content.Headers.ContentLength > 0)
                        {
                            convertedInput = ((HttpRequestMessage)input).Content.ReadAsStringAsync().Result;
                        }
                    }
                }

                TraceWriter.Verbose(string.Format("PowerShell Function started (Id={0})", invocationId));

                string functionInstanceOutputPath = Path.Combine(Path.GetTempPath(), "Functions", "Binding",
                    invocationId);

                Dictionary<string, string> bindingData = GetBindingData(convertedInput, binder);
                bindingData["InvocationId"] = invocationId;

                await
                    ProcessInputBindingsAsync(convertedInput, functionInstanceOutputPath, binder, bindingData,
                        _environmentVariables);

                InitializeEnvironmentVariables(_environmentVariables, functionInstanceOutputPath, input,
                    functionExecutionContext);

                InvokePowerShellScript();

                await
                    ProcessOutputBindingsAsync(functionInstanceOutputPath, _outputBindings, input, binder, bindingData);

                TraceWriter.Verbose(string.Format("PowerShell Function completed (Success, Id={0})", invocationId));
            }
            catch (Exception exception)
            {
                startedEvent.Success = false;
                RuntimeException runtimeException = exception as RuntimeException;
                if (runtimeException != null)
                {
                    TraceWriter.Verbose(string.Format(
                        "Function runtime exception: {0}: {1}{2}{3}",
                        runtimeException.ErrorRecord.InvocationInfo.InvocationName,
                        runtimeException.Message,
                        Environment.NewLine,
                        runtimeException.ErrorRecord.InvocationInfo.PositionMessage));
                }

                TraceWriter.Verbose(string.Format("PowerShell Function completed (Failure, Id={0})", invocationId));
                throw;
            }
            finally
            {
                _metrics.EndEvent(startedEvent);
                DeleteProcessEnvironmentVariables(_environmentVariables);
            }
        }

        protected override void OnScriptFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_script == null)
            {
                // we're not loaded yet, so nothing to reload
                return;
            }

            // The ScriptHost is already monitoring for changes to function.json, so we skip those
            string fileExtension = Path.GetExtension(e.Name);
            if (WatchedFileTypes.Contains(fileExtension))
            {
                _reloadScript();
            }
        }
        // private void InvokePowerShellScript()
        private void InvokePowerShellScript()
        {
            InitialSessionState iss = InitialSessionState.CreateDefault();

            using (Runspace runspace = RunspaceFactory.CreateRunspace(iss))
            {
                runspace.Open();

                using (
                    System.Management.Automation.PowerShell powerShellInstance =
                        System.Management.Automation.PowerShell.Create())
                {
                    powerShellInstance.Runspace = runspace;
                    _moduleFiles = GetModuleFilePaths(_functionName);
                    if (_moduleFiles.Any())
                    {
                        powerShellInstance.AddCommand("Import-Module").AddArgument(_moduleFiles);
                    }

                    _script = GetScript();
                    powerShellInstance.AddScript(_script, true);

                    PSDataCollection<PSObject> outputCollection = new PSDataCollection<PSObject>();
                    outputCollection.DataAdded += OutputCollectionDataAdded;

                    powerShellInstance.Streams.Error.DataAdded += ErrorDataAdded;

                    IAsyncResult result = powerShellInstance.BeginInvoke<PSObject, PSObject>(null, outputCollection);

                    while (result.IsCompleted == false)
                    {
                        System.Threading.Thread.Sleep(50);
                    }

                    // Clean up and remove module
                    if (_moduleFiles.Any())
                    {
                        powerShellInstance.AddCommand("Remove-Module").AddArgument(_moduleFiles);
                    }
                }

                runspace.Close();
            }
        }

        private static void SetProcessEnvironmentVariables(IDictionary<string, string> envVariables)
        {
            foreach (var pair in envVariables)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.Process);
            }
        }

        private static void DeleteProcessEnvironmentVariables(IDictionary<string, string> envVariables)
        {
            foreach (var pair in envVariables)
            {
                var envSetting = Environment.GetEnvironmentVariable(pair.Key, EnvironmentVariableTarget.Process);
                if (!string.IsNullOrEmpty(envSetting))
                {
                    Environment.SetEnvironmentVariable(pair.Key, null, EnvironmentVariableTarget.Process);
                }
            }
        }

        private async Task ProcessInputBindingsAsync(object input, string functionInstanceOutputPath, IBinderEx binder, Dictionary<string, string> bindingData, Dictionary<string, string> environmentVariables)
        {
            // if there are any input or output bindings declared, set up the temporary
            // output directory
            if (_outputBindings.Count > 0 || _inputBindings.Any())
            {
                Directory.CreateDirectory(functionInstanceOutputPath);
            }

            // process input bindings
            foreach (var inputBinding in _inputBindings)
            {
                string filePath = System.IO.Path.Combine(functionInstanceOutputPath, inputBinding.Metadata.Name);
                using (FileStream stream = File.OpenWrite(filePath))
                {
                    // If this is the trigger input, write it directly to the stream.
                    // The trigger binding is a special case because it is early bound
                    // rather than late bound as is the case with all the other input
                    // bindings.
                    if (inputBinding.Metadata.IsTrigger)
                    {
                        if (input is string)
                        {
                            using (StreamWriter sw = new StreamWriter(stream))
                            {
                                await sw.WriteAsync((string)input);
                            }
                        }
                        else if (input is byte[])
                        {
                            byte[] bytes = input as byte[];
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                        else if (input is Stream)
                        {
                            Stream inputStream = input as Stream;
                            await inputStream.CopyToAsync(stream);
                        }
                    }
                    else
                    {
                        // invoke the input binding
                        BindingContext bindingContext = new BindingContext
                        {
                            Binder = binder,
                            BindingData = bindingData,
                            Value = stream
                        };
                        await inputBinding.BindAsync(bindingContext);
                    }
                }

                environmentVariables[inputBinding.Metadata.Name] = Path.Combine(functionInstanceOutputPath, inputBinding.Metadata.Name);
            }
        }

        private static async Task ProcessOutputBindingsAsync(string functionInstanceOutputPath, Collection<FunctionBinding> outputBindings,
            object input, IBinderEx binder, Dictionary<string, string> bindingData)
        {
            if (outputBindings == null)
            {
                return;
            }

            try
            {
                foreach (var outputBinding in outputBindings)
                {
                    string filePath = System.IO.Path.Combine(functionInstanceOutputPath, outputBinding.Metadata.Name);
                    if (File.Exists(filePath))
                    {
                        using (FileStream stream = File.OpenRead(filePath))
                        {
                            BindingContext bindingContext = new BindingContext
                            {
                                Input = input,
                                Binder = binder,
                                BindingData = bindingData,
                                Value = stream
                            };
                            await outputBinding.BindAsync(bindingContext);
                        }
                    }
                }
            }
            finally
            {
                // clean up the output directory
                if (outputBindings.Any() && Directory.Exists(functionInstanceOutputPath))
                {
                    Directory.Delete(functionInstanceOutputPath, recursive: true);
                }
            }
        }

        /// <summary>
        /// Event handler for the output stream.
        /// </summary>
        private void OutputCollectionDataAdded(object sender, DataAddedEventArgs e)
        {
            // do something when an object is written to the output stream
            var source = (PSDataCollection<PSObject>)sender;
            var msg = source[e.Index].ToString();
            TraceWriter.Flush();
            TraceWriter.Info(msg);
        }

        /// <summary>
        /// Event handler for the error stream.
        /// </summary>
        private void ErrorDataAdded(object sender, DataAddedEventArgs e)
        {
            var source = (PSDataCollection<ErrorRecord>)sender;
            var msg = GetErrorMessage(source[e.Index]);
            TraceWriter.Flush();
            TraceWriter.Error(msg);
        }

        private string GetErrorMessage(ErrorRecord errorRecord)
        {
            string fileName = Path.GetFileName(_scriptFilePath);
            string invocationName = errorRecord.InvocationInfo.InvocationName;
            string errorInvocationName = string.IsNullOrEmpty(invocationName)
                ? fileName
                : errorRecord.InvocationInfo.InvocationName;
            string errorStackTrace = GetStackTrace(errorRecord.ScriptStackTrace, fileName);

            StringBuilder stringBuilder =
                new StringBuilder(string.Format("{0} : {1}{2}",
                    errorInvocationName,
                    errorRecord, Environment.NewLine));
            stringBuilder.AppendLine(errorStackTrace);
            stringBuilder.AppendLine(string.Format("{0} {1}", PowerShellConstants.AdditionChar, errorInvocationName));
            stringBuilder.AppendLine(string.Format("{0} {1}", PowerShellConstants.AdditionChar,
                new string(PowerShellConstants.UnderscoreChar, invocationName.Length)));
            stringBuilder.AppendLine(string.Format("{0}{1} {2} {3}",
                new string(PowerShellConstants.SpaceChar, PowerShellConstants.SpaceCount),
                PowerShellConstants.AdditionChar, PowerShellConstants.CategoryInfoLabel, errorRecord.CategoryInfo));
            if (string.IsNullOrEmpty(invocationName))
            {
                stringBuilder.AppendLine(string.Format("{0}{1} {2} {3},{4}",
                    new string(PowerShellConstants.SpaceChar, PowerShellConstants.SpaceCount),
                    PowerShellConstants.AdditionChar, PowerShellConstants.FullyQualifiedErrorIdLabel,
                    errorRecord.FullyQualifiedErrorId, fileName));
            }
            else
            {
                stringBuilder.AppendLine(string.Format("{0}{1} {2} {3}",
                    new string(PowerShellConstants.SpaceChar, PowerShellConstants.SpaceCount),
                    PowerShellConstants.AdditionChar, PowerShellConstants.FullyQualifiedErrorIdLabel,
                    errorRecord.FullyQualifiedErrorId));
            }

            return stringBuilder.ToString();
        }

        private string GetStackTrace(string scriptStackTrace, string fileName)
        {
            string stackTrace = scriptStackTrace.Replace(PowerShellConstants.StackTraceScriptBlock, fileName);

            if (stackTrace.Contains(_functionName))
            {
                string[] tokens = stackTrace.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                string[] newtokens = new string[tokens.Length];
                int index = 0;
                foreach (string token in tokens)
                {
                    if (token.Contains(_functionName))
                    {
                        string pattern = string.Format("^.*?(?=\\\\{0}\\\\)", _functionName);
                        MatchCollection matchCollection = Regex.Matches(token, pattern);
                        string newtoken = token.Replace(matchCollection[0].Value, string.Empty);
                        newtokens[index++] = newtoken.Replace('\\', '/');
                    }
                    else if (token.Contains(PowerShellConstants.StackTraceScriptBlock))
                    {
                        newtokens[index++] = token.Replace(PowerShellConstants.StackTraceScriptBlock, fileName);
                    }
                    else
                    {
                        newtokens[index++] = token;
                    }
                }

                stackTrace = string.Join(" ", newtokens);
            }

            return stackTrace;
        }

        private string GetScript()
        {
            string script = null;
            if (File.Exists(_scriptFilePath))
            {
                script = File.ReadAllText(_scriptFilePath);
            }

            return script;
        }

        private List<string> GetModuleFilePaths(string scriptFolder)
        {
            List<string> modulePaths = new List<string>();
            string currentDirectory = Path.Combine(_host.ScriptConfig.RootScriptPath, scriptFolder);
            string moduleDirectory = Path.Combine(currentDirectory, PowerShellConstants.ModulesFolderName);
            if (Directory.Exists(moduleDirectory))
            {
                modulePaths.AddRange(Directory.GetFiles(moduleDirectory, PowerShellConstants.ModulesManifestFileExtensionPattern));
                modulePaths.AddRange(Directory.GetFiles(moduleDirectory, PowerShellConstants.ModulesScriptFileExtensionPattern));
                modulePaths.AddRange(Directory.GetFiles(moduleDirectory, PowerShellConstants.ModulesBinaryFileExtensionPattern));
            }

            return modulePaths;
        }

        private void InitializeEnvironmentVariables(Dictionary<string, string> environmentVariables, string functionInstanceOutputPath, object input, ExecutionContext context)
        {
            environmentVariables["InvocationId"] = context.InvocationId.ToString();

            foreach (var outputBinding in _outputBindings)
            {
                environmentVariables[outputBinding.Metadata.Name] = Path.Combine(functionInstanceOutputPath, outputBinding.Metadata.Name);
            }

            Type triggerParameterType = input.GetType();
            if (triggerParameterType == typeof(HttpRequestMessage))
            {
                HttpRequestMessage request = (HttpRequestMessage)input;
                Dictionary<string, string> queryParams = request.GetQueryNameValuePairs().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var queryParam in queryParams)
                {
                    string varName = string.Format(CultureInfo.InvariantCulture, "REQ_QUERY_{0}", queryParam.Key.ToUpperInvariant());
                    environmentVariables[varName] = queryParam.Value;
                }

                foreach (var header in request.Headers)
                {
                    string varName = string.Format(CultureInfo.InvariantCulture, "REQ_HEADERS_{0}", header.Key.ToUpperInvariant());
                    environmentVariables[varName] = header.Value.First();
                }
            }

            SetProcessEnvironmentVariables(environmentVariables);
        }

        private void ReloadScript()
        {
            TraceWriter.Verbose(string.Format(CultureInfo.InvariantCulture, "Script for function '{0}' changed. Reloading.", Metadata.Name));

            _host.RestartEvent.Set();
        }
    }
}
