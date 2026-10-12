using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Sound
    // =====================================================================================
    // Played with the Windows PlaySound function, not .NET's SoundPlayer: SoundPlayer refuses every WAV
    // that is not plain PCM, and the built-in sound is compressed (IMA ADPCM), which PlaySound handles.
    static class Chime
    {
        static GCHandle _builtIn;   // pinned for the whole run: PlaySound reads it after Play() has returned

        // assets/match-found.wav: "Successful accepted reward" by tithuh, from Pixabay (Pixabay Content
        // License: free to use inside a larger work, no attribution needed, never to be offered on its own;
        // docs/licenses/match-found-sound.txt). Mixed to mono; from 1.1 s it fades out faster than the original,
        // most of it right away and then gently (three bands, the high ringing losing most and the low tone least;
        // the original rings on for almost 5 s), ends at 2.2 s, and stored as IMA ADPCM (4 bits a sample, 44.1 kHz, 48 KB).
        public static byte[] BuiltInWav()
        {
            return AppFiles.Resource("match-found.wav");
        }

        public static void Play(string file)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file))
                {
                    string path = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                    if (File.Exists(path))
                    {
                        if (!Native.PlaySound(path, IntPtr.Zero, Native.SND_FILENAME | Native.SND_ASYNC | Native.SND_NODEFAULT))
                            AppLog.Write("Windows could not play the sound file: {0}", path);
                        return;
                    }
                    AppLog.Write("sound file not found, using the built-in sound: {0}", path);
                }
                if (!_builtIn.IsAllocated) _builtIn = GCHandle.Alloc(BuiltInWav(), GCHandleType.Pinned);
                if (!Native.PlaySound(_builtIn.AddrOfPinnedObject(), IntPtr.Zero, Native.SND_MEMORY | Native.SND_ASYNC | Native.SND_NODEFAULT))
                    AppLog.Write("Windows could not play the built-in sound");
            }
            catch (Exception ex) { AppLog.Write("could not play the sound: {0}", ex.Message); }
        }
    }
}
