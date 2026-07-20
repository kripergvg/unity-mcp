using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Tests for RunTests tool functionality.
    /// Note: We cannot easily test the full HandleCommand because it would create
    /// recursive test runner calls.
    /// </summary>
    public class RunTestsTests
    {
        [Test]
        public void HandleCommand_WhenTestsAlreadyRunning_ReturnsBusyError()
        {
            // Arrange: Force TestJobManager into a "busy" state without starting a real run.
            // We do this via reflection because TestJobManager is internal.
            var asm = typeof(MCPForUnity.Editor.Services.MCPServiceLocator).Assembly;
            var testJobManagerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(testJobManagerType, "Could not locate TestJobManager type via reflection");

            var currentJobIdField = testJobManagerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(currentJobIdField, "Could not locate TestJobManager._currentJobId field");

            var originalJobId = currentJobIdField.GetValue(null) as string;
            currentJobIdField.SetValue(null, "busy-test-job-id");

            try
            {
                var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject()).GetAwaiter().GetResult();

                Assert.IsInstanceOf<ErrorResponse>(resultObj);
                var err = (ErrorResponse)resultObj;
                Assert.AreEqual(false, err.Success);
                Assert.AreEqual("tests_running", err.Code);

                var data = err.Data != null ? JObject.FromObject(err.Data) : null;
                Assert.NotNull(data, "Expected data payload on tests_running error");
                Assert.AreEqual("tests_running", data["reason"]?.ToString());
                Assert.GreaterOrEqual(data["retry_after_ms"]?.Value<int>() ?? 0, 500);
            }
            finally
            {
                currentJobIdField.SetValue(null, originalJobId);
            }
        }

        [Test]
        public void HandleCommand_WithInvalidMode_ReturnsError()
        {
            var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
            {
                ["mode"] = "NotARealMode"
            }).GetAwaiter().GetResult();

            Assert.IsInstanceOf<ErrorResponse>(resultObj);
            var err = (ErrorResponse)resultObj;
            Assert.AreEqual(false, err.Success);
            Assert.IsTrue(err.Error.Contains("Unknown test mode", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void FilterOptions_ParseExcludedCategories()
        {
            var method = typeof(MCPForUnity.Editor.Tools.RunTests).GetMethod(
                "GetFilterOptions",
                BindingFlags.Static | BindingFlags.NonPublic);
            var options = method.Invoke(null, new object[]
            {
                new JObject
                {
                    ["assemblyNames"] = new JArray("FastCombat.Tests"),
                    ["excludeCategoryNames"] = new JArray("Performance")
                }
            });
            var type = options.GetType();

            CollectionAssert.AreEqual(
                new[] { "Performance" },
                (string[])type.GetProperty("ExcludeCategoryNames").GetValue(options));
        }

        [Test]
        public void FailedJobSerializationIncludesFullResultAndDiagnostics()
        {
            var asm = typeof(MCPForUnity.Editor.Services.MCPServiceLocator).Assembly;
            var summaryType = asm.GetType("MCPForUnity.Editor.Services.TestRunSummary");
            var testResultType = asm.GetType("MCPForUnity.Editor.Services.TestRunTestResult");
            var runResultType = asm.GetType("MCPForUnity.Editor.Services.TestRunResult");
            var jobType = asm.GetType("MCPForUnity.Editor.Services.TestJob");
            var statusType = asm.GetType("MCPForUnity.Editor.Services.TestJobStatus");
            var managerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");

            Assert.NotNull(summaryType);
            Assert.NotNull(testResultType);
            Assert.NotNull(runResultType);
            Assert.NotNull(jobType);
            Assert.NotNull(statusType);
            Assert.NotNull(managerType);

            var summary = Activator.CreateInstance(
                summaryType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[] { 1, 0, 1, 0, 0.25, "Failed" },
                null);
            var testResult = Activator.CreateInstance(
                testResultType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    "Fails",
                    "Example.Tests.Fails",
                    "Failed",
                    0.25,
                    "complete failure message",
                    "complete stack trace",
                    "complete captured output"
                },
                null);
            var resultList = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(testResultType));
            resultList.Add(testResult);
            var runResult = Activator.CreateInstance(
                runResultType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { summary, resultList },
                null);
            var job = Activator.CreateInstance(jobType);
            jobType.GetProperty("JobId").SetValue(job, "job-1");
            jobType.GetProperty("Status").SetValue(job, Enum.Parse(statusType, "Failed"));
            jobType.GetProperty("Mode").SetValue(job, "EditMode");
            jobType.GetProperty("Result").SetValue(job, runResult);

            var serializer = managerType.GetMethod(
                "ToSerializable",
                BindingFlags.Static | BindingFlags.NonPublic);
            var payload = JObject.FromObject(serializer.Invoke(null, new[] { job, false, true }));

            Assert.AreEqual("failed", payload["status"]?.ToString());
            Assert.AreEqual(1, payload["result"]?["summary"]?["failed"]?.Value<int>());
            Assert.AreEqual(
                "complete failure message",
                payload["result"]?["results"]?[0]?["message"]?.ToString());
            Assert.AreEqual(
                "complete stack trace",
                payload["result"]?["results"]?[0]?["stackTrace"]?.ToString());
            Assert.AreEqual(
                "complete captured output",
                payload["result"]?["results"]?[0]?["output"]?.ToString());
        }
    }
}
