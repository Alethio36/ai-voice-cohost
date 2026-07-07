# Twitch AI Voice Co-Host (Ollama + ComfyUI TTS + OBS)

A single [Streamer.bot](https://streamer.bot) inline C# action that turns a Twitch
chat command into spoken audio in OBS:

```
!ai <message>  ->  Ollama (LLM reply)  ->  ComfyUI (text-to-speech mp3)  ->  OBS plays it
```

It also publishes a few facts about each response (the text, word/char counts,
audio duration, file path) as **persisted global variables** so other actions —
for example a mouth-flap animation — can react to them.

Everything runs **locally**. No cloud APIs, no keys.


***As a disclaimer this project was in part brought to life with the use of AI/LLM***

---

## Scope

This project covers **only the LLM + text-to-speech + playback** half.

It does **not** cover avatar / mouth animation. Animation has too many valid
approaches (image swap, VTube Studio, rigged models, etc.) to bundle one here.
Instead, the pipeline exposes the data an animation action needs (see
[Published variables](#published-variables)) and leaves the animation to you.

---

## Requirements

| Thing | Version / note |
|---|---|
| OS | Windows (paths in the code are Windows-style) |
| [Streamer.bot](https://streamer.bot) | Recent release |
| [OBS Studio](https://obsproject.com) | 28+ (this was built on 32.1.2) with **obs-websocket v5** (built into OBS 28+) |
| [Ollama](https://ollama.com) | For the LLM |
| [ComfyUI](https://github.com/comfyanonymous/ComfyUI) | For text-to-speech |
| [TTS Audio Suite](https://github.com/diodiogod/TTS-Audio-Suite) | ComfyUI custom node pack (provides the TTS nodes + example voices) |

A CUDA-capable NVIDIA GPU is strongly recommended for ComfyUI TTS. CPU works but
is slow.

---

## Repo contents

| File | Purpose |
|---|---|
| `ai_tts_pipeline.cs` | The Streamer.bot inline C# action |
| `slime_speech_workflow.json` | The ComfyUI TTS workflow (API format) the action drives |
| `README.md` | This file |

---

## Install

### 1. Install Ollama and pull a model

1. Download and install Ollama from <https://ollama.com/download>.
2. Ollama runs as a background service on `http://127.0.0.1:11434` after install.
3. Pull the default model (open Command Prompt or PowerShell):

   ```
   ollama pull llama3.2:3b
   ```

4. Confirm it works:

   ```
   ollama run llama3.2:3b "say hi"
   ```

   You should get a short reply. Type `/bye` to exit.

**Picking a different model (optional).** `llama3.2:3b` is a good default: small,
fast, low VRAM, fine for short co-host lines. If you want different behaviour:

- Bigger / smarter, more VRAM & slower: `llama3.1:8b`, `qwen2.5:7b`.
- Smaller / faster, less capable: `llama3.2:1b`, `qwen2.5:3b`.
- Browse models at <https://ollama.com/library>.

Whatever you pull, put its exact tag in the `OllamaModel` config value (below).

### 2. Install ComfyUI

Follow the official ComfyUI install for Windows:
<https://github.com/comfyanonymous/ComfyUI#installing>. The portable build is the
easiest.

Launch ComfyUI once and confirm the web UI loads in your browser (default:
<http://127.0.0.1:8188>).

### 3. Install the TTS Audio Suite node pack

Easiest via **ComfyUI Manager**:

1. Install ComfyUI Manager if you don't have it:
   <https://github.com/ltdrdata/ComfyUI-Manager>.
2. In ComfyUI, open **Manager -> Custom Nodes Manager**, search
   **"TTS Audio Suite"**, install it, then **restart ComfyUI**.

Or manually:

```
cd ComfyUI\custom_nodes
git clone https://github.com/diodiogod/TTS-Audio-Suite.git
```

then install its requirements into ComfyUI's Python environment (see that repo's
README) and restart ComfyUI.

**Models auto-download on first use** — the first TTS generation will be slow
while ChatterBox downloads. This is normal. The built-in example voices
(including `voices_examples/Morgan_Freeman CC3.wav`) ship with the node pack.

### 4. Load the workflow into ComfyUI (optional sanity check)

Drag `slime_speech_workflow.json` into the ComfyUI canvas. You should see the
ChatterBox / Character Voices / Save Audio nodes wire up. You don't have to run
it manually — the Streamer.bot action sends it to ComfyUI's API — but loading it
once confirms the node pack is installed correctly. If any node shows red /
missing, the node pack isn't installed.

### 5. Check your ComfyUI port

The action talks to ComfyUI over HTTP and needs the right port.

- **Default is `8188`.** If the web UI loads at `http://127.0.0.1:8188`, you're on
  the default and `ComfyUrl` is already correct.
- To confirm or change it, look at how you launch ComfyUI. A `--port` flag
  overrides the default, e.g.:

  ```
  python main.py --port 8188
  ```

  If your launcher (or a `.bat` file) passes `--port 8000` or anything else, use
  **that** number in `ComfyUrl`.
- Whatever port loads the ComfyUI web UI in your browser **is** the port to use.

### 6. Set up OBS

1. Add a **Media Source** to your scene. Name it exactly **`TTS Audio`**
   (or pick your own name and change `ObsMediaSource` in the config).
2. In its properties: **uncheck Loop**. Leave the local file empty or pointing at
   any placeholder — the action overwrites it each run.
3. Make sure the source is **audible**: check the Audio Mixer shows `TTS Audio`
   and it isn't muted. Use **Advanced Audio Properties -> Audio Monitoring** if
   you want to hear it locally as well as on stream.
4. Enable **obs-websocket**: OBS -> **Tools -> WebSocket Server Settings ->
   Enable WebSocket server**. Note the port/password.
5. In **Streamer.bot -> Settings -> OBS**, add/confirm the OBS connection using
   that port/password. It should show connected. This action uses OBS connection
   index **0** (the first one); change `ObsConnection` if yours differs.

### 7. Set up the Streamer.bot action

1. In Streamer.bot, create a new **Action** (e.g. `AI Voice`).
2. Add one sub-action: **Core -> C# -> Execute C# Code**.
3. Paste the entire contents of `ai_tts_pipeline.cs` into the code editor.
4. Click **Find References**
5. **Edit the config block at the top of the code** (see
   [Configuration](#configuration)). At minimum you must set the two `CHANGEME`
   paths and your system prompt.
6. Compile (Streamer.bot compiles on save). Fix any reference errors. When ready, **Save and Compile** to exit
7. Add a **Command** trigger: create a command with keyword `!ai`, set it to pass
   the message, and point it at this action. The chat text after `!ai` arrives as
   the `rawInput` argument, which is what the action reads.

---

## Configuration

All settings live in the clearly-marked config block at the top of
`ai_tts_pipeline.cs`. **You must change the `CHANGEME` values.** A fresh Windows
system needs these reviewed:

| Constant | What to set | Must change? |
|---|---|---|
| `InputArg` | Streamer.bot arg holding the chat text. Default `rawInput` works for a standard `!ai` command. | Only if your command passes text under a different name |
| `OllamaUrl` | Ollama base URL. Default `http://127.0.0.1:11434`. | Rarely |
| `OllamaModel` | The exact model tag you pulled, e.g. `llama3.2:3b`. | If you use a different model |
| `SystemPrompt` | The AI's personality and rules. **Ships as a placeholder — replace it.** Keep length guidance here. | **Yes** |
| `OllamaMaxTokens` | Hard cap on generated tokens (bounds reply length *and* generation time). Default `100` ~= a short line. | Optional |
| `OllamaTimeoutSeconds` | Give up on a hung LLM call. Default `30`. | Optional |
| `ComfyUrl` | ComfyUI base URL. Default `http://127.0.0.1:8188`. See [step 5](#5-check-your-comfyui-port). | If your port differs |
| `ComfyOutputRoot` | **CHANGEME.** Full path to ComfyUI's `output` folder, e.g. `C:\ComfyUI\output`. | **Yes** |
| `WorkflowPath` | **CHANGEME.** Full path to `slime_speech_workflow.json` from this repo (put it anywhere and point here). | **Yes** |
| `VoiceMode` | `builtin` (use a shipped voice) or `custom` (clone from a local clip). Default `builtin`. | Optional |
| `VoiceValue` | For `builtin`: the exact voice name string (default is the shipped Morgan Freeman example). For `custom`: a local audio file path to clone. | Optional |
| `ComfyTimeoutSeconds` | Max wait for TTS generation. Default `120` (covers first-run model download). | Optional |
| `ObsMediaSource` | OBS Media Source name. Default `TTS Audio`. | If you named it differently |
| `ObsConnection` | Streamer.bot OBS connection index. Default `0`. | If you have multiple OBS connections |
| `ObsDurationTimeoutSeconds` | Max wait for OBS to report audio length. Default `5`. | Optional |

The **advanced** block below that (`TextNodeId`, `SaveNodeId`, etc.) only needs
changing if you edit the workflow's node IDs. Leave it alone otherwise.

### Choosing a voice

- **Built-in voice** (`VoiceMode = "builtin"`): set `VoiceValue` to a voice name
  the node pack provides, e.g. `voices_examples/Morgan_Freeman CC3.wav`. Browse
  the `voices_examples` folder inside the TTS Audio Suite node directory for the
  full list, and use the exact string.
- **Cloned voice** (`VoiceMode = "custom"`): set `VoiceValue` to a **local audio
  file path** on the machine (e.g. `C:\voices\myclip.wav`). The action uploads it
  to ComfyUI and clones it. A short, clean, single-speaker clip works best.

---

## Published variables

After each response, the action sets these **persisted global variables**.
Reference them in other actions with **tilde syntax** (`~name~`) — note this is a
tilde, not `%percent%`. Percent syntax does not resolve globals in Streamer.bot;
only persisted globals can be referenced directly, and only with `~`.

| Variable | Type | Meaning |
|---|---|---|
| `~aiResponse~` | string | The cleaned LLM reply text |
| `~aiWordCount~` | int | Word count of the reply |
| `~aiCharCount~` | int | Character count of the reply |
| `~aiDurationMs~` | int | Actual audio length in milliseconds (from OBS) |
| `~aiFilePath~` | string | Absolute path to the generated mp3 |

These exist so a separate action (e.g. mouth animation) can consume them. Because
they're persisted, they survive a Streamer.bot restart holding the **last**
value — if a downstream action can fire before the first `!ai` of a session,
guard against stale data.

---

## Usage

In Twitch chat:

```
!ai what game should I play tonight?
```

The bot generates a reply, speaks it in the Morgan Freeman voice (by default),
and plays it through the `TTS Audio` source in OBS.

---

## Troubleshooting

Failures are logged in the Streamer.bot log with a clear prefix:

- **`[AI] ...`** — the Ollama / LLM stage.
- **`[TTS] ...`** — the ComfyUI or OBS stage.

| Symptom | Likely cause |
|---|---|
| `[AI] No input text` | The command didn't pass the chat text as `rawInput`, or `InputArg` is wrong |
| `[AI] Ollama request failed` | Ollama isn't running, or `OllamaUrl` / model tag is wrong |
| `[TTS] ComfyUI unreachable` | ComfyUI isn't running, or `ComfyUrl` port is wrong ([step 5](#5-check-your-comfyui-port)) |
| `[TTS] Workflow not found` | `WorkflowPath` doesn't point at the JSON file |
| `[TTS] Node errors` | Workflow / node mismatch — is TTS Audio Suite installed and the workflow loading cleanly? |
| `[TTS] History reported ... but file is missing` | `ComfyOutputRoot` is wrong (pointing at the wrong folder) |
| `[TTS] OBS did not report a duration` | Wrong `ObsMediaSource` name, or the file failed to load in OBS |
| Audio generates but is silent | OBS audio routing — check the mixer / monitoring for `TTS Audio` |
| `~aiResponse~` prints literally | You used `%aiResponse%` — globals need `~` tilde syntax |

**First-run slowness:** the first `!ai` after starting ComfyUI downloads the TTS
model and can take a while. `ComfyTimeoutSeconds` (default 120) allows for this.

---

## Notes / limitations

- **Serial by design.** The pipeline blocks its Streamer.bot queue for the full
  generate-and-play cycle. Put this action on its own queue. A second `!ai` while
  one is running queues behind it rather than interrupting.
- **No hard length cap in code.** Reply length is governed by the system prompt
  and `OllamaMaxTokens`, not a character truncation.
- **`enable_audio_cache` + fixed seed** in the workflow means identical text
  replays identical audio. Fine for a co-host; change the seed in the workflow if
  you ever want variation on repeated text.
