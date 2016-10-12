﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Settings;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics
{
    public class WebHostMetricsLogger : IMetricsLogger, IDisposable
    {
        private readonly MetricsEventManager _metricsEventManager;
        private bool disposed = false;

        public WebHostMetricsLogger(ISettingsManager settingsManager)
            : this(settingsManager, new EventGenerator(), 5)
        {
        }

        public WebHostMetricsLogger(MetricsEventManager eventManager)
        {
            _metricsEventManager = eventManager;
        }

        public WebHostMetricsLogger(ISettingsManager settingsManager, IEventGenerator eventGenerator, int metricEventIntervalInSeconds)
        {
            _metricsEventManager = new MetricsEventManager(settingsManager, eventGenerator, metricEventIntervalInSeconds);
        }

        public object BeginEvent(string eventName)
        {
            return _metricsEventManager.BeginEvent(eventName);
        }

        public void BeginEvent(MetricEvent metricEvent)
        {
            FunctionStartedEvent startedEvent = metricEvent as FunctionStartedEvent;
            if (startedEvent != null)
            {
                startedEvent.Timestamp = DateTime.UtcNow;
                _metricsEventManager.FunctionStarted(startedEvent);
            }
        }

        public void EndEvent(object eventHandle)
        {
            _metricsEventManager.EndEvent(eventHandle);
        }

        public void EndEvent(MetricEvent metricEvent)
        {
            FunctionStartedEvent completedEvent = metricEvent as FunctionStartedEvent;
            if (completedEvent != null)
            {
                completedEvent.Duration = DateTime.UtcNow - completedEvent.Timestamp;
                _metricsEventManager.FunctionCompleted(completedEvent);
            }
            else
            {
                _metricsEventManager.EndEvent((object)metricEvent);
            }
        }

        public void LogEvent(MetricEvent metricEvent)
        {
            HostStarted hostStartedEvent = metricEvent as HostStarted;
            if (hostStartedEvent != null)
            {
                _metricsEventManager.HostStarted(hostStartedEvent.Host);
            }
        }

        public void LogEvent(string eventName)
        {
            _metricsEventManager.LogEvent(eventName);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (_metricsEventManager != null)
                    {
                        _metricsEventManager.Dispose();
                    }
                }

                disposed = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}