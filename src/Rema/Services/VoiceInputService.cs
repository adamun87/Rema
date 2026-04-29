using System.Runtime.Versioning;

namespace Rema.Services;

public sealed class VoiceInputService : IDisposable
{
    private object? _engine;
    private bool _isRecording;

    public event Action<string>? TextRecognized;

    public bool IsAvailable => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public void StartListening()
    {
        if (_isRecording) return;

        if (_engine is null)
        {
            try
            {
                var engine = new System.Speech.Recognition.SpeechRecognitionEngine();
                engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += (_, e) =>
                {
                    if (e.Result.Confidence > 0.3f)
                        TextRecognized?.Invoke(e.Result.Text);
                };
                _engine = engine;
            }
            catch
            {
                return;
            }
        }

        _isRecording = true;
        ((System.Speech.Recognition.SpeechRecognitionEngine)_engine).RecognizeAsync(
            System.Speech.Recognition.RecognizeMode.Multiple);
    }

    [SupportedOSPlatform("windows")]
    public void StopListening()
    {
        if (!_isRecording) return;
        _isRecording = false;
        (_engine as System.Speech.Recognition.SpeechRecognitionEngine)?.RecognizeAsyncCancel();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) StopListening();
        (_engine as IDisposable)?.Dispose();
    }
}
