using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

// =============================================================================
//  AI -> TTS -> OBS pipeline  (single Streamer.bot inline C# sub-action)
//
//  Flow:  !ai <text>  ->  Ollama LLM reply  ->  ComfyUI TTS (mp3)
//         ->  OBS media source plays it  ->  heuristics published as persisted
//         globals (reference downstream with tilde syntax, e.g. ~aiResponse~)
//
//  Downstream (e.g. mouth animation) lives in a SEPARATE action and reads the
//  session globals published at the end of this block. Nothing about the mouth
//  is handled here.
//
//  Required references (C# editor -> References):
//    System.Net.Http.dll
//    Newtonsoft.Json.dll
// =============================================================================

public class CPHInline
{
    // #########################################################################
    // #  END-USER CONFIG  -  edit these, nothing below should need touching   #
    // #########################################################################

    // ---- Trigger input ------------------------------------------------------
    // Streamer.bot arg holding the chat text after "!ai". Leave as rawInput
    // unless your command passes the text under a different argument name.
    private const string InputArg = "rawInput";

    // ---- Ollama (LLM) -------------------------------------------------------
    private const string OllamaUrl   = "http://127.0.0.1:11434";  // Ollama server base URL
    private const string OllamaModel = "llama3.2:3b";             // model tag to run

    // System prompt = the AI's personality / rules. PLACEHOLDER - replace this.
    // Keep length limits here (there is no hard character cap in code by design).
    private const string SystemPrompt =
        "PLACEHOLDER: You are a witty Twitch co-host. Keep replies to one short sentence.";

    // Hard ceiling on generated tokens. Bounds BOTH reply length and generation
    // time at the source. ~100 tokens ~= a short spoken line. Raise for longer
    // replies (and longer TTS + longer queue occupancy).
    private const int OllamaMaxTokens = 100;

    // Wall-clock timeout for the LLM call. If Ollama hangs, the request is
    // cancelled and the action fails cleanly instead of blocking the queue.
    private const int OllamaTimeoutSeconds = 30;

    // How long Ollama keeps the model warm in memory after a call.
    private const string OllamaKeepAlive = "24h";

    // ---- ComfyUI (TTS) ------------------------------------------------------
    // ComfyUI default port is 8188. See README "Check your ComfyUI port" if unsure.
    private const string ComfyUrl        = "http://127.0.0.1:8188";                     // ComfyUI server base URL
    private const string ComfyOutputRoot = @"C:\CHANGEME\ComfyUI\output";              // <-- CHANGEME: ComfyUI's \output folder
    private const string WorkflowPath    = @"C:\CHANGEME\slime_speech_workflow.json";  // <-- CHANGEME: path to the workflow JSON from this repo

    // Voice selection:
    //   builtin -> VoiceValue is a ComfyUI voice_name string (built-in voice)
    //   custom  -> VoiceValue is a LOCAL audio file path; it is uploaded to
    //              ComfyUI and cloned. Switch mode by changing these two lines.
    private const string VoiceMode  = "builtin";                                  // "builtin" | "custom"
    private const string VoiceValue = "voices_examples/Morgan_Freeman CC3.wav";   // see VoiceMode

    // Max seconds to wait for ComfyUI to finish generating the mp3 (cold model
    // load on first run can be slow; trim once warm if you want faster failure).
    private const int ComfyTimeoutSeconds = 120;

    // ---- OBS ----------------------------------------------------------------
    private const string ObsMediaSource = "TTS Audio";  // exact OBS Media Source input name
    private const int    ObsConnection  = 0;            // Streamer.bot OBS connection index

    // Max seconds to wait for OBS to report the media duration after playback
    // starts (duration is unavailable for a brief moment after the file loads).
    private const int ObsDurationTimeoutSeconds = 5;

    // #########################################################################
    // #  ADVANCED  -  ComfyUI node IDs. Only change if you edit the workflow. #
    // #########################################################################
    private const string TextNodeId      = "65";  // PrimitiveStringMultiline  (text to speak)
    private const string SaveNodeId      = "74";  // SaveAudioMP3              (output file)
    private const string VoiceNodeId     = "51";  // CharacterVoicesNode       (voice select)
    private const string LoadAudioNodeId = "26";  // LoadAudio                 (custom clip)

    // Poll interval (ms) for both ComfyUI history and OBS duration checks.
    private const int PollMs = 500;

    // #########################################################################
    // #  IMPLEMENTATION  -  no end-user edits needed below this line          #
    // #########################################################################

    // One shared HttpClient for the process (avoids socket exhaustion).
    private static readonly HttpClient Http = new HttpClient();

    public bool Execute()
    {
        // 1. Chat text that seeds the LLM. No fallback: empty input is an error.
        if (!CPH.TryGetArg(InputArg, out string chatText) || string.IsNullOrWhiteSpace(chatText))
        {
            CPH.LogError($"[AI] No input text (expected arg '{InputArg}').");
            return false;
        }

        // 2. LLM reply.
        string reply = GenerateReply(chatText);
        if (reply == null) return false;

        // 3. TTS -> mp3 path.
        string promptId = QueuePrompt(reply);
        if (promptId == null) return false;

        string filePath = WaitForOutput(promptId);
        if (filePath == null) return false;

        // 4. Play in OBS and measure real audio duration.
        int durationMs = PlayAndMeasure(filePath);
        if (durationMs < 0) return false;

        // 5. Publish heuristics for downstream actions to consume.
        PublishGlobals(reply, filePath, durationMs);
        return true;
    }

    // --- Ollama --------------------------------------------------------------

    // Send the chat text to Ollama, return the cleaned reply (or null on error).
    private string GenerateReply(string chatText)
    {
        var body = new JObject
        {
            ["model"]      = OllamaModel,
            ["system"]     = SystemPrompt,
            ["prompt"]     = chatText,
            ["stream"]     = false,
            ["keep_alive"] = OllamaKeepAlive,
            ["options"]    = new JObject { ["num_predict"] = OllamaMaxTokens }
        };

        string responseBody;
        try
        {
            var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(OllamaTimeoutSeconds)))
            {
                var resp = Http.PostAsync($"{OllamaUrl}/api/generate", content, cts.Token).Result;
                responseBody = resp.Content.ReadAsStringAsync().Result;

                if (!resp.IsSuccessStatusCode)
                {
                    CPH.LogError($"[AI] Ollama HTTP {(int)resp.StatusCode}: {responseBody}");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            // Refused connection, timeout/cancellation, DNS, etc. Clean message.
            CPH.LogError($"[AI] Ollama request failed: {ex.GetBaseException().Message}");
            return null;
        }

        string reply = (string)JObject.Parse(responseBody)["response"];
        if (string.IsNullOrWhiteSpace(reply))
        {
            CPH.LogError($"[AI] Ollama returned no response text: {responseBody}");
            return null;
        }

        // Collapse newlines/tabs to spaces so it reads as one spoken line.
        reply = reply.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        CPH.LogInfo($"[AI] Reply: {reply}");
        return reply;
    }

    // --- ComfyUI -------------------------------------------------------------

    // Build the workflow for the configured voice, POST it, return prompt_id.
    private string QueuePrompt(string text)
    {
        if (!File.Exists(WorkflowPath))
        {
            CPH.LogError($"[TTS] Workflow not found: {WorkflowPath}");
            return null;
        }

        JObject workflow = JObject.Parse(File.ReadAllText(WorkflowPath));
        workflow[TextNodeId]["inputs"]["value"] = text;

        var voiceInputs = (JObject)workflow[VoiceNodeId]["inputs"];
        if (VoiceMode == "builtin")
        {
            // Sever the reference-audio link so voice_name takes effect.
            voiceInputs.Remove("opt_audio_input");
            voiceInputs["voice_name"] = VoiceValue;
        }
        else if (VoiceMode == "custom")
        {
            // Upload the clip to ComfyUI, then point LoadAudio at it (link stays).
            string uploaded = UploadAudio(VoiceValue);
            if (uploaded == null) return null;
            workflow[LoadAudioNodeId]["inputs"]["audio"] = uploaded;
        }
        else
        {
            CPH.LogError($"[TTS] Invalid VoiceMode '{VoiceMode}' (expected builtin|custom).");
            return null;
        }

        var payload = new JObject { ["prompt"] = workflow, ["client_id"] = "streamerbot" };

        string body;
        try
        {
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            var resp    = Http.PostAsync($"{ComfyUrl}/prompt", content).Result;
            body        = resp.Content.ReadAsStringAsync().Result;

            if (!resp.IsSuccessStatusCode)
            {
                CPH.LogError($"[TTS] /prompt HTTP {(int)resp.StatusCode}: {body}");
                return null;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[TTS] ComfyUI unreachable at {ComfyUrl}: {ex.GetBaseException().Message}");
            return null;
        }

        JObject result = JObject.Parse(body);
        if (result["node_errors"] is JObject errs && errs.HasValues)
        {
            CPH.LogError($"[TTS] Node errors: {errs}");
            return null;
        }
        return (string)result["prompt_id"];
    }

    // POST a local audio file to ComfyUI's input dir, return its stored filename.
    private string UploadAudio(string localPath)
    {
        if (!File.Exists(localPath))
        {
            CPH.LogError($"[TTS] Voice clip not found: {localPath}");
            return null;
        }

        try
        {
            using (var form = new MultipartFormDataContent())
            {
                var fileContent = new ByteArrayContent(File.ReadAllBytes(localPath));
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(fileContent, "image", Path.GetFileName(localPath)); // endpoint field is "image"; handles audio
                form.Add(new StringContent("input"), "type");
                form.Add(new StringContent("true"), "overwrite");            // stable filename across runs

                var resp = Http.PostAsync($"{ComfyUrl}/upload/image", form).Result;
                string body = resp.Content.ReadAsStringAsync().Result;

                if (!resp.IsSuccessStatusCode)
                {
                    CPH.LogError($"[TTS] /upload HTTP {(int)resp.StatusCode}: {body}");
                    return null;
                }

                JObject r        = JObject.Parse(body);
                string name      = (string)r["name"];
                string subfolder = (string)r["subfolder"] ?? "";
                return string.IsNullOrEmpty(subfolder) ? name : $"{subfolder}/{name}";
            }
        }
        catch (Exception ex)
        {
            CPH.LogError($"[TTS] Upload failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // Poll /history until the save node reports its file, return absolute path.
    private string WaitForOutput(string promptId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ComfyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            string body;
            try
            {
                body = Http.GetStringAsync($"{ComfyUrl}/history/{promptId}").Result;
            }
            catch (Exception ex)
            {
                CPH.LogError($"[TTS] History poll failed: {ex.GetBaseException().Message}");
                return null;
            }

            JObject hist = JObject.Parse(body);
            if (hist[promptId] is JObject entry)
            {
                if ((string)entry["status"]?["status_str"] == "error")
                {
                    CPH.LogError($"[TTS] Execution error: {entry["status"]?["messages"]}");
                    return null;
                }

                JToken audio = entry["outputs"]?[SaveNodeId]?["audio"]?[0];
                if (audio != null)
                {
                    string filename  = (string)audio["filename"];
                    string subfolder = (string)audio["subfolder"] ?? "";
                    string path      = Path.Combine(ComfyOutputRoot, subfolder, filename);

                    if (!File.Exists(path))
                    {
                        CPH.LogError($"[TTS] History reported '{path}' but file is missing.");
                        return null;
                    }
                    return path;
                }
            }
            Thread.Sleep(PollMs);
        }

        CPH.LogError($"[TTS] Timed out after {ComfyTimeoutSeconds}s waiting for output.");
        return null;
    }

    // --- OBS -----------------------------------------------------------------

    // Point the media source at the file, restart playback, return duration (ms).
    // Returns -1 on error.
    private int PlayAndMeasure(string filePath)
    {
        // Set the file, then restart so it plays from the top.
        var setSettings = new JObject
        {
            ["inputName"]     = ObsMediaSource,
            ["inputSettings"] = new JObject { ["local_file"] = filePath },
            ["overlay"]       = true
        };
        CPH.ObsSendRaw("SetInputSettings", setSettings.ToString(), ObsConnection);

        var restart = new JObject
        {
            ["inputName"]   = ObsMediaSource,
            ["mediaAction"] = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART"
        };
        CPH.ObsSendRaw("TriggerMediaInputAction", restart.ToString(), ObsConnection);

        // Duration is only readable once OBS has loaded and started the media,
        // so poll GetMediaInputStatus until it reports a non-zero duration.
        var statusReq = new JObject { ["inputName"] = ObsMediaSource };
        var deadline  = DateTime.UtcNow.AddSeconds(ObsDurationTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            string raw = CPH.ObsSendRaw("GetMediaInputStatus", statusReq.ToString(), ObsConnection);
            int duration = ExtractMediaDuration(raw);
            if (duration > 0) return duration;
            Thread.Sleep(PollMs);
        }

        CPH.LogError($"[TTS] OBS did not report a duration for '{ObsMediaSource}' within {ObsDurationTimeoutSeconds}s (wrong source name, or file failed to load).");
        return -1;
    }

    // Pull mediaDuration (ms) out of an ObsSendRaw response, tolerating whether
    // the payload is flat or nested under responseData. Returns 0 if absent or
    // JSON-null (OBS reports null until the media finishes loading), so the
    // caller keeps polling instead of crashing.
    private int ExtractMediaDuration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        JObject o = JObject.Parse(raw);
        JToken d = o["mediaDuration"] ?? o["responseData"]?["mediaDuration"];
        return (int?)d ?? 0;  // (int?) yields null for JSON-null or absent tokens
    }

    // --- Output --------------------------------------------------------------

    // Publish heuristics as PERSISTED globals so downstream actions can reference
    // them directly in fields with tilde syntax (~aiResponse~, ~aiDurationMs~,
    // etc.) with no Get Global Variable sub-action. Persisted is required: only
    // persisted globals are field-referenceable. Each response overwrites them.
    private void PublishGlobals(string reply, string filePath, int durationMs)
    {
        int wordCount = reply.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;

        CPH.SetGlobalVar("aiResponse",  reply,          true); // cleaned reply text  -> ~aiResponse~
        CPH.SetGlobalVar("aiWordCount", wordCount,      true); // word count          -> ~aiWordCount~
        CPH.SetGlobalVar("aiCharCount", reply.Length,   true); // character count     -> ~aiCharCount~
        CPH.SetGlobalVar("aiDurationMs", durationMs,    true); // real audio len (ms) -> ~aiDurationMs~
        CPH.SetGlobalVar("aiFilePath",  filePath,       true); // absolute mp3 path   -> ~aiFilePath~

        CPH.LogInfo($"[AI] Published: {wordCount} words, {reply.Length} chars, {durationMs}ms audio.");
    }
}
