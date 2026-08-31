# Speech2Issues

<p align="center">
  <a href="README.md"><strong>English</strong></a> · <a href="README.ru.md"><strong>Русский</strong></a>
</p>

Windows desktop app that records microphone and system audio with one button, mixes it into a mono WAV, transcribes speech locally with Whisper, sends the transcript to a selected AI provider, and creates tasks in PLANKA, GitHub Issues, Obsidian, or a webhook.

## Highlights

- Fully local speech recognition through [Whisper.net](https://github.com/sandrohanea/whisper.net).
- Lightweight, self-contained Windows x64 executable: no .NET installation is required for the published build.
- Animated first-run setup: CPU/CUDA runtime, Whisper model, and AI provider are downloaded or checked before the main window opens.
- Ollama, LM Studio, and any OpenAI-compatible API, with optional Bearer token support.
- Multi-project task routing, including safe PLANKA list selection and retryable delivery history.
- Global hotkey, tray mode, microphone/system-audio capture, editable task drafts, and local audio history.

## Requirements

- Windows 10 or 11 x64;
- .NET 8 SDK for development only;
- an available AI provider and at least one completion model;
- for GitHub Issues, GitHub CLI authentication (`gh auth login`) or a fine-grained PAT with **Issues: write** permission.

Whisper runs locally. On first launch, the setup wizard offers CPU or NVIDIA CUDA, a Whisper model, and an AI provider. The default multilingual `LargeV3Turbo` model is about 1.6 GB. Runtime libraries and models are downloaded atomically to `%LocalAppData%\Speech2Issues`; CUDA automatically falls back to CPU if its self-test fails.

Local service examples:

```powershell
Invoke-RestMethod http://127.0.0.1:11434/api/version
ollama list
Invoke-RestMethod http://127.0.0.1:1234/v1/models
```

## Getting started

```powershell
dotnet restore Speech2Issues.sln
dotnet run --project src/Speech2Issues.App
```

1. Complete the first-run wizard and select Ollama, LM Studio, or an OpenAI-compatible API. The wizard finishes only after a real provider check succeeds.
2. In **Settings → Audio and hotkey**, choose the microphone and system-audio device, then test Whisper. The same page controls how many idle minutes the loaded Whisper model remains in memory; `0` unloads it immediately and the default is 5 minutes.
3. Add shared service credentials in **Settings → Connections**.
4. Create a local project or import a detected project from PLANKA, GitHub, or Obsidian.
5. Enable one or more destinations for the project. For PLANKA, allow the target lists and select a fallback list.
6. Select the project and press the microphone button. After processing, there are 5 seconds to cancel, edit, or send immediately.

The full 16 kHz mono WAV is transcribed locally. Only the resulting transcript is sent to the AI provider for structured task generation; audio is never uploaded to the provider.

## AI providers

- **Ollama** — native `/api/tags`, `/api/show`, and `/api/chat` integration.
- **LM Studio** — OpenAI-compatible API, default URL `http://127.0.0.1:1234/v1`; a Bearer token is optional.
- **OpenAI-compatible API** — any HTTP/HTTPS base URL, model ID, and optional Bearer token.

Tokens are stored only in `secrets.bin`, encrypted with Windows DPAPI for the current user. If a server does not expose `/models`, its model ID can be entered manually.

## Projects and destinations

- Connections and credentials are shared; repositories, folders, PLANKA lists, and enabled destinations belong to each local project.
- One project can create a task in several destinations at once.
- For PLANKA, AI can choose only from the lists allowed by that project. Unknown IDs are replaced by the configured fallback list and can be corrected in the editor.
- Partial delivery is preserved: a retry from history sends only failed destinations.

Supported destinations:

- **PLANKA 2.1.1** — a card in an AI-selected or manually allowed list.
- **GitHub Issues** — per-project `owner/repo` and existing labels only.
- **Obsidian** — Project Manager, TaskNotes, Tasks, or plain Markdown profiles.
- **Webhook** — `POST` body `{ schemaVersion: 1, source, task }` with `Idempotency-Key`.

Every task includes a hidden `speech2issues:<id>` marker. Before retrying, the destination searches for that marker to prevent duplicates.

## Data and privacy

Application data is stored in `%LocalAppData%\Speech2Issues`:

- `settings.json` — non-secret settings;
- `secrets.bin` — API keys and tokens encrypted with Windows DPAPI;
- `history.db` — transcripts, delivery states, and links;
- `models\` — local Whisper GGML models;
- `runtime\` — downloaded Windows x64 CPU/CUDA Whisper libraries;
- `components.json` — installed component manifest;
- `recordings\` — WAV files kept after cancellation or a failed delivery for retry.

Successful audio is deleted. Secrets and audio are not written to logs.

## Build the distributable EXE

```powershell
.\build.ps1
```

The script restores packages, runs ordinary tests, and publishes a self-contained single-file executable to `publish\Speech2Issues.exe`. It fails if the executable exceeds 100 MB or Whisper runtime packages are accidentally bundled. Manual equivalent:

```powershell
dotnet publish src/Speech2Issues.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Tests

```powershell
dotnet test Speech2Issues.sln
```

Ordinary tests do not create external tasks. Live tests separately validate local Whisper on a sample WAV and task generation with an installed Ollama model:

```powershell
dotnet test tests/Speech2Issues.Tests --filter Category=Live
```

Whisper live tests use the small `Tiny` model. The application default is `LargeV3Turbo`.

Test real WASAPI capture without contacting an AI provider or creating a task:

```powershell
dotnet run --project tools/Speech2Issues.AudioSmoke -- 3
```

Transcribe a stored mono WAV without contacting AI or creating tasks:

```powershell
dotnet run --project tools/Speech2Issues.AudioSmoke -- --transcribe "C:\path\recording.wav" LargeV3Turbo
```
