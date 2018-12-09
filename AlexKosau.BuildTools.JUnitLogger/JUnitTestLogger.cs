﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using AlexKosau.BuildTools.JUnitLogger.JUnitSchema;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using TestCase = AlexKosau.BuildTools.JUnitLogger.JUnitSchema.TestCase;

namespace AlexKosau.BuildTools.JUnitLogger
{
    [ExtensionUri("logger://JUnitLogger/v1")]
    [FriendlyName("JUnitLogger")]
    public class JUnitTestLogger : ITestLoggerWithParameters
    {
        private readonly List<string> stdOut = new List<string>();
        private string machineName = string.Empty;
        private Dictionary<string, string> startupParameters = new Dictionary<string, string>();
        internal Queue<TestCase> testCases = new Queue<TestCase>();
        private string testRunDirectory;

        /// <summary>
        ///     Initializes the Test Logger.
        /// </summary>
        /// <param name="events">Events that can be registered for.</param>
        /// <param name="testRunDirectory">Test Run Directory</param>
        public void Initialize(TestLoggerEvents events, string testRunDirectory)
        {
            //Debugger.Launch();

            Initialize(events, new Dictionary<string, string>
            {
                {"TestResultsFile", "TestResults.xml"},
                {"IncludeSourceFileInfo", "false"}
            });
        }

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            //Debugger.Launch();

            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += ReflectionOnlyAssemblyResolve;

            startupParameters = parameters;
            Trace.WriteLine("[JUnitLogger] Current dir: " + Environment.CurrentDirectory);
            foreach (var kv in parameters)
            {
                Trace.WriteLine(string.Format("[JUnitLogger] Init parameter: " + kv.Key + " " + kv.Value));
            }
            SubscribeToEvents(events);
        }

        private void SubscribeToEvents(TestLoggerEvents events)
        {
            events.TestRunMessage += TestMessageHandler;
            events.TestResult += TestResultHandler;
            events.TestRunComplete += TestRunCompleteHandler;
        }

        /// <summary>
        ///     Called when a test message is received.
        /// </summary>
        private void TestMessageHandler(object sender, TestRunMessageEventArgs e)
        {
            switch (e.Level)
            {
                case TestMessageLevel.Informational:
                    stdOut.Add("Information: " + e.Message);
                    Console.WriteLine("[JUnitLogger] Information: " + e.Message);
                    break;

                case TestMessageLevel.Warning:
                    stdOut.Add("Warning: " + e.Message);
                    Console.WriteLine("[JUnitLogger] Warning: " + e.Message);
                    break;

                case TestMessageLevel.Error:
                    stdOut.Add("Error: " + e.Message);
                    Console.WriteLine("[JUnitLogger] Error: " + e.Message);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        ///     Called when a test result is received.
        /// </summary>
        private void TestResultHandler(object sender, TestResultEventArgs e)
        {
            machineName = e.Result.ComputerName;

            var testCase = new TestCase
            {
                Name = e.Result.TestCase.DisplayName,
                Classname = GetClassName(e.Result.TestCase.FullyQualifiedName), 
                Result = e.Result.Outcome.ToString(),
                Status = "run",
                Time = e.Result.Duration.TotalSeconds,
            };

            if (e.Result.Outcome == TestOutcome.Skipped)
            {
                var methodInfo = this.GetTestMethodInfo(e.Result);
                var description = this.GetDescription(methodInfo);
                
                // testCase.Skipped = new Skipped
                //                        {
                //                            Text = "Yes",
                //                            Message = description ?? "---EMPTY---"
                //                        };
            }

            //IncludeErrorsAndFailures(e, testCase);
            //this.PrintSourceCodeInformation(e, testCase);
            //IncludeMessages(e, testCase);

            testCases.Enqueue(testCase);
        }

        private MethodInfo GetTestMethodInfo(TestResult testResult)
        {
            string currentTestAssembly = testResult.TestCase.Source;
            string fullyQualifiedName = testResult.TestCase.FullyQualifiedName;
            string declaringClass = fullyQualifiedName.Substring(0, fullyQualifiedName.LastIndexOf('.'));
            string methodName = fullyQualifiedName.Substring(fullyQualifiedName.LastIndexOf('.') + 1);

            Assembly assembly = LoadAssembly(currentTestAssembly);

            Type testClass = assembly.GetType(declaringClass, true, false);

            MethodInfo methodInfo = testClass.GetMethod(methodName);

            return methodInfo;
        }

        IDictionary<string, Assembly> assemblyMap = new Dictionary<string, Assembly>();
        private Assembly LoadAssembly(string assemblyName)
        {
            Assembly assembly;
            if (!this.assemblyMap.TryGetValue(assemblyName, out assembly))
            {
                assembly = Assembly.LoadFrom(assemblyName);
                this.assemblyMap.Add(assemblyName, assembly);
            }

            return assembly;
        }

        private Assembly ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            return System.Reflection.Assembly.ReflectionOnlyLoad(args.Name);
        }
        private string GetDescription(MethodInfo methodInfo)
        {
            return ReflectionOnlyContextAttributeValueReader.TryGetAttributeConstructorArgument<DescriptionAttribute, string>(methodInfo).FirstOrDefault<string>()
                ?? ReflectionOnlyContextAttributeValueReader.TryGetAttributeConstructorArgument<Microsoft.VisualStudio.TestTools.UnitTesting.DescriptionAttribute, string>(methodInfo).FirstOrDefault<string>();
        }


        private static void IncludeErrorsAndFailures(TestResultEventArgs e, TestCase testCase)
        {
            var errorMessage = e.Result.ErrorMessage ?? string.Empty;
            var errorStackTrace = e.Result.ErrorStackTrace ?? string.Empty;

            if (string.IsNullOrWhiteSpace(errorMessage) && string.IsNullOrWhiteSpace(errorStackTrace))
            {
                return;
            }

            var err = new List<ErrorOrFailure>
                          {
                              new ErrorOrFailure
                                  {
                                      Message = new string(errorMessage.Select(ch => XmlConvert.IsXmlChar(ch) ? ch : '?').ToArray()),
                                      Text = new string(errorStackTrace.Select(ch => XmlConvert.IsXmlChar(ch) ? ch : '?').ToArray())
                                  }
                          };
            if (e.Result.Outcome == TestOutcome.Failed)
            {
                testCase.Failures = err;
            }
            else
            {
                testCase.Errors = err;
            }
        }

        private static void IncludeMessages(TestResultEventArgs e, TestCase testCase)
        {
            foreach (TestResultMessage msg in e.Result.Messages)
            {
                if (msg.Category == "StdOutMsgs")
                {
                    if (testCase.SystemOut == null) testCase.SystemOut = string.Empty;
                    testCase.SystemOut += msg.Text + Environment.NewLine;
                }
                else if (msg.Category == "StdErrMsgs")
                {
                    if (testCase.SystemErr == null) testCase.SystemErr = string.Empty;
                    testCase.SystemErr += msg.Text + Environment.NewLine;
                }
                else
                {
                    testCase.SystemOut += msg.Category + ": " + msg.Text + Environment.NewLine;
                }
            }
        }

        private void PrintSourceCodeInformation(TestResultEventArgs e, TestCase testCase)
        {
            if (startupParameters.ContainsKey("IncludeSourceFileInfo") &&
                Convert.ToBoolean(startupParameters["IncludeSourceFileInfo"]))
            {
                var systemOut = new StringBuilder();
                systemOut.AppendLine("------------- Source code information -------------");
                systemOut.AppendLine("Source: " + e.Result.TestCase.GetPropertyValue(TestCaseProperties.Source, "N/A"));
                systemOut.AppendLine("Code file path: " +
                                     e.Result.TestCase.GetPropertyValue(TestCaseProperties.CodeFilePath, "N/A"));
                systemOut.AppendLine("Line number: " +
                                     e.Result.TestCase.GetPropertyValue(TestCaseProperties.LineNumber, "N/A"));
                systemOut.AppendLine("Started: " + e.Result.GetPropertyValue(TestResultProperties.StartTime, "N/A"));
                systemOut.AppendLine("Finished: " + e.Result.GetPropertyValue(TestResultProperties.EndTime, "N/A"));
                testCase.SystemOut = systemOut.ToString();

                if (e.Result.Messages.Any(m => m.Category == "StdOutMsgs"))
                {
                    systemOut.AppendLine("------------- Stdout -------------");
                }
            }
        }

        internal static string GetClassName(string fullyQualifiedName)
        {
            int indexOfDot = fullyQualifiedName.LastIndexOf('.');
            if (indexOfDot == -1) return fullyQualifiedName;

            return fullyQualifiedName.Remove(indexOfDot);
        }

        /// <summary>
        ///     Called when a test run is completed.
        /// </summary>
        private void TestRunCompleteHandler(object sender, TestRunCompleteEventArgs e)
        {
            try
            {
                Console.WriteLine("Total Executed: {0}", e.TestRunStatistics.ExecutedTests);
                Console.WriteLine("Total Passed: {0}", e.TestRunStatistics[TestOutcome.Passed]);
                Console.WriteLine("Total Failed: {0}", e.TestRunStatistics[TestOutcome.Failed]);
                Console.WriteLine("Total Skipped: {0}", e.TestRunStatistics[TestOutcome.Skipped]);

                var root = new TestRun { TestSuites = new List<TestSuite>() };

                var result = new TestSuite
                {
                    Name = "VS Test result",
                    Failures = (int)e.TestRunStatistics[TestOutcome.Failed],
                    Skipped = (int)e.TestRunStatistics[TestOutcome.Skipped],
                    Errors =
                        (int)e.TestRunStatistics[TestOutcome.None] + (int)e.TestRunStatistics[TestOutcome.NotFound],
                    Tests = (int)e.TestRunStatistics.ExecutedTests,
                    TestCases = testCases.ToList(),
                    //Timestamp = DateTime.Now,
                    Time = e.ElapsedTimeInRunningTests.TotalSeconds,
                    //SystemOut = string.Join(Environment.NewLine, stdOut),
                    //Hostname = machineName,
                    Properties = null,
                    // Properties = new List<property>
                    // {
                    //     new property {Name = "IsAborted", Value = e.IsAborted.ToString()},
                    //     new property {Name = "IsCanceled", Value = e.IsCanceled.ToString()},
                    // }
                };

                if (e.Error != null)
                {
                    result.SystemOut = e.Error + Environment.NewLine + result.SystemOut;
                    //result.Properties.Add(new property { Name = "Error", Value = e.Error.ToString() });
                }

                root.TestSuites.Add(result);

                var ser = new XmlSerializer(typeof(TestRun));
                var settings = new XmlWriterSettings();                
                settings.OmitXmlDeclaration = true;
                settings.Indent = true;
                var emptyNamespaces = new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty });

                string fileName;
                if (!startupParameters.TryGetValue("TestResultsFile", out fileName) ||
                    string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "TestResult.xml";
                }
                Console.WriteLine("Writing the results into {0}", Path.GetFullPath(fileName));
                using (FileStream fs = File.Create(fileName))
                using (var writer = XmlWriter.Create(fs, settings))
                {
                    ser.Serialize(writer, root, emptyNamespaces);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("IO exception: {0}", ex.Message);
            }
            catch (SerializationException ex)
            {
                Console.WriteLine("Serialization exception: {0}", ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }
        }

        static class ReflectionOnlyContextAttributeValueReader
        {
            public static IEnumerable<TResult> TryGetAttributeProperty<TAttribute, TResult>(MethodInfo methodInfo, string propertyName)
                where TAttribute : Attribute
            {
                Func<CustomAttributeData, AttributeValue<TResult>> extractor = (customAttributeData) =>
                {
                    IList<CustomAttributeNamedArgument> properties = customAttributeData.NamedArguments;

                    foreach (CustomAttributeNamedArgument customAttributeNamedArgument in properties)
                    {
                        if (customAttributeNamedArgument.MemberName == propertyName)
                        {
                            if (customAttributeNamedArgument.TypedValue.ArgumentType != typeof(TResult)) return AttributeValue<TResult>.NotFound;

                            return new AttributeValue<TResult>((TResult)customAttributeNamedArgument.TypedValue.Value);
                        }
                    }

                    return AttributeValue<TResult>.NotFound;
                };
                return GetAttributeValueInternal<TAttribute, TResult>(methodInfo, extractor);
            }

            public static IEnumerable<TResult> TryGetAttributeConstructorArgument<TAttribute, TResult>(MethodInfo methodInfo)
                where TAttribute : Attribute
            {
                return TryGetAttributeConstructorArgument<TAttribute, TResult>(methodInfo, 0);
            }

            public static IEnumerable<TResult> TryGetAttributeConstructorArgument<TAttribute, TResult>(MethodInfo methodInfo, int constructorArgumentPosition)
                 where TAttribute : Attribute
            {
                Func<CustomAttributeData, AttributeValue<TResult>> extractor = (customAttributeData) =>
                {
                    IList<CustomAttributeTypedArgument> constructorArguments = customAttributeData.ConstructorArguments;

                    if (constructorArgumentPosition >= constructorArguments.Count) return AttributeValue<TResult>.NotFound;

                    CustomAttributeTypedArgument customAttributeTypedArgument = constructorArguments[constructorArgumentPosition];

                    if (customAttributeTypedArgument.ArgumentType != typeof(TResult)) return AttributeValue<TResult>.NotFound;

                    return new AttributeValue<TResult>((TResult)customAttributeTypedArgument.Value);
                };
                return GetAttributeValueInternal<TAttribute, TResult>(methodInfo, extractor);
            }

            private static IEnumerable<TResult> GetAttributeValueInternal<TAttribute, TResult>(MethodInfo methodInfo, Func<CustomAttributeData, AttributeValue<TResult>> extractor)
                where TAttribute : Attribute
            {
                IEnumerable<CustomAttributeData> customAttributeDataList = GetCustomAttributeData<TAttribute>(methodInfo);

                if (!customAttributeDataList.Any())
                {
                    return Enumerable.Empty<TResult>();
                }

                ICollection<TResult> result = new List<TResult>();

                foreach (CustomAttributeData customAttributeData in customAttributeDataList)
                {
                    AttributeValue<TResult> value = extractor(customAttributeData);

                    if (value == AttributeValue<TResult>.NotFound) continue;

                    result.Add(value.Value);
                }

                return result;
            }

            // a home cooked nullable style wrapper.
            private class AttributeValue<T>
            {
                // marks an attribute value that wasn't found.
                // could have used null but this is more readable.
                public static readonly AttributeValue<T> NotFound = new AttributeValue<T>();

                private AttributeValue()
                {
                    Value = default(T);
                }

                public AttributeValue(T value)
                {
                    Value = value;
                }

                public T Value { get; private set; }
            }

            private static IEnumerable<CustomAttributeData> GetCustomAttributeData<TAttribute>(MethodInfo methodInfo)
            {
                IList<CustomAttributeData> customAttributeDataList = CustomAttributeData.GetCustomAttributes(methodInfo);

                return customAttributeDataList.Where<CustomAttributeData>(cad => cad.AttributeType.FullName == typeof(TAttribute).FullName);
            }
        }
    }
}