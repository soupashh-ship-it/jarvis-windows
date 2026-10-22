# JARVIS for Windows

A Windows voice assistant with a local wake word and speech pipeline, Gemini and OpenAI-compatible model providers, and native desktop tools.

## Download

Get **Jarvis-1.0.1-win-x64.zip** from [GitHub Releases](https://github.com/soupashh-ship-it/jarvis-windows/releases/latest). Extract the ZIP to a folder and run Jarvis.exe. The package includes the Windows runtime and required wake-word models.

Windows may show a SmartScreen warning because this release is not signed with a trusted code-signing certificate.

## First run

JARVIS starts in local mode. Open **Settings** to choose a model provider. You can use a Gemini API key, a local Ollama or LM Studio endpoint, another supported provider, or Google Antigravity CLI sign-in.

To use Google login, install and sign in to the Antigravity CLI first, then select **Google Antigravity (Google login)** in JARVIS settings. Google says third-party applications that access Antigravity violate its terms and may lead to account suspension or termination. Review [Google's Antigravity FAQ](https://antigravity.google/docs/faq) before enabling this provider.

## Requirements

- Windows 10 21H2+ or Windows 11, 64-bit. Local speech recognition requires Windows 11 or Windows Server 2022+ and the [Microsoft Visual C++ 2022 x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).
- A microphone for voice input. Enable microphone access for desktop apps in Windows privacy settings.
- Internet access for cloud models and the first local speech-model download.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then run:

~~~powershell
.\build.ps1
.\dist\Jarvis\Jarvis.exe
~~~

The build restores pinned dependencies, builds and tests the solution, then publishes a self-contained win-x64 package. To add a Start-menu shortcut after building, run .\install.ps1.

## Privacy

Wake-word detection and speech transcription run locally. When a cloud provider is selected, prompts and any content needed for a request (such as screenshots or tool results) are sent to that provider. JARVIS does not include telemetry. Settings, encrypted API keys, notes, logs, and downloaded models are stored in your Windows user profile under %LOCALAPPDATA%\Jarvis; they are not part of the release ZIP.

## License

MIT. See LICENSE.


