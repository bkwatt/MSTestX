#nullable enable
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using System.Runtime.Serialization;
using TestAppRunner.ViewModels;

namespace TestAppRunner
{
    internal class TestAdapterConnection
    {
        private SocketCommunicationManager comm = new SocketCommunicationManager();
        private bool isConnected;
        private int port;
        private System.Threading.Thread? messageLoopThread;

        public TestAdapterConnection(int port)
        {
            this.port = port;
        }

        public async Task StartAsync()
        {
            LogLifecycle($"adapter listening on port {port}");
            var server = comm.HostServer(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
            await comm.AcceptClientAsync();
            LogLifecycle("client connected");
            isConnected = true;
            var tcs = new TaskCompletionSource<object?>();
            messageLoopThread = new System.Threading.Thread(() => StartMessageLoop(tcs));
            messageLoopThread.Start();
            await tcs.Task;
        }

        private class SettingsXmlImpl : IRunSettings
        {
            public SettingsXmlImpl(string xml)
            {
                SettingsXml = xml;
            }
            public string SettingsXml { get; }

            public ISettingsProvider? GetSettings(string? settingsName) => null;
        }

        private Task<Message?> ReceiveMessageAsync()
        {
            return Task.Run<Message?>(() =>
            {
                try
                {
                    var rawMessage = comm.ReceiveRawMessage();
                    if (!string.IsNullOrEmpty(rawMessage))
                    {
                        return JsonDataSerializer.Instance.DeserializeMessage(rawMessage);
                    }
                }
                catch (System.IO.IOException ioException)
                {
                    var socketException = ioException.InnerException as System.Net.Sockets.SocketException;
                    if (socketException != null
                        && socketException.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut)
                    {
                        System.Diagnostics.Trace.TraceInformation($"SocketCommunicationManager ReceiveMessage: failed to receive message because read timeout {ioException}");
                    }
                    else
                    {
                        isConnected = false;
                        System.Diagnostics.Trace.TraceError($"SocketCommunicationManager ReceiveMessage: failed to receive message {ioException}");
                    }
                }
                catch (Exception exception)
                {
                    EqtTrace.Error(
                        "SocketCommunicationManager ReceiveMessage: failed to receive message {0}",
                        exception);
                }
                return null;
            });

        }
            
        private async void StartMessageLoop(TaskCompletionSource<object?> tcs)
        {
            while (isConnected)
            {
                var message = await ReceiveMessageAsync();
                if(message != null)
                {
                    if (message.MessageType == MessageType.SessionConnected)
                    {
                        LogLifecycle("session connected; sending version check");
                        // Version Check
                        comm.SendMessage(MessageType.VersionCheck, 1);
                        tcs.TrySetResult(null);
                        SendTestHostLaunched();
                        isConnected = true;
                    }
                    else if (!isConnected)
                    {
                        continue;
                    }
                    else if (message.MessageType == MessageType.StartDiscovery)
                    {
                        var req = comm.DeserializePayload<DiscoveryRequestPayload>(message);
                        comm.SendMessage(MessageType.DiscoveryInitialize);
                        var tests = ViewModels.TestRunnerVM.Instance.Tests;
                        LogLifecycle($"discovery complete: tests={tests.Count()}");
                        comm.SendMessage(MessageType.DiscoveryComplete, new
                            DiscoveryCompletePayload() { LastDiscoveredTests = tests.Select(t => t.Test), TotalTests = tests.Count() });
                    }
                    else if (message.MessageType == MessageType.TestRunSelectedTestCasesDefaultHost ||
                        message.MessageType == MessageType.TestRunAllSourcesWithDefaultHost)
                    {
                        var trr = comm.DeserializePayload<TestRunRequestPayload>(message);
                        if (trr.TestCases != null)
                        {
                            var testsToRun = ViewModels.TestRunnerVM.Instance.Tests.Select(t => t.Test).Where(t => trr.TestCases.Any(t2 => t2.Id == t.Id)).ToList();
                            _ = RunRemoteTestsAsync(testsToRun, trr.RunSettings);
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Got message: {message.MessageType}, Payload={message.Payload}");
                    }
                }
            }
            comm.StopServer();
            _ = StartAsync();
        }

        private async Task RunRemoteTestsAsync(List<TestCase> testsToRun, string? runSettings)
        {
            DateTime start = DateTime.Now;
            LogLifecycle($"run request received: tests={testsToRun.Count}");
            try
            {
                var settings = new SettingsXmlImpl(ViewModels.TestRunnerVM.Instance.Settings.AppendParameters(runSettings));
                var results = (await ViewModels.TestRunnerVM.Instance.RunFromRemoteAdapter(testsToRun, settings)).ToList();
                TimeSpan elapsedTime = DateTime.Now - start;
                LogLifecycle($"run completed: results={results.Count}");

                var stats = new Dictionary<TestOutcome, long>()
                {
                    { TestOutcome.Passed, results.Where(t => t.Outcome == TestOutcome.Passed).Count() },
                    { TestOutcome.Failed, results.Where(t => t.Outcome == TestOutcome.Failed).Count() },
                    { TestOutcome.Skipped, results.Where(t => t.Outcome == TestOutcome.Skipped).Count() },
                    { TestOutcome.NotFound, results.Where(t => t.Outcome == TestOutcome.NotFound).Count() },
                    { TestOutcome.None, results.Where(t => t.Outcome == TestOutcome.None).Count() }
                };

                var testRunStats = new TestRunStatistics(results.Count(), stats);
                var payload = new TestRunCompletePayload()
                {
                    LastRunTests = new TestRunChangedEventArgs(testRunStats, results, testsToRun),
                    RunAttachments = new List<AttachmentSet>(),
                    TestRunCompleteArgs = new TestRunCompleteEventArgs(testRunStats, false, false, null, new System.Collections.ObjectModel.Collection<AttachmentSet>(), elapsedTime)
                };
                TrySendExecutionComplete(payload);
            }
            catch (OperationCanceledException)
            {
                LogLifecycle("run canceled; sending CancelTestRun");
                TrySendMessage(MessageType.CancelTestRun);
            }
            catch (Exception ex)
            {
                LogLifecycle($"run failed: {ex}");
                TrySendMessage(MessageType.TestMessage, new TestMessagePayload { MessageLevel = TestMessageLevel.Error, Message = ex.ToString() });
                var runCompletePayload = new TestRunCompletePayload()
                {
                    TestRunCompleteArgs = new TestRunCompleteEventArgs(null, false, true, ex, null, TimeSpan.MinValue),
                    LastRunTests = null
                };
                TrySendExecutionComplete(runCompletePayload);
            }
            finally
            {
                ViewModels.TestRunnerVM.Instance.TerminateAfterRemoteAdapterCompletion();
            }
        }

        private void SendExecutionComplete(TestRunCompletePayload payload)
        {
            LogLifecycle("sending ExecutionComplete");
            SendMessage(MessageType.ExecutionComplete, payload);
            LogLifecycle("sent ExecutionComplete");
        }

        private bool TrySendExecutionComplete(TestRunCompletePayload payload)
        {
            try
            {
                SendExecutionComplete(payload);
                return true;
            }
            catch (Exception ex)
            {
                LogLifecycle($"failed to send ExecutionComplete: {ex.Message}");
                return false;
            }
        }

        private bool TrySendMessage(string messageType)
        {
            try
            {
                SendMessage(messageType);
                return true;
            }
            catch (Exception ex)
            {
                LogLifecycle($"failed to send {messageType}: {ex.Message}");
                return false;
            }
        }

        private bool TrySendMessage(string messageType, object payload)
        {
            try
            {
                SendMessage(messageType, payload);
                return true;
            }
            catch (Exception ex)
            {
                LogLifecycle($"failed to send {messageType}: {ex.Message}");
                return false;
            }
        }

        private static void LogLifecycle(string message)
        {
            ViewModels.TestRunnerVM.LogLifecycle(message);
        }

        internal void SendMessage(string messageType, object payload)
        {
            var json = JsonDataSerializer.Instance.SerializePayload(messageType, payload);
            comm.SendMessage(messageType, payload);
        }

        internal void SendMessage(string messageType)
        {
            var json = JsonDataSerializer.Instance.SerializeMessage(messageType);
            comm.SendMessage(messageType);
        }

        internal void SendMessage(TestMessageLevel testMessageLevel, string message)
        {
            SendMessage(MessageType.TestMessage, new TestMessagePayload { MessageLevel = testMessageLevel, Message = message });
        }

        internal void SendTestStart(TestCase testCase)
        {
            SendMessage(MessageType.DataCollectionTestStart, new TestCaseStartEventArgs(testCase));
        }

        internal void SendTestHostLaunched()
        {
            SendMessage(MessageType.TestHostLaunched, new TestHostLaunchedPayload() { ProcessId = GetProcessId() });
        }

        internal void SendTestEndResult(TestResult testResult)
        {
            // Make attachment paths relative
            foreach (var set in testResult.Attachments)
            {
                for (int i = 0; i < set.Attachments.Count; i++)
                {
                    var uri = set.Attachments[i].Uri.OriginalString;

                    var rootDirectory = TestRunnerVM.Instance.Settings.TestRunDirectory;
                    if (uri.StartsWith(rootDirectory))
                    {
                        uri = uri.Substring(rootDirectory.Length);
                        if (rootDirectory.Last() != System.IO.Path.PathSeparator && uri[0] == System.IO.Path.PathSeparator)
                            uri = uri.Substring(1);
                        set.Attachments[i] = new UriDataAttachment(new Uri(uri, UriKind.Relative), set.Attachments[i].Description);
                    }
                }
            }
            SendMessage(MessageType.DataCollectionTestEndResult, new Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection.TestResultEventArgs(testResult));
        }

        internal void SendTestEnd(TestCase testCase, TestOutcome outcome)
        {
            SendMessage(MessageType.DataCollectionTestEnd, new TestCaseEndEventArgs(testCase, outcome));
        }


        internal void SendAttachments(IList<AttachmentSet> attachmentSets, string rootDirectory)
        {
            foreach (var set in attachmentSets)
            {
                SendMessage("AttachmentSet", new FileAttachmentSet(set, rootDirectory));
            }
        }

        [DataContract]
        private class FileAttachmentSet
        {
            [DataMember]
            public Uri Uri { get; private set; }

            [DataMember]
            public string DisplayName { get; private set; }

            [DataMember]
            public IList<FileDataAttachment> Attachments { get; private set; }

            public FileAttachmentSet(AttachmentSet set, string rootDirectory)
            {
                Uri = set.Uri;
                DisplayName = set.DisplayName;
                Attachments = set.Attachments.Select(a => new FileDataAttachment(a, rootDirectory)).ToList();
            }
        }
        [DataContract]
        private class FileDataAttachment
        {
            [DataMember]
            public string? Description { get; private set; }
            [DataMember]
            public string Uri { get; private set; }
            private byte[]? data;
            [DataMember]
            public byte[]? Data
            { 
                get => data ?? (path is not null ? (data = System.IO.File.ReadAllBytes(path)) : null); 
                private set => data = value; 
            }
            string? path = null;
            public FileDataAttachment(UriDataAttachment a, string rootDirectory)
            {
                path = a.Uri.LocalPath;
                Uri = a.Uri.OriginalString;
                if (Uri.StartsWith(rootDirectory))
                {
                    Uri = Uri.Substring(rootDirectory.Length);
                    if (rootDirectory.Last() != System.IO.Path.PathSeparator && Uri[0] == System.IO.Path.PathSeparator)
                        Uri = Uri.Substring(1);
                }
                Description = a.Description;
            }
        }

        private static int GetProcessId()
        {
#if __ANDROID__
            return Android.OS.Process.MyPid();
#elif __IOS__
            return Foundation.NSProcessInfo.ProcessInfo.ProcessIdentifier;
#else
            return 0;
#endif
            /*int pid = 0;
            if (Xamarin.Forms.Device.RuntimePlatform == Xamarin.Forms.Device.Android)
            {
                var processType = Type.GetType("Android.OS.Process, Mono.Android, Version=0.0.0.0, Culture=neutral, PublicKeyToken=84e04ff9cfb79065");
                var myPidMethod = processType.GetMethod("MyPid", new Type[] { });
                pid = (int)myPidMethod.Invoke(null, new object[] { });
            }
            else if (Xamarin.Forms.Device.RuntimePlatform == Xamarin.Forms.Device.iOS)
            {
                var processType = Type.GetType("Foundation.NSProcessInfo.ProcessInfo, Xamarin.iOS, Version=0.0.0.0, Culture=neutral, PublicKeyToken=84e04ff9cfb79065");
                var pidProperty = processType.GetProperty("ProcessIdentifier");
                pid = (int)pidProperty.GetValue(null);
            }
            return pid;*/
        }
    }
}
