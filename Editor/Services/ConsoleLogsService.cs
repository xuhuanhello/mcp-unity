using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Services
{
    /// <summary>
    /// Reads the Unity Console window's own entries (internal UnityEditor.LogEntries), so results match
    /// exactly what the user sees in the Console. This includes native engine messages (e.g. Metal
    /// warnings) that are written to the Console directly and never reach Application.logMessageReceived.
    /// The Console's severity toggles and search box are neutralized during a read and then restored.
    /// </summary>
    public class ConsoleLogsService : IConsoleLogsService
    {
        // LogEntry.mode bits, mirroring UnityEditor.ConsoleWindow.Mode (stable from 2021.3 through 6000.x).
        // The Console picks its error/warning/info icon from these same bits.
        private const int ModeError = 1 << 0;
        private const int ModeAssert = 1 << 1;
        private const int ModeFatal = 1 << 4;
        private const int ModeAssetImportError = 1 << 6;
        private const int ModeAssetImportWarning = 1 << 7;
        private const int ModeScriptingError = 1 << 8;
        private const int ModeScriptingWarning = 1 << 9;
        private const int ModeScriptCompileError = 1 << 11;
        private const int ModeScriptCompileWarning = 1 << 12;
        private const int ModeStickyError = 1 << 13;
        private const int ModeScriptingException = 1 << 17;
        private const int ModeScriptingAssertion = 1 << 21;
        private const int ModeVisualScriptingError = 1 << 22;

        private const int ModeMaskError = ModeError | ModeFatal | ModeAssetImportError | ModeScriptingError
                                          | ModeScriptCompileError | ModeStickyError | ModeVisualScriptingError;
        private const int ModeMaskWarning = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        // UnityEditor.ConsoleWindow.ConsoleFlags forced on during a read: the LogLevelLog/Warning/Error toggles
        // (so hidden severities are still read) and ShowTimestamp (so entry lines carry their time).
        private const int ConsoleFlagsForRead = (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10);

        // "[12:34:56] " prefix of an entry's first line while ShowTimestamp is on.
        private static readonly Regex TimestampPrefix = new Regex(@"^\[(\d{2}:\d{2}:\d{2})\]\s");

        // MCP log type names mapped to Unity log types ("error" also covers exceptions and asserts).
        private static readonly Dictionary<string, HashSet<LogType>> LogTypeMapping =
            new Dictionary<string, HashSet<LogType>>(StringComparer.OrdinalIgnoreCase)
            {
                { "info", new HashSet<LogType> { LogType.Log } },
                { "log", new HashSet<LogType> { LogType.Log } },
                { "warning", new HashSet<LogType> { LogType.Warning } },
                { "error", new HashSet<LogType> { LogType.Error, LogType.Exception, LogType.Assert } },
                { "exception", new HashSet<LogType> { LogType.Exception } },
                { "assert", new HashSet<LogType> { LogType.Assert } }
            };

        private static ConsoleReflection _reflection;

        // Entries are read on demand from the Console itself, so there is nothing to listen to or buffer.
        public void StartListening() { }
        public void StopListening() { }
        public void CleanupOldLogs(int keepCount = 500) { }

        public int GetLogCount()
        {
            ConsoleReflection reflection = GetReflection();
            return reflection != null ? (int)reflection.GetCount.Invoke(null, null) : 0;
        }

        /// <summary>
        /// Get Console entries (newest first) as JSON with pagination support
        /// </summary>
        public JObject GetLogsAsJson(string logType = "", int offset = 0, int limit = 100, bool includeStackTrace = true)
        {
            ConsoleReflection reflection = GetReflection();
            if (reflection == null)
            {
                throw new InvalidOperationException(
                    "Could not access the Unity Console (UnityEditor.LogEntries); this Unity version is not supported.");
            }

            HashSet<LogType> wanted = null;
            if (!string.IsNullOrEmpty(logType) && !LogTypeMapping.TryGetValue(logType, out wanted))
            {
                if (!Enum.TryParse(logType, true, out LogType parsed))
                {
                    throw new ArgumentException($"Unknown log type '{logType}'. Use info, warning or error.");
                }
                wanted = new HashSet<LogType> { parsed };
            }

            var logsArray = new JArray();
            int totalCount = 0;
            int filteredCount = 0;

            bool flagsOverridden = TryForceReadFlags(reflection, out int savedFlags);
            bool searchOverridden = TryClearSearch(reflection, out string savedSearch);
            try
            {
                totalCount = (int)reflection.StartGettingEntries.Invoke(null, null);
                object entry = Activator.CreateInstance(reflection.LogEntryType);
                var args = new object[2];

                for (int i = totalCount - 1; i >= 0; i--) // newest first
                {
                    args[0] = i;
                    args[1] = entry;
                    reflection.GetEntryInternal.Invoke(null, args);

                    LogType type = GetLogType((int)reflection.Mode.GetValue(entry));
                    if (wanted != null && !wanted.Contains(type))
                    {
                        continue;
                    }

                    filteredCount++;
                    if (filteredCount <= offset || logsArray.Count >= limit)
                    {
                        continue; // keep counting matches so the caller knows how many exist
                    }

                    string fullText = (string)reflection.Message.GetValue(entry) ?? string.Empty;
                    SplitMessage(fullText, out string message, out string stackTrace);
                    var logObject = new JObject
                    {
                        ["message"] = message,
                        ["type"] = type.ToString()
                    };

                    if (reflection.GetLinesAndMode != null)
                    {
                        object[] lineArgs = { i, 1, 0, string.Empty };
                        reflection.GetLinesAndMode.Invoke(null, lineArgs);
                        Match timestamp = TimestampPrefix.Match(lineArgs[3] as string ?? string.Empty);
                        if (timestamp.Success)
                        {
                            logObject["timestamp"] = timestamp.Groups[1].Value;
                        }
                    }
                    if (reflection.GetEntryCount != null)
                    {
                        int count = (int)reflection.GetEntryCount.Invoke(null, new object[] { i });
                        if (count > 1)
                        {
                            logObject["count"] = count; // repeats merged by the Console's Collapse option
                        }
                    }

                    string file = (string)reflection.File.GetValue(entry);
                    if (!string.IsNullOrEmpty(file))
                    {
                        logObject["file"] = file;
                        logObject["line"] = (int)reflection.Line.GetValue(entry);
                    }

                    if (includeStackTrace)
                    {
                        logObject["stackTrace"] = stackTrace ?? string.Empty;
                    }
                    logsArray.Add(logObject);
                }
            }
            finally
            {
                reflection.EndGettingEntries.Invoke(null, null);
                if (searchOverridden)
                {
                    reflection.SetFilteringText.Invoke(null, new object[] { savedSearch });
                }
                if (flagsOverridden)
                {
                    reflection.ConsoleFlags.SetValue(null, savedFlags);
                }
            }

            return new JObject
            {
                ["logs"] = logsArray,
                ["_totalCount"] = totalCount,
                ["_filteredCount"] = filteredCount,
                ["_returnedCount"] = logsArray.Count
            };
        }

        private static LogType GetLogType(int mode)
        {
            // Order matters: an exception also carries the error bit.
            if ((mode & ModeScriptingException) != 0) return LogType.Exception;
            if ((mode & (ModeAssert | ModeScriptingAssertion)) != 0) return LogType.Assert;
            if ((mode & ModeMaskError) != 0) return LogType.Error;
            if ((mode & ModeMaskWarning) != 0) return LogType.Warning;
            return LogType.Log;
        }

        // A managed frame such as "UnityEngine.Debug:LogWarning (object)" or
        // "Some.Type/<>c__DisplayClass1_0:<Method>b__0 ()": no space before or right after the colon.
        private static readonly Regex ManagedFrame = new Regex(@"^[\w.`<>\[\],+/]+:[\w.`<>$]+ ?\(");

        /// <summary>
        /// A Console entry's text is the message followed by its stack trace. The trace starts at the first
        /// line (after the first) that looks like a managed or native stack frame.
        /// </summary>
        private static void SplitMessage(string fullText, out string message, out string stackTrace)
        {
            string[] lines = fullText.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                bool isFrame = line.StartsWith("at ", StringComparison.Ordinal)
                               || line.StartsWith("0x", StringComparison.Ordinal)
                               || line.Contains("(at ")
                               || ManagedFrame.IsMatch(line);
                if (isFrame)
                {
                    message = string.Join("\n", lines, 0, i).TrimEnd();
                    stackTrace = string.Join("\n", lines, i, lines.Length - i).TrimEnd();
                    return;
                }
            }
            message = fullText.TrimEnd();
            stackTrace = null;
        }

        /// <summary>Turns on the Console's severity toggles and timestamps for the read.</summary>
        private static bool TryForceReadFlags(ConsoleReflection reflection, out int savedFlags)
        {
            savedFlags = 0;
            if (reflection.ConsoleFlags == null)
            {
                return false;
            }
            savedFlags = (int)reflection.ConsoleFlags.GetValue(null);
            int forced = savedFlags | ConsoleFlagsForRead;
            if (forced == savedFlags)
            {
                return false;
            }
            reflection.ConsoleFlags.SetValue(null, forced);
            return true;
        }

        /// <summary>Clears the Console search box for the read; only when it can be restored afterwards.</summary>
        private static bool TryClearSearch(ConsoleReflection reflection, out string savedSearch)
        {
            savedSearch = null;
            if (reflection.SetFilteringText == null || reflection.GetFilteringText == null)
            {
                return false;
            }
            savedSearch = reflection.GetFilteringText.Invoke(null, null) as string;
            if (string.IsNullOrEmpty(savedSearch))
            {
                return false;
            }
            reflection.SetFilteringText.Invoke(null, new object[] { string.Empty });
            return true;
        }

        private static ConsoleReflection GetReflection()
        {
            return _reflection ?? (_reflection = ConsoleReflection.TryCreate());
        }

        /// <summary>Reflection handles for the internal Console API, resolved once.</summary>
        private sealed class ConsoleReflection
        {
            public Type LogEntryType;
            public MethodInfo StartGettingEntries;
            public MethodInfo EndGettingEntries;
            public MethodInfo GetEntryInternal;
            public MethodInfo GetCount;
            public FieldInfo Mode;
            public FieldInfo Message;
            public FieldInfo File;
            public FieldInfo Line;
            // Optional: timestamps, collapse counts, and neutralizing the Console window's own filters.
            public MethodInfo GetLinesAndMode;
            public MethodInfo GetEntryCount;
            public PropertyInfo ConsoleFlags;
            public MethodInfo GetFilteringText;
            public MethodInfo SetFilteringText;

            public static ConsoleReflection TryCreate()
            {
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Assembly editor = typeof(EditorApplication).Assembly;
                Type logEntries = editor.GetType("UnityEditor.LogEntries");
                Type logEntry = editor.GetType("UnityEditor.LogEntry");
                if (logEntries == null || logEntry == null)
                {
                    return null;
                }

                var reflection = new ConsoleReflection
                {
                    LogEntryType = logEntry,
                    StartGettingEntries = logEntries.GetMethod("StartGettingEntries", staticFlags),
                    EndGettingEntries = logEntries.GetMethod("EndGettingEntries", staticFlags),
                    GetEntryInternal = logEntries.GetMethod("GetEntryInternal", staticFlags),
                    GetCount = logEntries.GetMethod("GetCount", staticFlags),
                    Mode = logEntry.GetField("mode", instanceFlags),
                    Message = logEntry.GetField("message", instanceFlags),
                    File = logEntry.GetField("file", instanceFlags),
                    Line = logEntry.GetField("line", instanceFlags),
                    GetLinesAndMode = logEntries.GetMethod("GetLinesAndModeFromEntryInternal", staticFlags),
                    GetEntryCount = logEntries.GetMethod("GetEntryCount", staticFlags),
                    ConsoleFlags = logEntries.GetProperty("consoleFlags", staticFlags),
                    GetFilteringText = logEntries.GetMethod("GetFilteringText", staticFlags),
                    SetFilteringText = logEntries.GetMethod("SetFilteringText", staticFlags)
                };

                bool complete = reflection.StartGettingEntries != null && reflection.EndGettingEntries != null
                                && reflection.GetEntryInternal != null && reflection.GetCount != null
                                && reflection.Mode != null && reflection.Message != null
                                && reflection.File != null && reflection.Line != null;
                if (!complete)
                {
                    Debug.LogError("[MCP Unity] Could not access the Unity Console via UnityEditor.LogEntries; get_console_logs will fail.");
                    return null;
                }
                return reflection;
            }
        }
    }
}
