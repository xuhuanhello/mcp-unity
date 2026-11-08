using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for controlling Unity play mode (play, pause, step)
    /// </summary>
    public class SetPlayModeStatusTool : McpToolBase
    {
        public SetPlayModeStatusTool()
        {
            Name = "set_play_mode_status";
            Description = "Controls Unity play mode. Actions: 'play', 'pause', 'stop', 'step'.";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters?["action"]?.ToString()?.ToLowerInvariant();
                
                if (string.IsNullOrEmpty(action))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter 'action'. Valid actions: 'play', 'pause', 'stop', 'step'",
                        "missing_parameter"
                    );
                }

                bool wasPlaying = EditorApplication.isPlaying;
                bool wasPaused = EditorApplication.isPaused;
                string transition = null; // set when a play mode change is still in progress

                switch (action)
                {
                    case "play":
                        if (!EditorApplication.isPlaying)
                        {
                            // Setting isPlaying fires ExitingEditMode synchronously, which stops this server and
                            // drops the connection before this response is sent: the client times out and the
                            // console logs "WebSocket error: An error has occurred in sending data". Enter Play
                            // mode on the next editor tick instead, after the response has gone out.
                            EditorApplication.update -= EnterPlayModeNextTick; // never queue it twice
                            EditorApplication.update += EnterPlayModeNextTick;
                            transition = "entering_play_mode";
                        }
                        else if (EditorApplication.isPaused)
                        {
                            // Unpause if already playing
                            EditorApplication.isPaused = false;
                        }
                        break;

                    case "pause":
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPaused = !EditorApplication.isPaused;
                        }
                        else
                        {
                            return McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot pause: Editor is not in play mode",
                                "invalid_state"
                            );
                        }
                        break;

                    case "stop":
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.isPlaying = false;
                            transition = "exiting_play_mode"; // isPlaying still reads true until the frame ends
                        }
                        break;

                    case "step":
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.Step();
                        }
                        else
                        {
                            return McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot step: Editor is not in play mode",
                                "invalid_state"
                            );
                        }
                        break;

                    default:
                        return McpUnitySocketHandler.CreateErrorResponse(
                            $"Invalid action '{action}'. Valid actions: 'play', 'pause', 'stop', 'step'",
                            "invalid_parameter"
                        );
                }

                bool isPlaying = EditorApplication.isPlaying;
                bool isPaused = EditorApplication.isPaused;
                string state = transition == "entering_play_mode" ? "Entering Play mode"
                    : transition == "exiting_play_mode" ? "Exiting Play mode"
                    : isPlaying ? (isPaused ? "Playing (paused)" : "Playing") : "Edit mode";

                var result = new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Action '{action}' executed. State: {state}",
                    ["action"] = action,
                    ["wasPlaying"] = wasPlaying,
                    ["wasPaused"] = wasPaused,
                    ["isPlaying"] = isPlaying,
                    ["isPaused"] = isPaused
                };
                if (transition != null)
                {
                    result["transition"] = transition;
                }

                McpLogger.LogInfo($"Play mode action '{action}' executed. isPlaying={isPlaying}, isPaused={isPaused}");

                return result;
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Error controlling play mode: {ex.Message}",
                    "play_mode_error"
                );
            }
        }

        private static void EnterPlayModeNextTick()
        {
            EditorApplication.update -= EnterPlayModeNextTick;
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = true;
            }
        }
    }
}
