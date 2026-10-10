# Makes tools/chungus/voice_raw.wav: the words of the boss's one spoken line, from a Windows
# text-to-speech voice. Run once; the result is checked in, and sfx.py turns it into his voice
# (slower, far deeper, in a hall) when build.py runs.
#
#   pwsh tools\chungus\voice.ps1
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice('Microsoft David Desktop')
$synth.Rate = -3
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synth.SetOutputToWaveFile((Join-Path $PSScriptRoot 'voice_raw.wav'), $format)
$prompt = New-Object System.Speech.Synthesis.PromptBuilder
$prompt.AppendText('Perfectly balanced')
$prompt.AppendBreak([TimeSpan]::FromMilliseconds(380))
$prompt.AppendText('as all things should be.')
$synth.Speak($prompt)
$synth.SetOutputToNull()
$synth.Dispose()
