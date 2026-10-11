// Bonk Mode - https://github.com/makesupply/bonk-mode
// Copyright 2026 makesupply. Licensed under the Apache License 2.0; see LICENSE and NOTICE.
// Derivative works must keep the NOTICE attribution.
// Bonk Mode keygen window v2. Compiled at runtime by bonk-mode.ps1 (C# 5, .NET Framework).
// One double-buffered GDI+ form with six screens; chiptune music and sound effects are
// synthesized square/triangle/noise streamed through winmm waveOut. No asset files.
// Keep this file ASCII-only (Windows PowerShell reads it as UTF-8 but be safe).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BonkMode
{
    // ================================================================== audio

    public sealed class ChipAudio : IDisposable
    {
        public const int Rate = 22050;
        const int NBuf = 4;
        const int BufSamples = 1024;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat
        {
            public short Tag; public short Channels; public int SamplesPerSec; public int AvgBytesPerSec;
            public short BlockAlign; public short BitsPerSample; public short CbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr Data; public int BufferLength; public int BytesRecorded; public IntPtr User;
            public int Flags; public int Loops; public IntPtr Next; public IntPtr Reserved;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        class Voice { public short[] Data; public int Pos; }

        IntPtr hwo = IntPtr.Zero;
        readonly IntPtr[] hdrs = new IntPtr[NBuf];
        readonly IntPtr[] datas = new IntPtr[NBuf];
        readonly List<Voice> voices = new List<Voice>();
        readonly short[] music;
        int musicPos;
        Thread thread;
        volatile bool running;
        public volatile bool MusicOn = true;
        public volatile bool SfxOn = true;
        public volatile int MusicVol = 60;

        public ChipAudio()
        {
            music = Synth.BuildMusic();
            WaveFormat fmt = new WaveFormat();
            fmt.Tag = 1; fmt.Channels = 1; fmt.SamplesPerSec = Rate; fmt.BitsPerSample = 16;
            fmt.BlockAlign = 2; fmt.AvgBytesPerSec = Rate * 2; fmt.CbSize = 0;
            if (waveOutOpen(out hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { hwo = IntPtr.Zero; return; }
            int hsz = Marshal.SizeOf(typeof(WaveHdr));
            for (int i = 0; i < NBuf; i++)
            {
                datas[i] = Marshal.AllocHGlobal(BufSamples * 2);
                WaveHdr h = new WaveHdr();
                h.Data = datas[i]; h.BufferLength = BufSamples * 2;
                hdrs[i] = Marshal.AllocHGlobal(hsz);
                Marshal.StructureToPtr(h, hdrs[i], false);
                waveOutPrepareHeader(hwo, hdrs[i], hsz);
            }
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        void Loop()
        {
            int hsz = Marshal.SizeOf(typeof(WaveHdr));
            int flagsOff = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");
            short[] tmp = new short[BufSamples];
            for (int i = 0; i < NBuf; i++) { Fill(tmp); Marshal.Copy(tmp, 0, datas[i], BufSamples); waveOutWrite(hwo, hdrs[i], hsz); }
            while (running)
            {
                for (int i = 0; i < NBuf; i++)
                {
                    int f = Marshal.ReadInt32(hdrs[i], flagsOff);
                    if ((f & 1) != 0)
                    {
                        Fill(tmp);
                        Marshal.Copy(tmp, 0, datas[i], BufSamples);
                        Marshal.WriteInt32(hdrs[i], flagsOff, f & ~1);
                        waveOutWrite(hwo, hdrs[i], hsz);
                    }
                }
                Thread.Sleep(8);
            }
        }

        void Fill(short[] buf)
        {
            int vol = MusicVol;
            lock (voices)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    int s = 0;
                    if (MusicOn) { s = music[musicPos] * vol / 120; if (++musicPos >= music.Length) musicPos = 0; }
                    for (int v = voices.Count - 1; v >= 0; v--)
                    {
                        Voice vo = voices[v];
                        s += vo.Data[vo.Pos++];
                        if (vo.Pos >= vo.Data.Length) voices.RemoveAt(v);
                    }
                    if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                    buf[i] = (short)s;
                }
            }
        }

        public void Play(short[] sfx)
        {
            if (hwo == IntPtr.Zero || sfx == null || !SfxOn) return;
            lock (voices) { if (voices.Count < 12) { Voice v = new Voice(); v.Data = sfx; voices.Add(v); } }
        }

        public void Dispose()
        {
            if (hwo == IntPtr.Zero) return;
            running = false;
            if (thread != null) thread.Join(500);
            waveOutReset(hwo);
            int hsz = Marshal.SizeOf(typeof(WaveHdr));
            for (int i = 0; i < NBuf; i++)
            {
                waveOutUnprepareHeader(hwo, hdrs[i], hsz);
                Marshal.FreeHGlobal(hdrs[i]);
                Marshal.FreeHGlobal(datas[i]);
            }
            waveOutClose(hwo);
            hwo = IntPtr.Zero;
        }
    }

    public static class Synth
    {
        const int R = ChipAudio.Rate;
        static readonly Random rng = new Random(1999);

        static double Hz(int midi) { return 440.0 * Math.Pow(2, (midi - 69) / 12.0); }

        static double Env(int i, int len, double sustain)
        {
            double a = Math.Min(1.0, i / (R * 0.003));
            double d = sustain + (1 - sustain) * Math.Exp(-i / (R * 0.06));
            int rel = (int)(len * 0.12);
            double r = i > len - rel ? (len - i) / (double)Math.Max(1, rel) : 1;
            return a * d * r;
        }

        static void Square(float[] b, int start, int len, double hz, double duty, double amp, bool vib)
        {
            double ph = 0, inc = hz / R;
            for (int i = 0; i < len && start + i < b.Length; i++)
            {
                double f = inc;
                if (vib && i > R * 0.12) f *= 1 + 0.008 * Math.Sin(2 * Math.PI * 6 * i / R);
                ph += f; if (ph >= 1) ph -= 1;
                b[start + i] += (float)((ph < duty ? 1 : -1) * amp * Env(i, len, 0.7));
            }
        }

        static void Triangle(float[] b, int start, int len, double hz, double amp)
        {
            double ph = 0, inc = hz / R;
            for (int i = 0; i < len && start + i < b.Length; i++)
            {
                ph += inc; if (ph >= 1) ph -= 1;
                double tri = Math.Round((1 - 4 * Math.Abs(ph - 0.5)) * 8) / 8;   // 16-step like the NES triangle
                b[start + i] += (float)(tri * amp * Env(i, len, 0.9));
            }
        }

        static void Noise(float[] b, int start, int len, double amp, double decay)
        {
            double last = 0;
            for (int i = 0; i < len && start + i < b.Length; i++)
            {
                if (i % 2 == 0) last = rng.NextDouble() * 2 - 1;
                b[start + i] += (float)(last * amp * Math.Exp(-i / (len * decay)));
            }
        }

        static void Kick(float[] b, int start, double amp)
        {
            int len = (int)(R * 0.12);
            double ph = 0;
            for (int i = 0; i < len && start + i < b.Length; i++)
            {
                double t = i / (double)R;
                ph += (40 + 110 * Math.Exp(-t * 30)) / R;
                b[start + i] += (float)(Math.Sin(2 * Math.PI * ph) * amp * Math.Exp(-t * 18));
            }
        }

        static short[] ToPcm(float[] b, float gainScale)
        {
            float peak = 0.0001f;
            foreach (float v in b) { float a = Math.Abs(v); if (a > peak) peak = a; }
            float gain = (peak > 0.95f ? 0.95f / peak : 1f) * gainScale;
            short[] o = new short[b.Length];
            for (int i = 0; i < b.Length; i++) o[i] = (short)(b[i] * gain * 32767);
            return o;
        }

        // Original 8-bar loop in A minor, 150 bpm: lead + echo, 16th arps, triangle bass, noise kit.
        public static short[] BuildMusic()
        {
            int s16 = (int)(R * 60.0 / 150 / 4);
            float[] b = new float[s16 * 128];
            int[][] lead = new int[][] {
                new[]{76,4}, new[]{81,2}, new[]{79,2}, new[]{76,2}, new[]{74,2}, new[]{72,2}, new[]{74,2},
                new[]{72,4}, new[]{69,2}, new[]{72,2}, new[]{77,4}, new[]{76,2}, new[]{72,2},
                new[]{76,2}, new[]{79,2}, new[]{76,2}, new[]{84,4}, new[]{83,2}, new[]{79,2}, new[]{76,2},
                new[]{74,6}, new[]{71,2}, new[]{74,2}, new[]{79,6},
                new[]{81,2}, new[]{81,2}, new[]{79,2}, new[]{81,2}, new[]{84,4}, new[]{83,2}, new[]{81,2},
                new[]{77,4}, new[]{76,2}, new[]{77,2}, new[]{81,4}, new[]{79,2}, new[]{77,2},
                new[]{79,4}, new[]{77,2}, new[]{76,2}, new[]{74,4}, new[]{71,2}, new[]{74,2},
                new[]{76,8}, new[]{80,4}, new[]{83,4}
            };
            int p = 0;
            foreach (int[] n in lead)
            {
                Square(b, p * s16, n[1] * s16, Hz(n[0]), 0.5, 0.15, n[1] >= 4);
                Square(b, (p + 3) * s16 % b.Length, n[1] * s16, Hz(n[0]), 0.25, 0.045, false);
                p += n[1];
            }
            int[][] chords = new int[][] {
                new[]{69,72,76}, new[]{65,69,72}, new[]{67,72,76}, new[]{67,71,74},
                new[]{69,72,76}, new[]{65,69,72}, new[]{67,71,74}, new[]{68,71,76}
            };
            int[] roots = { 45, 41, 48, 43, 45, 41, 43, 40 };
            for (int bar = 0; bar < 8; bar++)
            {
                int[] c = chords[bar];
                int[] arp = { c[0], c[1], c[2], c[0] + 12 };
                for (int s = 0; s < 16; s++)
                    Square(b, (bar * 16 + s) * s16, s16, Hz(arp[s % 4] + 12), 0.125, 0.045, false);
                for (int e = 0; e < 8; e++)
                {
                    int pos = (bar * 16 + e * 2) * s16;
                    Triangle(b, pos, 2 * s16, Hz(roots[bar] + (e % 2 == 1 ? 12 : 0)), 0.3);
                    Noise(b, pos, s16 / 2, 0.035, 0.25);
                    if (e == 0 || e == 4 || e == 5) Kick(b, pos, 0.45);
                    if (e == 2 || e == 6) Noise(b, pos, s16 * 2, 0.13, 0.3);
                }
            }
            return ToPcm(b, 1f);
        }

        public static short[] Bonk()
        {
            float[] b = new float[(int)(R * 0.32)];
            int sweep = (int)(R * 0.2);
            double ph = 0;
            for (int i = 0; i < sweep; i++)
            {
                double t = i / (double)sweep;
                ph += 600 * Math.Pow(80 / 600.0, t) / R; if (ph >= 1) ph -= 1;
                b[i] += (float)((ph < 0.5 ? 1 : -1) * 0.3 * (1 - t));
            }
            Noise(b, 0, (int)(R * 0.07), 0.35, 0.3);
            Kick(b, 0, 0.7);
            return ToPcm(b, 1f);
        }

        public static short[] Click()
        {
            float[] b = new float[(int)(R * 0.035)];
            Square(b, 0, b.Length, 1400, 0.5, 0.12, false);
            return ToPcm(b, 0.25f);
        }

        public static short[] Blip()
        {
            int half = (int)(R * 0.035);
            float[] b = new float[half * 2];
            Square(b, 0, half, 880, 0.25, 0.2, false);
            Square(b, half, half, 1320, 0.25, 0.2, false);
            return ToPcm(b, 0.3f);
        }

        public static short[] Whoosh()
        {
            float[] b = new float[(int)(R * 0.18)];
            double last = 0, ph = 0;
            for (int i = 0; i < b.Length; i++)
            {
                double t = i / (double)b.Length;
                if (i % (int)(2 + t * 10) == 0) last = rng.NextDouble() * 2 - 1;
                ph += (200 + 900 * t) / R; if (ph >= 1) ph -= 1;
                b[i] = (float)((last * 0.6 + (ph < 0.5 ? 0.2 : -0.2)) * Math.Sin(Math.PI * t));
            }
            return ToPcm(b, 0.25f);
        }

        public static short[] Parole()
        {
            int[] notes = { 72, 76, 79, 84, 88, 91 };
            int step = (int)(R * 0.055);
            float[] b = new float[step * notes.Length + step * 3];
            for (int i = 0; i < notes.Length; i++) Square(b, i * step, step * (i == notes.Length - 1 ? 4 : 1), Hz(notes[i]), 0.25, 0.2, false);
            return ToPcm(b, 0.5f);
        }

        public static short[] Slam()
        {
            float[] b = new float[(int)(R * 0.45)];
            Noise(b, 0, b.Length, 0.3, 0.35);
            Square(b, 0, (int)(R * 0.3), 55, 0.5, 0.25, false);
            Kick(b, 0, 0.8);
            return ToPcm(b, 0.66f);
        }

        public static short[] Alarm()
        {
            int half = (int)(R * 0.16);
            float[] b = new float[half * 4];
            for (int k = 0; k < 4; k++) Square(b, k * half, half, k % 2 == 0 ? 880 : 660, 0.5, 0.18, false);
            return ToPcm(b, 0.45f);
        }

        public static short[] Fanfare()
        {
            int[] notes = { 67, 72, 76, 79, 76, 79, 84 };
            int[] lens = { 1, 1, 1, 2, 1, 1, 5 };
            int step = (int)(R * 0.07);
            float[] b = new float[step * 14];
            int p = 0;
            for (int i = 0; i < notes.Length; i++) { Square(b, p * step, lens[i] * step, Hz(notes[i]), 0.5, 0.2, lens[i] > 2); p += lens[i]; }
            return ToPcm(b, 0.55f);
        }
    }

    // Per-process CPU time and disk bytes, read with the lightest access right Windows offers.
    public static class ProcStats
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
        const int QueryLimited = 0x1000;

        // Fills cpu (100ns units of kernel+user time) and io (bytes read+written); -1 where unreadable.
        public static void SampleMany(int[] pids, long[] cpu, long[] io)
        {
            for (int i = 0; i < pids.Length; i++)
            {
                cpu[i] = -1; io[i] = -1;
                IntPtr h = OpenProcess(QueryLimited, false, pids[i]);
                if (h == IntPtr.Zero) continue;
                try
                {
                    long c, e, k, u;
                    if (GetProcessTimes(h, out c, out e, out k, out u)) cpu[i] = k + u;
                    IoCounters ioc;
                    if (GetProcessIoCounters(h, out ioc)) io[i] = (long)(ioc.ReadBytes + ioc.WriteBytes);
                }
                finally { CloseHandle(h); }
            }
        }
    }
    // Global hotkeys (Ctrl+Alt+B / P / K) for the tray, without needing a visible window.
    public class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        public event Action<int> Pressed;
        readonly List<int> ids = new List<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        // mods: 1 Alt, 2 Ctrl, 4 Shift; 0x4000 = no auto-repeat
        public bool Register(int id, int mods, int vk)
        {
            bool ok = RegisterHotKey(Handle, id, mods | 0x4000, vk);
            if (ok) ids.Add(id);
            return ok;
        }

        public void UnregisterAll() { foreach (int id in ids) UnregisterHotKey(Handle, id); ids.Clear(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose() { UnregisterAll(); DestroyHandle(); }
    }
    // ================================================================== model

    public class AppInfo
    {
        public string Label, Names, Relaunch;
        public int MB, Procs, Hash, Number, Cpu10, IoKB;
        public bool Enabled, Running, Jailed, Suspect, Solitary;
        public int Escapes;
        public string Proc;
        public RectangleF Rect;
        public float Shake, Flash, Bars, ShownMB, Nervous;
        public List<int> History = new List<int>();
        public List<int> CpuHistory = new List<int>();
    }

    class Inmate
    {
        public AppInfo App;
        public float X = 70, Dir = 1, Speed = 30, Walk, BubbleT, NextBubble = 2, EscT;
        public string Bubble;
        public bool Escaping, Fire, Fired;
    }

    public class StartItem
    {
        public string Key, Name, Source, Command;
        public bool Enabled, Immune, Admin, Pending;
        public int MB;
        public float Bars;
    }

    class Particle { public float X, Y, VX, VY, Life; public string C; public Color Col; }
    class Pop { public float X, Y, Age; public string Text; }
    class Star { public float X, Y, Z; }
    class Hit { public RectangleF R; public string Id; public Hit(RectangleF r, string id) { R = r; Id = id; } }
    class Head { public float Y; public string Text, Right; public Color Col; }

    [StructLayout(LayoutKind.Sequential)]
    class MemStat
    {
        public uint Length = 64; public uint Load;
        public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt;
    }

    // ================================================================== window

    public class KeygenForm : Form
    {
        public event Action BonkClicked;
        public event Action ParoleClicked;
        public event Action EditClicked;
        public event Action UpdateClicked;
        public event Action<string, bool> ToggleClicked;
        public event Action<string> BonkOneClicked;
        public event Action<string> ParoleOneClicked;
        public event Action<string, string> SettingChanged;
        public event Action GameCancelClicked;
        public event Action<string, string> SuspectAction;
        public event Action<string, bool> StartupToggle;
        public event Action<string, bool> SolitaryToggled;
        public event Action StartupRefresh;

        public static bool NoAudio;

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemStat m);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        const int W = 960, H = 720;
        const int GridTop = 246, GridBottom = 586, BtnY = 614;
        const int JailTop = 186, JailBottom = 572;
        const int ListTop = 192, ListBottom = 600;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color Green = Color.FromArgb(0, 255, 120);
        static readonly Color DimGreen = Color.FromArgb(0, 110, 55);
        static readonly Color Red = Color.FromArgb(255, 60, 60);
        static readonly Color Gold = Color.FromArgb(255, 210, 0);
        static readonly string[] TabNames = { "HoMe", "JaiL", "DoSSieRS", "STaRTuP", "STaTS", "SeTTiNGS", "uPDaTeS", "GReeTZ" };
        const int ScrHome = 0, ScrJail = 1, ScrDossier = 2, ScrStartup = 3, ScrStats = 4, ScrSettings = 5, ScrUpdates = 6, ScrGreetz = 7;
        static readonly string[] PopWords = { "BoNK!", "WHaM!", "SMaSH!", "BoNK!", "K.O.!", "ByE!", "BoNK!", "PoW!", "CLaNK!" };
        static readonly string[] Bubbles = {
            "LeT Me ouT!", "i JuST WaNTeD To SYNC!", "1 MoRe uPDaTe...", "i KNoW MY RiGHTS!", "CaLL MY LaWYeR!",
            "iT WaS a BaCKGRouND JoB!", "i WaS oNLY 1 GB!", "WHo BoNKeD Me?!", "i'LL Be BaCK aT LoGiN", "You MiSS MY PoPuPS?",
            "i HaVe a NoTiFiCaTioN!", "THiS iS RaM aBuSe!", "TeLL MY CaCHe i LoVe iT"
        };
        static readonly string[] Crimes = {
            "synced 40 GB during a boss fight", "asked to update. again.", "held 1 GB of RAM hostage",
            "spawned helper processes nobody asked for", "opened a what's-new popup mid-raid", "phoned home 3,000 times",
            "rendered a widget no one has ever looked at", "kept the GPU awake for a tooltip", "auto-started without consent",
            "notification spam in the first degree", "loitering in the system tray", "resisting closure (X just minimizes)",
            "telemetry trafficking", "aggravated background indexing", "impersonating a lightweight app"
        };
        static readonly string[][] SettingRows = {
            new[] { "music", "bool", "Music", "The chiptune. 100% homemade square waves." },
            new[] { "volume", "slider", "Music volume", "How loud the keygen jams." },
            new[] { "sfx", "bool", "Sound FX", "Bonks, slams, clicks and parole jingles." },
            new[] { "shake", "bool", "Screen shake", "Every bonk rattles the window." },
            new[] { "scanlines", "bool", "CRT scanlines", "For that 1999 monitor glow." },
            new[] { "hammer", "cycle:fast,normal,dramatic", "Hammer speed", "How long each swing takes. Dramatic is very dramatic." },
            new[] { "polite", "cycle:2,4,8", "Polite warning", "Seconds apps get to close nicely before the hammer falls." },
            new[] { "gameMode", "cycle:off,ask,auto", "Auto game mode", "Bonk when a game starts, parole when it quits. Ask = 5 second countdown you can cancel." },
            new[] { "profile", "cycle:gaming,streaming,work,battery", "Profile", "Each profile remembers its own checkboxes. Streaming spares Discord and OBS; work spares office apps." },
            new[] { "powerPlan", "bool", "Turbo power plan", "Switch Windows to High performance while bonked; your old plan comes back on parole." },
            new[] { "radar", "bool", "Hog radar", "Flag heavy apps that are not on your list yet. Sweeps every 30 seconds." },
            new[] { "radarMB", "cycle:200,400,800,1500", "Radar threshold", "How much RAM an unlisted app needs before it becomes a suspect." },
            new[] { "solitary", "bool", "Solitary confinement", "Jailed apps marked SOLO get shut straight back down if they restart themselves. If you open one yourself, it's paroled instead." },
            new[] { "hotkeys", "bool", "Global hotkeys", "Ctrl+Alt+B bonks, Ctrl+Alt+P paroles, Ctrl+Alt+K opens this window. Works inside games." },
            new[] { "autostart", "bool", "Start with Windows", "Put the hammer in the tray at login." },
            new[] { "autoUpdate", "bool", "Check for updates", "Look for a new release on GitHub when this opens and every 6h." }
        };

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly List<AppInfo> apps = new List<AppInfo>();
        readonly List<AppInfo> homeOrder = new List<AppInfo>();
        readonly List<AppInfo> suspects = new List<AppInfo>();
        double radarPing = -1;
        readonly List<Head> homeHeads = new List<Head>();
        readonly Dictionary<string, Inmate> inmates = new Dictionary<string, Inmate>();
        readonly List<Particle> parts = new List<Particle>();
        readonly List<Pop> pops = new List<Pop>();
        readonly Star[] stars = new Star[150];
        readonly Random rnd = new Random();
        readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
        readonly Dictionary<int, Pen> pens = new Dictionary<int, Pen>();
        readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        List<Hit> zones = new List<Hit>(), zonesNext = new List<Hit>();

        ChipAudio audio;
        short[] sfxBonk, sfxClick, sfxBlip, sfxWhoosh, sfxParole, sfxSlam, sfxFanfare, sfxAlarm;
        Bitmap logo, overlay, frame, bricks, plasma;
        Font fSmall, fTiny, fName, fTileBig, fNfo, fNfoB, fBtn, fScroll, fBanner, fPop, fSub, fBig, fCount;
        StringFormat sfCenter, sfWrap, sfLeftMid;

        int screen;
        double t, openT, ramTimer, scrollX, transT, mascotT = 4, mascotSwing;
        float guardX = 100, guardDir = 1, tumbleX = -60;
        DateTime last = DateTime.Now;
        float shake, charW, homeContentH;
        int homeScroll, jailScroll, appsScroll, logScroll;
        List<AppInfo> hammerQueue = new List<AppInfo>();
        int hammerIdx = -1;
        double hammerT, hammerDur = 0.26;
        bool hammerHit, busy, inAdvance, shakeOn = true, scanOn = true;
        double bannerT = -1;
        string bannerText = "", bannerSub = "";
        object[] pending;
        string status;
        uint ramLoad; double ramFreeGB, ramTotalGB;
        int jailCount, escapes;
        string hoverId;
        Point mouse = new Point(-1, -1);
        string scrollText;
        string selected;
        string filter = "all";

        string localVersion = "?", remoteVersion, rawBase, repoUrl = "https://github.com/makesupply/bonk-mode";
        readonly List<string> changelog = new List<string>();
        string updStatus = "NoT CHeCKeD YeT";
        bool updChecking, updAvailable;
        DateTime lastCheck = DateTime.MinValue;
        int updPct = -1;
        string updMsg = "";
        string gameStatus = "aSK :: WaTCHiNG";
        int settingsScroll, startScroll, startJailed;
        readonly List<StartItem> startItems = new List<StartItem>();

        public KeygenForm()
        {
            Text = "BONK MODE :: RAM JAiL KeYGeN";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Color.Black;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            fSmall = new Font("Consolas", 8f);
            fTiny = new Font("Consolas", 7f, FontStyle.Bold);
            fName = new Font("Consolas", 9f, FontStyle.Bold);
            fTileBig = new Font("Consolas", 13f, FontStyle.Bold);
            fNfo = new Font("Consolas", 9f);
            fNfoB = new Font("Consolas", 9f, FontStyle.Bold);
            fBtn = new Font("Consolas", 10f, FontStyle.Bold);
            fScroll = new Font("Consolas", 16f, FontStyle.Bold);
            fBanner = new Font("Consolas", 40f, FontStyle.Bold);
            fPop = new Font("Consolas", 11f, FontStyle.Bold);
            fSub = new Font("Consolas", 9f);
            fBig = new Font("Consolas", 14f, FontStyle.Bold);
            fCount = new Font("Consolas", 24f, FontStyle.Bold);
            sfCenter = new StringFormat(); sfCenter.Alignment = StringAlignment.Center; sfCenter.LineAlignment = StringAlignment.Center;
            sfWrap = new StringFormat(); sfWrap.Trimming = StringTrimming.EllipsisWord;
            sfLeftMid = new StringFormat(); sfLeftMid.LineAlignment = StringAlignment.Center; sfLeftMid.Trimming = StringTrimming.EllipsisCharacter; sfLeftMid.FormatFlags = StringFormatFlags.NoWrap;

            for (int i = 0; i < stars.Length; i++) { stars[i] = new Star(); ResetStar(stars[i]); stars[i].Z = (float)rnd.NextDouble(); }
            logo = BuildLogo();
            overlay = BuildOverlay();
            bricks = BuildBricks();
            plasma = new Bitmap(96, 51, PixelFormat.Format32bppRgb);

            status = Leet("ready. pick your victims.");
            scrollText = "     *** BONK MODE v2  //  RAM JAiL KEYGEN ***   CRACKED BY THE BONK CREW ...   " +
                "CARDS UP TOP ARE GONNA GET BONKED, CLICK ONE TO SPARE IT ...   RIGHT-CLICK ANY CARD FOR ITS DOSSIER ...   " +
                "VISIT THE CELL BLOCK AND GRANT PAROLE ONE INMATE AT A TIME ...   THE UPDATER PULLS FRESH CRACKS STRAIGHT FROM GITHUB ...   " +
                "STEAM, YOUR GPU DRIVER AND WINDOWS HAVE DIPLOMATIC IMMUNITY ...   " +
                "GREETZ FLY OUT TO RAZOR 1911, FAIRLIGHT, RELOADED, CODEX, SKIDROW, SND, CPY AND EVERY KEYGEN MUSICIAN WHO MADE A 40KB TRACKER FILE SLAP ...   " +
                "NO GREETZ TO ADOBE CREATIVE CLOUD, YOU KNOW WHAT YOU DID ...   BONK MODE IS OPEN SOURCE UNDER APACHE 2.0, FORKS MUST CREDIT GITHUB.COM/MAKESUPPLY/BONK-MODE ...   [1-8] SCREENS  [B] BONK  CTRL+ALT+B BONKS FROM INSIDE A GAME  [P] PAROLE  [M] MUSIC  [ESC] EXIT ...   ";
            using (Graphics g = CreateGraphics())
                charW = g.MeasureString("MMMMMMMMMM", fScroll, PointF.Empty, StringFormat.GenericTypographic).Width / 10f;
            scrollX = W;

            settings["music"] = "True"; settings["volume"] = "60"; settings["sfx"] = "True"; settings["shake"] = "True";
            settings["scanlines"] = "True"; settings["hammer"] = "normal"; settings["polite"] = "4";
            settings["autostart"] = "False"; settings["autoUpdate"] = "True";
            settings["gameMode"] = "ask"; settings["radar"] = "True"; settings["radarMB"] = "400"; settings["profile"] = "gaming"; settings["powerPlan"] = "False"; settings["hotkeys"] = "True"; settings["solitary"] = "True";

            if (!NoAudio) { try { audio = new ChipAudio(); } catch { audio = null; } }
            sfxBonk = Synth.Bonk(); sfxClick = Synth.Click(); sfxBlip = Synth.Blip(); sfxWhoosh = Synth.Whoosh();
            sfxParole = Synth.Parole(); sfxSlam = Synth.Slam(); sfxFanfare = Synth.Fanfare(); sfxAlarm = Synth.Alarm();
            ReadRam();

            timer.Interval = 30;
            timer.Tick += delegate { Step(); Invalidate(); };
            timer.Start();
        }

        // ============================================================ public API (PowerShell side)

        public bool Busy { get { return busy || hammerIdx >= 0; } }

        public void SetBusy(bool on) { busy = on; if (!on) updPct = -1; }

        public void SetStatus(string s) { status = Leet(s ?? ""); }

        public void SetSetting(string key, string value)
        {
            settings[key] = value ?? "";
            ApplySetting(key);
        }

        public void SetVersion(string local, string raw, string repo)
        {
            localVersion = local; rawBase = raw;
            if (!string.IsNullOrEmpty(repo)) repoUrl = repo;
        }

        public void SetUpdateProgress(int pct, string msg) { updPct = pct; updMsg = Leet(msg ?? ""); }

        public void SetGameStatus(string s) { gameStatus = s ?? ""; }

        public void SetStartup(string[] keys, string[] names, string[] sources, string[] commands, bool[] enabled, bool[] immune, int[] mb, bool[] admin)
        {
            Dictionary<string, StartItem> old = new Dictionary<string, StartItem>();
            foreach (StartItem x in startItems) old[x.Key] = x;
            startItems.Clear();
            startJailed = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                StartItem s;
                if (!old.TryGetValue(keys[i], out s)) { s = new StartItem(); s.Key = keys[i]; s.Bars = enabled[i] ? 0 : 1; }
                s.Name = names[i]; s.Source = sources[i]; s.Command = commands[i]; s.Enabled = enabled[i];
                s.Immune = immune[i]; s.MB = mb[i]; s.Admin = admin[i]; s.Pending = false;
                if (!s.Enabled && !s.Immune) startJailed++;
                startItems.Add(s);
            }
        }

        public void ShowGameBanner(string game)
        {
            bannerText = "GaMe oN!"; bannerSub = Leet(game + " detected. clearing the room."); bannerT = 0;
            if (shakeOn) shake = 0.6f;
            Play(sfxFanfare);
        }

        public void ShowScreen(int s) { screen = Math.Max(0, Math.Min(TabNames.Length - 1, s)); transT = 0; }

        public void PreviewBonk() { StartBonk(null, false); }

        public void PreviewUpdate(string remote, string log) { FinishCheck(remote, log, null); }

        public void SetApps(string[] labels, int[] mb, bool[] enabled, bool[] running, bool[] jailed, int[] procs, string[] names, string[] relaunch, int[] cpu10, int[] iokb, bool[] solitary, int[] escapes)
        {
            if (hammerIdx >= 0) { pending = new object[] { labels, mb, enabled, running, jailed, procs, names, relaunch, cpu10, iokb, solitary, escapes }; return; }
            Dictionary<string, AppInfo> old = new Dictionary<string, AppInfo>();
            foreach (AppInfo x in apps) old[x.Label] = x;
            apps.Clear();
            jailCount = 0;
            for (int i = 0; i < labels.Length; i++)
            {
                AppInfo a;
                if (!old.TryGetValue(labels[i], out a))
                {
                    a = new AppInfo(); a.Label = labels[i];
                    a.Hash = StableHash(labels[i]);
                    a.Number = 1000 + (a.Hash % 9000);
                }
                a.MB = mb[i]; a.Enabled = enabled[i]; a.Running = running[i];
                a.Jailed = jailed[i] && !running[i];
                a.Procs = procs[i]; a.Names = names[i]; a.Relaunch = relaunch[i];
                a.Cpu10 = cpu10[i]; a.IoKB = iokb[i]; a.Solitary = solitary[i];
                if (old.ContainsKey(labels[i]) && escapes[i] > a.Escapes) EscapeFoiled(a);
                a.Escapes = escapes[i];
                if (a.Running)
                {
                    a.History.Add(a.MB); if (a.History.Count > 72) a.History.RemoveAt(0);
                    a.CpuHistory.Add(a.Cpu10); if (a.CpuHistory.Count > 72) a.CpuHistory.RemoveAt(0);
                }
                if (a.Jailed) jailCount++;
                apps.Add(a);
            }
            if (selected == null || Find(selected) == null) selected = apps.Count > 0 ? apps[0].Label : null;
            SyncInmates();
            LayoutHome();
        }

        public void Advance(double dt)
        {
            if (inAdvance) return;
            inAdvance = true;
            try { AdvanceCore(dt); } finally { inAdvance = false; }
        }

        // ============================================================ behavior

        void Step()
        {
            DateTime now = DateTime.Now;
            double dt = (now - last).TotalSeconds;
            last = now;
            if (dt > 0.1) dt = 0.1;
            Advance(dt);
        }

        void AdvanceCore(double dt)
        {
            float fdt = (float)dt;
            t += dt; openT += dt;
            transT = Math.Max(0, transT - dt);
            foreach (Star s in stars) { s.Z -= fdt * 0.3f; if (s.Z <= 0.02f) ResetStar(s); }
            foreach (AppInfo a in apps)
            {
                a.Shake = Math.Max(0, a.Shake - fdt * 2.5f);
                a.Flash = Math.Max(0, a.Flash - fdt * 3.5f);
                a.Nervous = Math.Max(0, a.Nervous - fdt);
                if (a.Jailed) a.Bars = Math.Min(1, a.Bars + fdt * 5); else a.Bars = Math.Max(0, a.Bars - fdt * 2.5f);
                a.ShownMB += (a.MB - a.ShownMB) * (float)Math.Min(1, dt * 5);
                if (IsTarget(a) && hammerIdx < 0 && rnd.NextDouble() < dt * 0.22) { a.Shake = Math.Max(a.Shake, 0.16f); a.Nervous = 0.9f; }
            }
            if (hammerIdx >= 0)
            {
                hammerT += dt / hammerDur;
                if (!hammerHit && hammerT >= 0.55) { hammerHit = true; Impact(hammerQueue[hammerIdx]); }
                if (hammerT >= 1)
                {
                    hammerIdx++; hammerT = 0; hammerHit = false;
                    if (hammerIdx >= hammerQueue.Count) FinishHammer();
                }
            }
            foreach (Inmate m in inmates.Values)
            {
                if (m.Escaping)
                {
                    m.EscT += fdt;
                    if (m.EscT > 0.3f) m.X += fdt * 320;
                    if (m.Fire && !m.Fired && m.EscT > 0.95f)
                    {
                        m.Fired = true;
                        string label = m.App.Label;
                        if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { if (ParoleOneClicked != null) ParoleOneClicked(label); }));
                    }
                    continue;
                }
                m.BubbleT -= fdt; m.NextBubble -= fdt;
                if (m.NextBubble <= 0) { m.Bubble = Bubbles[rnd.Next(Bubbles.Length)]; m.BubbleT = 2.4f; m.NextBubble = 5 + (float)rnd.NextDouble() * 9; }
                if (m.BubbleT <= 0 && m.App.Solitary) { m.X += (130 - m.X) * (float)Math.Min(1, dt * 2); }
                else if (m.BubbleT <= 0)
                {
                    m.X += m.Dir * m.Speed * fdt; m.Walk += fdt * 9;
                    if (m.X < 22) { m.X = 22; m.Dir = 1; }
                    if (m.X > 148) { m.X = 148; m.Dir = -1; }
                }
            }
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Particle p = parts[i];
                p.X += p.VX * fdt; p.Y += p.VY * fdt; p.VY += 700 * fdt; p.Life -= fdt;
                if (p.Life <= 0) parts.RemoveAt(i);
            }
            for (int i = pops.Count - 1; i >= 0; i--) { pops[i].Age += fdt; if (pops[i].Age > 0.75f) pops.RemoveAt(i); }
            if (bannerT >= 0) { bannerT += dt; if (bannerT > 2.2) bannerT = -1; }
            if (radarPing >= 0) { radarPing += dt; if (radarPing > 3) radarPing = -1; }
            foreach (StartItem s in startItems) { if (!s.Enabled && !s.Immune) s.Bars = Math.Min(1, s.Bars + fdt * 3); else s.Bars = Math.Max(0, s.Bars - fdt * 3); }
            shake = Math.Max(0, shake - fdt * 2.5f);
            scrollX -= dt * 140;
            if (scrollX < -scrollText.Length * charW) scrollX = W;
            mascotT -= dt;
            if (mascotT <= 0) { mascotT = 6 + rnd.NextDouble() * 5; mascotSwing = 1; }
            mascotSwing = Math.Max(0, mascotSwing - dt * 2.2);
            guardX += guardDir * 55 * fdt;
            if (guardX > W - 70) guardDir = -1; else if (guardX < 70) guardDir = 1;
            tumbleX += 90 * fdt; if (tumbleX > W + 60) tumbleX = -60;
            ramTimer -= dt;
            if (ramTimer <= 0) { ramTimer = 1; ReadRam(); }
        }

        void FinishHammer()
        {
            hammerIdx = -1; hammerQueue.Clear();
            bannerText = "RaM JaiLeD!"; bannerT = 0; shake = 1f;
            Play(sfxSlam);
            if (busy) status = Leet("waiting for stragglers...");
            if (pending != null)
            {
                object[] p = pending; pending = null;
                SetApps((string[])p[0], (int[])p[1], (bool[])p[2], (bool[])p[3], (bool[])p[4], (int[])p[5], (string[])p[6], (string[])p[7], (int[])p[8], (int[])p[9], (bool[])p[10], (int[])p[11]);
            }
            else { SyncInmates(); LayoutHome(); }
        }

        void Play(short[] s) { if (audio != null) audio.Play(s); }

        static bool IsTarget(AppInfo a) { return a.Running && a.Enabled && !a.Jailed; }

        AppInfo FindSuspect(string proc)
        {
            foreach (AppInfo a in suspects) if (a.Proc == proc) return a;
            return null;
        }

        public void SetSuspects(string[] procs, string[] labels, int[] mb, int[] count, int[] cpu10)
        {
            Dictionary<string, AppInfo> old = new Dictionary<string, AppInfo>();
            foreach (AppInfo x in suspects) old[x.Proc] = x;
            suspects.Clear();
            bool fresh = false;
            for (int i = 0; i < procs.Length; i++)
            {
                AppInfo a;
                if (!old.TryGetValue(procs[i], out a))
                {
                    a = new AppInfo(); a.Proc = procs[i]; a.Suspect = true; a.Running = true;
                    a.Hash = StableHash(procs[i]); a.Number = 1000 + (a.Hash % 9000); fresh = true;
                }
                a.Label = labels[i]; a.MB = mb[i]; a.Procs = count[i]; a.Names = procs[i]; a.Cpu10 = cpu10[i];
                suspects.Add(a);
            }
            if (fresh) { radarPing = 0; Play(sfxBlip); }
            if (hammerIdx < 0) LayoutHome();
        }

        void EscapeFoiled(AppInfo a)
        {
            Play(sfxAlarm);
            if (shakeOn) shake = Math.Max(shake, 0.5f);
            bannerText = a.Solitary ? "eSCaPe FoiLeD!" : "JaiLBReaK!";
            bannerSub = Leet(a.Solitary ? a.Label + " tried to sneak out. back in the hole." : a.Label + " restarted itself. mark it solo to stop that.");
            bannerT = 0;
            Inmate m;
            if (inmates.TryGetValue(a.Label, out m)) { m.Bubble = a.Solitary ? "BaCK iN THe HoLe..." : "i'M ouT!"; m.BubbleT = 3; }
        }

        AppInfo Find(string label)
        {
            foreach (AppInfo a in apps) if (a.Label == label) return a;
            return null;
        }

        void Go(int s)
        {
            if (s == screen) return;
            screen = s; transT = 0.28;
            Play(sfxWhoosh);
        }

        void StartBonk(string only, bool raise)
        {
            if (Busy) return;
            List<AppInfo> q = new List<AppInfo>();
            float mb = 0;
            foreach (AppInfo a in apps)
            {
                bool pick = only == null ? IsTarget(a) : (a.Label == only && a.Running && !a.Jailed);
                if (pick) { q.Add(a); mb += a.MB; }
            }
            if (q.Count == 0) { status = Leet(only == null ? "nothing to bonk. your ram is vibing." : "that one isn't running."); Play(sfxClick); return; }
            if (screen != 0) Go(0);
            homeScroll = 0;
            hammerQueue = q; hammerIdx = 0; hammerT = 0; hammerHit = false;
            bannerSub = "+" + FormatSize(mb) + " FReeD";
            status = Leet("bonking " + (q.Count == 1 ? q[0].Label : q.Count + " apps") + "...");
            if (raise)
            {
                busy = true;
                if (only == null) { if (BonkClicked != null) BonkClicked(); }
                else if (BonkOneClicked != null) BonkOneClicked(only);
            }
        }

        void StartParoleAll()
        {
            if (Busy) return;
            if (jailCount == 0) { status = Leet("jail is empty. nobody to release."); Play(sfxClick); return; }
            foreach (AppInfo a in apps) if (a.Jailed) { a.Jailed = false; a.Flash = 0.6f; }
            foreach (Inmate m in inmates.Values) { m.Escaping = true; m.Fire = false; }
            escapes += jailCount;
            jailCount = 0;
            Play(sfxParole);
            bannerText = "PaRoLe!"; bannerSub = Leet("they're free. for now."); bannerT = 0;
            status = Leet("granting parole...");
            busy = true;
            if (ParoleClicked != null) ParoleClicked();
        }

        void StartParoleOne(string label)
        {
            if (Busy) return;
            Inmate m;
            if (!inmates.TryGetValue(label, out m)) return;
            if (screen != 1) Go(1);
            m.Escaping = true; m.Fire = true; m.Bubble = "FReeDoM!"; m.BubbleT = 3;
            escapes++;
            Play(sfxParole);
            status = Leet("parole granted: " + label);
            busy = true;
        }

        void ToggleApp(AppInfo a)
        {
            if (a == null || a.Jailed || Busy) return;
            a.Enabled = !a.Enabled;
            a.Shake = 0.25f;
            Play(sfxClick);
            status = Leet((a.Enabled ? "sentenced: " : "spared: ") + a.Label);
            if (ToggleClicked != null) ToggleClicked(a.Label, a.Enabled);
            LayoutHome();
        }

        void Impact(AppInfo a)
        {
            a.Jailed = true; a.Shake = 1; a.Flash = 1;
            jailCount++;
            RectangleF r = HomeRect(a);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            const string bits = "01$#*@%&!";
            for (int i = 0; i < 24; i++)
            {
                Particle p = new Particle();
                p.X = cx; p.Y = cy;
                p.VX = (float)(rnd.NextDouble() * 540 - 270);
                p.VY = (float)(-120 - rnd.NextDouble() * 340);
                p.Life = (float)(0.5 + rnd.NextDouble() * 0.5);
                p.C = bits[rnd.Next(bits.Length)].ToString();
                p.Col = rnd.Next(3) == 0 ? Color.Yellow : Green;
                parts.Add(p);
            }
            Pop pop = new Pop(); pop.X = cx; pop.Y = cy - 6; pop.Text = PopWords[rnd.Next(PopWords.Length)];
            pops.Add(pop);
            if (shakeOn) shake = Math.Max(shake, 0.55f);
            Play(sfxBonk);
        }

        void SyncInmates()
        {
            List<string> gone = new List<string>();
            foreach (KeyValuePair<string, Inmate> kv in inmates)
            {
                AppInfo a = Find(kv.Key);
                if (a == null || !a.Jailed) gone.Add(kv.Key);
            }
            foreach (string k in gone) inmates.Remove(k);
            foreach (AppInfo a in apps)
            {
                if (!a.Jailed || inmates.ContainsKey(a.Label)) continue;
                Inmate m = new Inmate();
                m.App = a;
                m.X = 30 + (float)rnd.NextDouble() * 110;
                m.Dir = rnd.Next(2) == 0 ? -1 : 1;
                m.Speed = 18 + (float)rnd.NextDouble() * 26;
                m.NextBubble = 1 + (float)rnd.NextDouble() * 8;
                inmates[a.Label] = m;
            }
        }

        void LayoutHome()
        {
            homeOrder.Clear(); homeHeads.Clear();
            List<AppInfo> targets = new List<AppInfo>(), spared = new List<AppInfo>(), jailed = new List<AppInfo>();
            foreach (AppInfo a in apps)
            {
                if (a.Jailed) jailed.Add(a);
                else if (!a.Running) continue;
                else if (a.Enabled) targets.Add(a);
                else spared.Add(a);
            }
            float y = 0;
            if (suspects.Count > 0) y = Section(y, "NeW SuSPeCTS", suspects, Gold, "HeaVY aPPS NoT oN YouR LiST. aDD THeM oR SPaRe THeM FoReVeR.");
            y = Section(y, "GoNNa GeT BoNKeD", targets, Red, "THeSe CLoSe WHeN You HiT BoNK. CLiCK oNe To SPaRe iT.");
            y = Section(y, "SPaReD", spared, Green, "THeSe STaY oPeN. CLiCK oNe To SeNTeNCe iT.");
            if (jailed.Count > 0) y = Section(y, "iN JaiL", jailed, Color.FromArgb(170, 170, 185), "CLoSeD BY a BoNK. PaRoLe BRiNGS THeM BaCK.");
            homeContentH = y;
        }

        float Section(float y, string title, List<AppInfo> list, Color col, string hint)
        {
            const int tw = 142, th = 106, gap = 8, cols = 6;
            float x0 = 20 + (920 - (cols * (tw + gap) - gap)) / 2f;
            float mb = 0;
            foreach (AppInfo a in list) mb += a.MB;
            Head h = new Head();
            h.Y = y; h.Col = col;
            h.Text = title + " (" + list.Count + ")" + (list.Count > 0 && title != "iN JaiL" ? "  ::  " + FormatSize(mb) : "");
            if (title == "NeW SuSPeCTS" && radarPing >= 0 && radarPing < 1.5 && (int)(radarPing * 6) % 2 == 0) h.Col = Color.White;
            h.Right = Leet(hint);
            homeHeads.Add(h);
            y += 24;
            if (list.Count == 0) return y + 26;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Rect = new RectangleF(x0 + (i % cols) * (tw + gap), y + (i / cols) * (th + gap), tw, th);
                homeOrder.Add(list[i]);
            }
            return y + ((list.Count + cols - 1) / cols) * (th + gap) + 6;
        }

        RectangleF HomeRect(AppInfo a)
        {
            return new RectangleF(a.Rect.X, a.Rect.Y + GridTop - homeScroll, a.Rect.Width, a.Rect.Height);
        }

        void ApplySetting(string k)
        {
            string v = settings.ContainsKey(k) ? settings[k] : "";
            if (k == "music" && audio != null) audio.MusicOn = IsTrue(v);
            else if (k == "volume" && audio != null) { int n; if (int.TryParse(v, out n)) audio.MusicVol = Math.Max(0, Math.Min(100, n)); }
            else if (k == "sfx" && audio != null) audio.SfxOn = IsTrue(v);
            else if (k == "shake") shakeOn = IsTrue(v);
            else if (k == "scanlines") scanOn = IsTrue(v);
            else if (k == "hammer") hammerDur = v == "fast" ? 0.16 : v == "dramatic" ? 0.55 : 0.26;
        }

        void ChangeSetting(string k, string v)
        {
            settings[k] = v;
            ApplySetting(k);
            Play(sfxClick);
            if (SettingChanged != null) SettingChanged(k, v);
        }

        static bool IsTrue(string v) { return string.Equals(v, "True", StringComparison.OrdinalIgnoreCase); }

        void ReadRam()
        {
            MemStat m = new MemStat();
            if (GlobalMemoryStatusEx(m)) { ramLoad = m.Load; ramFreeGB = m.Avail / 1073741824.0; ramTotalGB = m.Total / 1073741824.0; }
        }

        void ResetStar(Star s) { s.X = (float)(rnd.NextDouble() * 2 - 1); s.Y = (float)(rnd.NextDouble() * 2 - 1); s.Z = 1; }

        // ------------------------------------------------------------ updates

        void StartUpdateCheck(bool manual)
        {
            if (updChecking || string.IsNullOrEmpty(rawBase)) return;
            updChecking = true;
            updStatus = "CHeCKiNG GiTHuB...";
            if (manual) Play(sfxBlip);
            string baseUrl = rawBase;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string v = null, log = null, err = null;
                try
                {
                    ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers["User-Agent"] = "BonkMode";
                        string bust = "?t=" + DateTime.UtcNow.Ticks;
                        v = wc.DownloadString(baseUrl + "VERSION" + bust).Trim();
                        try { log = wc.DownloadString(baseUrl + "CHANGELOG.md" + bust); } catch { log = ""; }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { FinishCheck(v, log, err); })); } catch { }
            });
        }

        void FinishCheck(string v, string log, string err)
        {
            updChecking = false;
            lastCheck = DateTime.Now;
            if (err != null) { updStatus = "oFFLiNe. GiTHuB DiDN'T aNSWeR."; return; }
            remoteVersion = v;
            changelog.Clear();
            if (!string.IsNullOrEmpty(log))
                foreach (string raw in log.Replace("\r", "").Split('\n'))
                {
                    // wrap long lines so nothing hides past the box edge
                    string line = raw;
                    string indent = line.StartsWith("- ") ? "  " : "";
                    while (line.Length > 110)
                    {
                        int cut = line.LastIndexOf(' ', 110);
                        if (cut < 20) cut = 110;
                        changelog.Add(line.Substring(0, cut));
                        line = indent + line.Substring(cut).TrimStart();
                    }
                    changelog.Add(line);
                }
            bool was = updAvailable;
            updAvailable = Newer(v, localVersion);
            updStatus = updAvailable ? "NeW CRaCK aVaiLaBLe: v" + v : "uP To DaTe. BoNK oN.";
            if (updAvailable && !was) { Play(sfxFanfare); status = Leet("update v" + v + " is out. see the updates screen."); }
        }

        static bool Newer(string remote, string local)
        {
            Version r, l;
            if (!Version.TryParse((remote ?? "").Trim(), out r)) return false;
            if (!Version.TryParse((local ?? "").Trim(), out l)) return true;
            return r > l;
        }

        // ============================================================ input

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (IsTrue(settings["autoUpdate"])) StartUpdateCheck(false);
        }

        string HitTest(Point p)
        {
            for (int i = zones.Count - 1; i >= 0; i--) if (zones[i].R.Contains(p)) return zones[i].Id;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            hoverId = HitTest(mouse);
            Cursor = hoverId != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1);
            hoverId = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || HitTest(e.Location) != null) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // drag the borderless window
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string id = HitTest(e.Location);
            if (id != null) HandleClick(id, e.Button);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int d = -e.Delta / 120 * 50;
            if (screen == 0) homeScroll += d;
            else if (screen == 1) jailScroll += d;
            else if (screen == ScrDossier && e.X < 480) appsScroll += d / 2;
            else if (screen == ScrUpdates) logScroll += d / 2;
            else if (screen == ScrSettings) settingsScroll += d;
            else if (screen == ScrStartup) startScroll += d / 2;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.M) ChangeSetting("music", IsTrue(settings["music"]) ? "False" : "True");
            else if (e.KeyCode == Keys.B) StartBonk(null, true);
            else if (e.KeyCode == Keys.P) StartParoleAll();
            else if (e.KeyCode >= Keys.D1 && e.KeyCode < Keys.D1 + TabNames.Length) Go(e.KeyCode - Keys.D1);
            else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode < Keys.NumPad1 + TabNames.Length) Go(e.KeyCode - Keys.NumPad1);
        }

        void HandleClick(string id, MouseButtons button)
        {
            if (id == "close") { Close(); return; }
            if (id == "min") { WindowState = FormWindowState.Minimized; return; }
            if (id.StartsWith("tab:")) { Go(int.Parse(id.Substring(4))); return; }
            if (id == "music") { ChangeSetting("music", IsTrue(settings["music"]) ? "False" : "True"); return; }
            if (id == "edit") { Play(sfxClick); if (EditClicked != null) EditClicked(); return; }
            if (id == "home") { Go(0); return; }
            if (id == "jail") { Go(1); return; }
            if (id == "bonk") { StartBonk(null, true); return; }
            if (id == "parole") { StartParoleAll(); return; }
            if (id.StartsWith("tile:"))
            {
                AppInfo a = Find(id.Substring(5));
                if (a == null) return;
                if (button == MouseButtons.Right) { selected = a.Label; filter = "all"; Go(ScrDossier); Play(sfxBlip); }
                else if (a.Jailed) { selected = a.Label; Go(1); }
                else ToggleApp(a);
                return;
            }
            if (id.StartsWith("toggle:")) { ToggleApp(Find(id.Substring(7))); return; }
            if (id.StartsWith("row:")) { selected = id.Substring(4); Play(sfxClick); return; }
            if (id.StartsWith("chip:")) { filter = id.Substring(5); appsScroll = 0; Play(sfxClick); return; }
            if (id.StartsWith("bonkone:")) { StartBonk(id.Substring(8), true); return; }
            if (id.StartsWith("paroleone:")) { StartParoleOne(id.Substring(10)); return; }
            if (id.StartsWith("set:"))
            {
                string[] p = id.Split(':');
                string key = p[1], act = p[2];
                if (act == "toggle") ChangeSetting(key, IsTrue(settings[key]) ? "False" : "True");
                else if (act == "val") ChangeSetting(key, p[3]);
                else if (act == "prev" || act == "next")
                {
                    string[] opts = CycleOptions(key);
                    int i = Array.IndexOf(opts, settings[key]);
                    i = (i + (act == "next" ? 1 : opts.Length - 1)) % opts.Length;
                    ChangeSetting(key, opts[i]);
                }
                return;
            }
            if (id.StartsWith("susadd:") || id.StartsWith("susspare:"))
            {
                bool add = id.StartsWith("susadd:");
                string proc = id.Substring(id.IndexOf(':') + 1);
                AppInfo s = FindSuspect(proc);
                if (s != null) { suspects.Remove(s); LayoutHome(); }
                Play(add ? sfxSlam : sfxClick);
                if (add && shakeOn) shake = 0.3f;
                status = Leet(add ? "added to the bonk list: " + (s != null ? s.Label : proc) : "spared forever: " + (s != null ? s.Label : proc));
                if (SuspectAction != null) SuspectAction(proc, add ? "add" : "spare");
                return;
            }
            if (id == "startup-refresh") { Play(sfxBlip); if (StartupRefresh != null) StartupRefresh(); return; }
            if (id.StartsWith("solitary:"))
            {
                AppInfo a = Find(id.Substring(9));
                if (a == null) return;
                a.Solitary = !a.Solitary;
                Play(a.Solitary ? sfxSlam : sfxClick);
                if (a.Solitary && shakeOn) shake = 0.3f;
                status = Leet(a.Solitary ? a.Label + " is in solitary. no more sneaking back." : a.Label + " is back in a normal cell.");
                if (SolitaryToggled != null) SolitaryToggled(a.Label, a.Solitary);
                return;
            }
            if (id.StartsWith("startup:"))
            {
                if (Busy) return;
                string key = id.Substring(8);
                StartItem s = null;
                foreach (StartItem x in startItems) if (x.Key == key) s = x;
                if (s == null || s.Immune) return;
                bool enable = !s.Enabled;
                s.Pending = true;
                Play(enable ? sfxParole : sfxSlam);
                if (!enable && shakeOn) shake = 0.35f;
                status = Leet((enable ? "paroling " : "jailing ") + s.Name + (enable ? " back into startup..." : " from startup...") + (s.Admin ? " (windows will ask for admin)" : ""));
                busy = true;
                if (StartupToggle != null) StartupToggle(key, enable);
                return;
            }
            if (id == "gamecancel") { Play(sfxClick); if (GameCancelClicked != null) GameCancelClicked(); return; }
            if (id == "upd:check") { StartUpdateCheck(true); return; }
            if (id == "upd:install")
            {
                if (Busy || !updAvailable) return;
                busy = true; updPct = 0; updMsg = Leet("warming up the patcher...");
                Play(sfxBlip);
                if (UpdateClicked != null) UpdateClicked();
                return;
            }
            if (id == "upd:repo") { try { Process.Start(repoUrl); } catch { } return; }
        }

        static string[] CycleOptions(string key)
        {
            foreach (string[] row in SettingRows)
                if (row[0] == key && row[1].StartsWith("cycle:")) return row[1].Substring(6).Split(',');
            return new string[] { "" };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (audio != null) { audio.Dispose(); audio = null; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (audio != null) { audio.Dispose(); audio = null; }
                foreach (Bitmap b in new[] { logo, overlay, frame, bricks, plasma }) if (b != null) b.Dispose();
                foreach (Font f in new[] { fSmall, fTiny, fName, fTileBig, fNfo, fNfoB, fBtn, fScroll, fBanner, fPop, fSub, fBig, fCount }) if (f != null) f.Dispose();
                foreach (SolidBrush br in brushes.Values) br.Dispose();
                foreach (Pen p in pens.Values) p.Dispose();
                brushes.Clear(); pens.Clear();
            }
            base.Dispose(disposing);
        }

        // ============================================================ painting

        SolidBrush B(Color c)
        {
            int k = c.ToArgb();
            SolidBrush b;
            if (brushes.TryGetValue(k, out b)) return b;
            if (brushes.Count > 600) { foreach (SolidBrush x in brushes.Values) x.Dispose(); brushes.Clear(); }
            b = new SolidBrush(c);
            brushes[k] = b;
            return b;
        }

        Pen P(Color c)
        {
            int k = c.ToArgb();
            Pen p;
            if (pens.TryGetValue(k, out p)) return p;
            if (pens.Count > 300) { foreach (Pen x in pens.Values) x.Dispose(); pens.Clear(); }
            p = new Pen(c);
            pens[k] = p;
            return p;
        }

        bool zonesOff;
        void Zone(RectangleF r, string id) { if (!zonesOff) zonesNext.Add(new Hit(r, id)); }
        bool Hot(string id) { return hoverId == id; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (frame == null) frame = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            zonesNext.Clear();
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                DrawStars(g);
                GraphicsState s0 = g.Save();
                if (shake > 0 && shakeOn) g.TranslateTransform((float)((rnd.NextDouble() * 2 - 1) * shake * 8), (float)((rnd.NextDouble() * 2 - 1) * shake * 6));
                DrawCopper(g);
                DrawLogo(g);
                DrawMascot(g);
                DrawSubtitle(g);
                DrawNav(g);
                switch (screen)
                {
                    case ScrHome: DrawHome(g); break;
                    case ScrJail: DrawJail(g); break;
                    case ScrDossier: DrawDossier(g); break;
                    case ScrStartup: DrawStartup(g); break;
                    case ScrStats: DrawStats(g); break;
                    case ScrSettings: DrawSettings(g); break;
                    case ScrUpdates: DrawUpdates(g); break;
                    default: DrawGreetz(g); break;
                }
                DrawHammer(g);
                DrawParticles(g);
                DrawPops(g);
                DrawBanner(g);
                g.Restore(s0);
                DrawScroller(g);
                if (scanOn) g.DrawImageUnscaled(overlay, 0, 0);
                g.DrawRectangle(P(DimGreen), 0, 0, W - 1, H - 1);
                SmallButton(g, new Rectangle(W - 72, 6, 30, 18), "_", "min");
                SmallButton(g, new Rectangle(W - 38, 6, 30, 18), "X", "close");
            }
            List<Hit> tmp = zones; zones = zonesNext; zonesNext = tmp;
            hoverId = HitTest(mouse);

            Graphics sg = e.Graphics;
            if (transT > 0)
            {
                // glitch wipe between screens
                sg.Clear(Color.Black);
                float k = (float)(transT / 0.28);
                int y = 0;
                while (y < H)
                {
                    int h = 4 + rnd.Next(26);
                    int dx = (int)((rnd.NextDouble() * 2 - 1) * 70 * k);
                    sg.DrawImage(frame, new Rectangle(dx, y, W, h), new Rectangle(0, y, W, h), GraphicsUnit.Pixel);
                    y += h;
                }
                sg.FillRectangle(B(Color.FromArgb((int)(70 * k), 255, 0, 200)), 0, 0, W, H);
            }
            else sg.DrawImageUnscaled(frame, 0, 0);
        }

        // ------------------------------------------------------------ shared chrome

        void DrawStars(Graphics g)
        {
            foreach (Star s in stars)
            {
                float px = W / 2 + s.X / s.Z * W / 2, py = H / 2 + s.Y / s.Z * H / 2;
                if (px < 0 || px >= W || py < 0 || py >= H) { ResetStar(s); continue; }
                float k = 1 - s.Z;
                int v = 60 + (int)(k * 195);
                int sz = 1 + (int)(k * 3);
                g.FillRectangle(B(Color.FromArgb(v, v, Math.Min(255, v + 20))), px, py, sz, sz);
            }
        }

        void DrawCopper(Graphics g)
        {
            for (int i = 0; i < 3; i++)
            {
                float y = (float)(56 + Math.Sin(t * 1.7 + i * 1.3) * 38);
                Color c = Hsv(t * 50 + i * 120, 0.9, 0.9);
                RectangleF r = new RectangleF(0, y, W, 16);
                using (LinearGradientBrush br = new LinearGradientBrush(r, Color.Black, Color.Black, LinearGradientMode.Vertical))
                {
                    ColorBlend cb = new ColorBlend();
                    cb.Colors = new[] { Color.FromArgb(0, c), Color.FromArgb(170, c), Color.FromArgb(255, Color.White), Color.FromArgb(170, c), Color.FromArgb(0, c) };
                    cb.Positions = new[] { 0f, 0.3f, 0.5f, 0.7f, 1f };
                    br.InterpolationColors = cb;
                    g.FillRectangle(br, r);
                }
            }
        }

        void DrawLogo(Graphics g)
        {
            int lx = (W - logo.Width) / 2, ly = 6;
            for (int x = 0; x < logo.Width; x += 3)
            {
                int dy = (int)(Math.Sin(t * 3.2 + x * 0.035) * 4);
                g.DrawImage(logo, new Rectangle(lx + x, ly + dy, 3, logo.Height), x, 0, 3, logo.Height, GraphicsUnit.Pixel);
            }
        }

        void DrawMascot(Graphics g)
        {
            float x = (W - logo.Width) / 2 - 70, y = (float)(66 + Math.Sin(t * 2.4) * 6);
            double swing = mascotSwing > 0 ? Math.Sin((1 - mascotSwing) * Math.PI) * 70 : 0;
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(x + 30, y + 10);
            g.ScaleTransform(0.55f, 0.55f);
            DrawHammerShape(g, (float)(-35 + swing), 255);
            // googly eyes on the hammer head
            g.RotateTransform((float)(-35 + swing));
            float look = (float)Math.Sin(t * 1.3) * 3;
            g.FillEllipse(Brushes.White, -94, -14, 12, 12); g.FillEllipse(Brushes.White, -80, -14, 12, 12);
            g.FillEllipse(Brushes.Black, -90 + look, -10, 5, 5); g.FillEllipse(Brushes.Black, -76 + look, -10, 5, 5);
            g.Restore(st);
            if (mascotSwing > 0.3) g.DrawString("HuP!", fTiny, B(Gold), x - 18, y - 22);
            string ver = "v" + localVersion + (updAvailable ? " (!)" : "");
            g.DrawString(ver, fTiny, B((int)(t * 2) % 2 == 0 || !updAvailable ? Green : Gold), (W + logo.Width) / 2 + 14, 74);
        }

        void DrawSubtitle(Graphics g)
        {
            string sub = "- = r a m   j a i l   k e y g e n   v 2 = -";
            SizeF sz = g.MeasureString(sub, fSub);
            float x = (W - sz.Width) / 2, y = 104;
            g.DrawLine(P(Green), 60, y + 8, x - 10, y + 8);
            g.DrawLine(P(Green), x + sz.Width + 10, y + 8, W - 60, y + 8);
            g.DrawString(sub, fSub, B(Green), x, y);
        }

        void DrawNav(Graphics g)
        {
            int n = TabNames.Length, tw = (920 - (n - 1) * 8) / n;
            for (int i = 0; i < n; i++)
            {
                Rectangle r = new Rectangle(20 + i * (tw + 8), 124, tw, 24);
                string id = "tab:" + i;
                string label = (i + 1) + " " + TabNames[i];
                if (i == ScrJail && jailCount > 0) label += " (" + jailCount + ")";
                if (i == ScrStartup && startJailed > 0) label += " (" + startJailed + ")";
                bool alert = i == ScrUpdates && updAvailable && (int)(t * 3) % 2 == 0;
                bool active = screen == i;
                Color bg = active ? Green : Hot(id) ? Color.FromArgb(0, 70, 35) : alert ? Color.FromArgb(120, 90, 0) : Color.FromArgb(0, 0, 0);
                g.FillRectangle(B(bg), r);
                g.DrawRectangle(P(active ? Green : DimGreen), r.X, r.Y, r.Width - 1, r.Height - 1);
                g.DrawString(label + (i == ScrUpdates && updAvailable ? " !" : ""), fNfoB, B(active ? Color.Black : alert ? Color.Black : Green), r, sfCenter);
                Zone(r, id);
            }
        }

        void Box(Graphics g, Rectangle box, string title)
        {
            g.FillRectangle(B(Color.FromArgb(205, 0, 0, 0)), box);
            g.DrawRectangle(P(DimGreen), box);
            g.DrawRectangle(P(DimGreen), box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6);
            if (string.IsNullOrEmpty(title)) return;
            string tt = " :: " + title + " :: ";
            SizeF ts = g.MeasureString(tt, fNfo);
            float tx = box.X + (box.Width - ts.Width) / 2;
            g.FillRectangle(Brushes.Black, tx, box.Y - 7, ts.Width, 14);
            g.DrawString(tt, fNfo, B(Green), tx, box.Y - 8);
        }

        void Button(Graphics g, RectangleF r, string text, string id, Color bg, Color fg, bool enabled)
        {
            bool hot = enabled && Hot(id);
            if (!enabled) { bg = Color.FromArgb(30, 30, 30); fg = Color.FromArgb(110, 110, 110); }
            g.FillRectangle(B(hot ? fg : bg), r);
            g.DrawRectangle(P(fg), r.X, r.Y, r.Width - 1, r.Height - 1);
            g.DrawString(text, fBtn, B(hot ? Color.Black : fg), r, sfCenter);
            if (enabled) Zone(r, id);
        }

        void SmallButton(Graphics g, Rectangle r, string text, string id)
        {
            bool hot = Hot(id);
            g.FillRectangle(B(hot ? Green : Color.Black), r);
            g.DrawRectangle(P(Green), r.X, r.Y, r.Width - 1, r.Height - 1);
            g.DrawString(text, fSmall, B(hot ? Color.Black : Green), r, sfCenter);
            Zone(r, id);
        }

        void Scrollbar(Graphics g, int x, int top, int bottom, float contentH, int scroll)
        {
            float viewH = bottom - top;
            if (contentH <= viewH) return;
            float barH = Math.Max(24, viewH * viewH / contentH);
            float y = top + (viewH - barH) * scroll / (contentH - viewH);
            g.FillRectangle(B(Color.FromArgb(40, 0, 255, 120)), x, top, 4, viewH);
            g.FillRectangle(B(Green), x, y, 4, barH);
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        // ------------------------------------------------------------ screen 1: home

        void DrawHome(Graphics g)
        {
            Rectangle box = new Rectangle(20, 160, W - 40, 78);
            Box(g, box, "ReLeaSe iNFo");
            int targets = 0; float tmb = 0;
            foreach (AppInfo a in apps) if (IsTarget(a)) { targets++; tmb += a.MB; }
            string[] left = {
                Dots("TaRGeTS", targets + " aPPS WiLL Be BoNKeD"),
                Dots("RaM 2 FRee", FormatSize(tmb)),
                Dots("iN JaiL", jailCount + " iNMaTeS")
            };
            string[] right = {
                Dots("SYSTeM RaM", RamBar() + " " + ramLoad + "%"),
                Dots("FRee RaM", ramFreeGB.ToString("0.0", Inv) + " / " + ramTotalGB.ToString("0", Inv) + " GB"),
                Dots("GaMe MoDe", gameStatus)
            };
            int budget = (int)((openT - 0.15) * 170);
            for (int i = 0; i < 3; i++)
            {
                budget = TypeLine(g, left[i], Color.FromArgb(210, 230, 210), 34, box.Y + 10 + i * 16, budget);
                budget = TypeLine(g, right[i], Color.FromArgb(210, 230, 210), 490, box.Y + 10 + i * 16, budget);
            }
            string cursor = ((int)(t * 3) % 2 == 0) ? "_" : " ";
            TypeLine(g, "=> STaTuS: " + status + cursor, Color.Yellow, 34, box.Y + 58, budget);
            if (gameStatus.StartsWith("BoNKiNG iN"))
                Button(g, new RectangleF(box.Right - 120, box.Y + 39, 104, 18), "CaNCeL", "gamecancel", Color.FromArgb(120, 0, 0), Color.Yellow, true);

            Rectangle area = new Rectangle(20, GridTop, W - 40, GridBottom - GridTop);
            int maxScroll = Math.Max(0, (int)homeContentH - area.Height);
            homeScroll = Clamp(homeScroll, 0, maxScroll);
            g.SetClip(area);
            foreach (Head h in homeHeads)
            {
                float y = GridTop + h.Y - homeScroll;
                if (y > area.Bottom || y + 22 < area.Top) continue;
                g.FillRectangle(B(Color.FromArgb(150, 0, 0, 0)), 28, y, W - 56, 20);
                g.FillRectangle(B(h.Col), 28, y + 2, 6, 16);
                g.DrawString("=[ " + h.Text + " ]=", fNfoB, B(h.Col), 40, y + 3);
                g.DrawString(h.Right, fSmall, B(Color.FromArgb(150, 160, 150)), new RectangleF(380, y + 4, W - 420, 14), sfLeftMid);
                if (h.Text.StartsWith("GoNNa") && h.Text.Contains("(0)"))
                    g.DrawString(Leet("nothing scheduled for bonking. click a spared card to sentence it."), fNfo, B(Color.Gray), 46, y + 28);
            }
            foreach (AppInfo a in homeOrder)
            {
                RectangleF r = HomeRect(a);
                if (r.Bottom < area.Top || r.Top > area.Bottom) continue;
                DrawTile(g, a, r);
                RectangleF vis = RectangleF.Intersect(r, area);
                if (vis.Height > 4) Zone(vis, (a.Suspect ? "sustile:" : "tile:") + (a.Suspect ? a.Proc : a.Label));
                if (a.Suspect) DrawSuspectButtons(g, a, r, area);
            }
            g.ResetClip();
            Scrollbar(g, W - 26, area.Top, area.Bottom, homeContentH, homeScroll);
            if (maxScroll > 0 && homeScroll < maxScroll)
                g.DrawString(Leet("v scroll for more v"), fTiny, B(Color.FromArgb((int)(150 + 100 * Math.Sin(t * 5)), Green)), new RectangleF(0, area.Bottom - 12, W, 12), sfCenter);

            // hover info bar: the full truth about whatever card is under the mouse
            string info;
            AppInfo hov = (hoverId != null && hoverId.StartsWith("tile:")) ? Find(hoverId.Substring(5)) : null;
            AppInfo hs = (hoverId != null && (hoverId.StartsWith("sus"))) ? FindSuspect(hoverId.Substring(hoverId.IndexOf(':') + 1)) : null;
            if (hs != null)
                info = "> " + hs.Label + "  ::  " + FormatSize(hs.MB) + " iN " + hs.Procs + " PRoCeSSeS  ::  " + hs.Proc + ".eXe  ::  NoT oN YouR LiST";
            else if (hov != null)
                info = "> " + hov.Label + "  ::  " + (hov.Jailed ? "iN JaiL" : FormatSize(hov.MB) + " iN " + hov.Procs + " PRoCeSSeS") + "  ::  " + hov.Names;
            else
                info = Leet("> left-click a card to sentence/spare it. right-click for its dossier. wheel scrolls.");
            g.DrawString(info, fSmall, B(hov != null || hs != null ? Color.Yellow : Color.FromArgb(150, 160, 150)), new RectangleF(24, 591, W - 48, 16), sfLeftMid);

            int tCount = targets;
            Color bonkBg = tCount > 0 && !Busy ? ((int)(t * 4) % 2 == 0 ? Color.FromArgb(150, 0, 0) : Color.FromArgb(210, 40, 0)) : Color.FromArgb(40, 40, 40);
            Button(g, new RectangleF(20, BtnY, 300, 32), Busy ? "[ BoNKiNG... ]" : "[ BoNK 'eM aLL (" + tCount + ") ]", "bonk", bonkBg, Color.Yellow, !Busy);
            Button(g, new RectangleF(330, BtnY, 200, 32), "[ PaRoLe aLL (" + jailCount + ") ]", "parole", Color.FromArgb(0, 40, 20), Green, !Busy);
            Button(g, new RectangleF(540, BtnY, 190, 32), "[ PRoFiLe: " + Leet(settings.ContainsKey("profile") ? settings["profile"] : "gaming") + " > ]", "set:profile:next", Color.FromArgb(40, 0, 60), Color.FromArgb(220, 140, 255), !Busy);
            Button(g, new RectangleF(740, BtnY, 200, 32), "[ eDiT LiST ]", "edit", Color.FromArgb(0, 40, 20), Green, true);
        }

        int TypeLine(Graphics g, string s, Color c, float x, float y, int budget)
        {
            if (budget <= 0) return budget;
            g.DrawString(s.Length <= budget ? s : s.Substring(0, budget), fNfo, B(c), x, y);
            return budget - s.Length;
        }

        void DrawTile(Graphics g, AppInfo a, RectangleF r)
        {
            if (a.Shake > 0) r.Offset((float)(Math.Sin(t * 90 + a.Hash) * a.Shake * 6), (float)(Math.Cos(t * 70) * a.Shake * 3));
            if (a.Suspect) { DrawSuspectTile(g, a, r); return; }
            bool target = IsTarget(a), jailed = a.Jailed;
            bool hot = Hot("tile:" + a.Label);
            Color edge = jailed ? Color.FromArgb(150, 70, 70) : target ? Red : Green;
            if (hot) edge = Color.White;
            g.FillRectangle(B(hot ? Color.FromArgb(240, 26, 46, 32) : Color.FromArgb(230, 8, 14, 10)), r);
            g.DrawRectangle(P(edge), r.X, r.Y, r.Width - 1, r.Height - 1);

            if (target)
            {
                // pulsing crosshair brackets: this one is on the list
                float k = (float)(2 + 3 * (0.5 + 0.5 * Math.Sin(t * 7 + a.Hash % 10)));
                Pen p = P(Color.FromArgb(255, 255, 90, 60));
                float L = 12;
                float x1 = r.X - k, y1 = r.Y - k, x2 = r.Right + k - 1, y2 = r.Bottom + k - 1;
                g.DrawLine(p, x1, y1, x1 + L, y1); g.DrawLine(p, x1, y1, x1, y1 + L);
                g.DrawLine(p, x2, y1, x2 - L, y1); g.DrawLine(p, x2, y1, x2, y1 + L);
                g.DrawLine(p, x1, y2, x1 + L, y2); g.DrawLine(p, x1, y2, x1, y2 - L);
                g.DrawLine(p, x2, y2, x2 - L, y2); g.DrawLine(p, x2, y2, x2, y2 - L);
            }

            if (a.Bars > 0) DrawBars(g, r, a.Bars);

            string chip = jailed ? (a.Solitary ? "SoLiTaRY" : "JaiLeD") : target ? "TaRGeT" : "SaFe";
            Color cc = jailed ? Color.FromArgb(170, 170, 185) : target ? Red : Green;
            SizeF cs = g.MeasureString(chip, fTiny);
            g.FillRectangle(B(cc), r.X + 5, r.Y + 5, cs.Width, 13);
            g.DrawString(chip, fTiny, Brushes.Black, r.X + 5, r.Y + 6);
            if (!jailed && a.Cpu10 >= 80)
            {
                // burning more than 8% of the whole CPU
                SizeF hs = g.MeasureString("CPU HoG", fTiny);
                Color hc = (int)(t * 5) % 2 == 0 ? Color.FromArgb(255, 120, 0) : Red;
                g.FillRectangle(B(hc), r.Right - hs.Width - 6, r.Y + 5, hs.Width, 13);
                g.DrawString("CPU HoG", fTiny, Brushes.Black, r.Right - hs.Width - 6, r.Y + 6);
            }
            else if (!jailed) g.DrawString("x" + a.Procs, fTiny, B(Color.Gray), new RectangleF(r.X, r.Y + 6, r.Width - 6, 12), RightAlign());

            RectangleF nr = new RectangleF(r.X + 6, r.Y + 22, r.Width - 12, 40);
            g.DrawString(a.Label, fName, Brushes.Black, new RectangleF(nr.X + 1, nr.Y + 1, nr.Width, nr.Height), sfWrap);
            g.DrawString(a.Label, fName, B(jailed ? Color.FromArgb(215, 215, 225) : Color.White), nr, sfWrap);

            if (!jailed)
            {
                Color mbc = a.MB >= 1024 ? Color.FromArgb(255, 80, 80) : a.MB >= 300 ? Color.Yellow : Green;
                g.DrawString(FormatShort(a.ShownMB), fTileBig, B(mbc), new RectangleF(r.X, r.Y + 56, r.Width, 22), sfCenter);
                g.DrawString("CPU " + FormatCpu(a.Cpu10) + "  DiSK " + FormatIo(a.IoKB), fTiny, B(a.Cpu10 >= 80 ? Color.FromArgb(255, 150, 60) : Color.FromArgb(140, 160, 140)), new RectangleF(r.X, r.Y + 76, r.Width, 12), sfCenter);
            }
            else g.DrawString(Leet("serving time"), fTiny, B(Color.FromArgb(200, 200, 210)), new RectangleF(r.X, r.Y + 64, r.Width, 18), sfCenter);

            string foot; Color fc;
            if (jailed) { foot = Leet("click: visit cell"); fc = Color.FromArgb(255, 110, 110); }
            else if (target) { foot = (int)(t * 4) % 2 == 0 ? "> WiLL BoNK <" : "  WiLL BoNK  "; fc = Color.Yellow; }
            else { foot = Leet("spared"); fc = Color.FromArgb(0, 200, 100); }
            g.DrawString(foot, fTiny, B(fc), new RectangleF(r.X, r.Bottom - 17, r.Width, 14), sfCenter);

            if (target && a.Nervous > 0)
            {
                float bob = (float)Math.Abs(Math.Sin(t * 12)) * 4;
                g.FillEllipse(B(Color.FromArgb(120, 200, 255)), r.Right - 16, r.Y + 20 - bob, 7, 10);
                g.DrawString("!", fTiny, B(Gold), r.Right - 26, r.Y + 18 - bob);
            }
            if (a.Flash > 0) g.FillRectangle(B(Color.FromArgb((int)(220 * a.Flash), 255, 255, 255)), r);
        }

        void DrawSuspectTile(Graphics g, AppInfo a, RectangleF r)
        {
            bool hot = Hot("sustile:" + a.Proc) || Hot("susadd:" + a.Proc) || Hot("susspare:" + a.Proc);
            bool blink = (int)(t * 3 + a.Hash % 7) % 2 == 0;
            g.FillRectangle(B(hot ? Color.FromArgb(240, 46, 40, 10) : Color.FromArgb(230, 22, 18, 4)), r);
            g.DrawRectangle(P(blink ? Gold : Color.FromArgb(150, 120, 0)), r.X, r.Y, r.Width - 1, r.Height - 1);
            g.DrawRectangle(P(Color.FromArgb(90, 255, 210, 0)), r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5);
            SizeF cs = g.MeasureString("SuSPeCT", fTiny);
            g.FillRectangle(B(Gold), r.X + 5, r.Y + 5, cs.Width, 13);
            g.DrawString("SuSPeCT", fTiny, Brushes.Black, r.X + 5, r.Y + 6);
            // little radar dish sweeping in the corner
            float rx = r.Right - 16, ry = r.Y + 13, rr = 9;
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(B(Color.FromArgb(20, 60, 30)), rx - rr, ry - rr, rr * 2, rr * 2);
            g.DrawEllipse(P(Green), rx - rr, ry - rr, rr * 2, rr * 2);
            double ang = t * 4 + a.Hash;
            g.DrawLine(P(Green), rx, ry, rx + (float)Math.Cos(ang) * rr, ry + (float)Math.Sin(ang) * rr);
            if ((int)(t * 2) % 2 == 0) g.FillEllipse(B(Red), rx + 3, ry - 4, 3, 3);
            g.Restore(st);
            RectangleF nr = new RectangleF(r.X + 6, r.Y + 22, r.Width - 12, 26);
            g.DrawString(a.Label, fName, B(Color.White), nr, sfWrap);
            Color mbc = a.MB >= 1024 ? Color.FromArgb(255, 80, 80) : Color.Yellow;
            g.DrawString(FormatShort(a.MB) + "  x" + a.Procs, fTileBig, B(mbc), new RectangleF(r.X, r.Y + 46, r.Width, 22), sfCenter);
            if (a.Cpu10 > 0) g.DrawString("CPU " + FormatCpu(a.Cpu10), fTiny, B(a.Cpu10 >= 100 ? Color.FromArgb(255, 150, 60) : Color.FromArgb(160, 160, 120)), new RectangleF(r.X, r.Y + 66, r.Width, 12), sfCenter);
        }

        void DrawSuspectButtons(Graphics g, AppInfo a, RectangleF r, Rectangle area)
        {
            float bw = (r.Width - 15) / 2f;
            RectangleF add = new RectangleF(r.X + 5, r.Bottom - 26, bw, 20);
            RectangleF spare = new RectangleF(r.X + 10 + bw, r.Bottom - 26, bw, 20);
            bool vis = add.Top >= area.Top && add.Bottom <= area.Bottom;
            bool ha = vis && Hot("susadd:" + a.Proc), hsp = vis && Hot("susspare:" + a.Proc);
            g.FillRectangle(B(ha ? Red : Color.FromArgb(110, 0, 0)), add);
            g.DrawRectangle(P(Color.Yellow), add.X, add.Y, add.Width - 1, add.Height - 1);
            g.DrawString("+ LiST", fTiny, B(ha ? Color.Black : Color.Yellow), add, sfCenter);
            g.FillRectangle(B(hsp ? Green : Color.FromArgb(0, 50, 25)), spare);
            g.DrawRectangle(P(Green), spare.X, spare.Y, spare.Width - 1, spare.Height - 1);
            g.DrawString("SPaRe", fTiny, B(hsp ? Color.Black : Green), spare, sfCenter);
            if (vis) { Zone(add, "susadd:" + a.Proc); Zone(spare, "susspare:" + a.Proc); }
        }
        StringFormat right;
        StringFormat RightAlign()
        {
            if (right == null) { right = new StringFormat(); right.Alignment = StringAlignment.Far; }
            return right;
        }

        void DrawBars(Graphics g, RectangleF r, float k)
        {
            float bh = r.Height * k;
            g.FillRectangle(B(Color.FromArgb((int)(110 * k), 0, 0, 0)), r.X, r.Y, r.Width, bh);
            for (int i = 1; i <= 5; i++)
            {
                float bx = r.X + r.Width * i / 6f - 2;
                g.FillRectangle(B(Color.FromArgb(150, 150, 165)), bx, r.Y, 4, bh);
                g.FillRectangle(B(Color.FromArgb(230, 230, 240)), bx, r.Y, 1, bh);
            }
            g.FillRectangle(B(Color.FromArgb(150, 150, 165)), r.X, r.Y + 2, r.Width, 4);
            if (k >= 1) g.FillRectangle(B(Color.FromArgb(150, 150, 165)), r.X, r.Bottom - 22, r.Width, 3);
        }

        // ------------------------------------------------------------ screen 2: cell block

        void DrawJail(Graphics g)
        {
            string head = "CeLL BLoCK B  ::  " + jailCount + " iNMaTeS  ::  " + escapes + " PaRoLeD THiS SeSSioN  ::  WaRDeN: THe HaMMeR";
            g.DrawString(head, fNfoB, B(Gold), new RectangleF(20, 158, W - 40, 20), sfCenter);

            List<Inmate> list = new List<Inmate>();
            foreach (AppInfo a in apps) { Inmate m; if (inmates.TryGetValue(a.Label, out m)) list.Add(m); }
            Rectangle area = new Rectangle(20, JailTop, W - 40, JailBottom - JailTop);
            const int cw = 170, ch = 196, gap = 10, cols = 5;
            float x0 = 20 + (920 - (cols * (cw + gap) - gap)) / 2f;
            int rows = (list.Count + cols - 1) / cols;
            float contentH = rows * (ch + gap);
            jailScroll = Clamp(jailScroll, 0, Math.Max(0, (int)contentH - area.Height));

            if (list.Count == 0)
            {
                DrawTumbleweed(g, tumbleX, 420);
                g.DrawString(Leet("jail is empty. suspiciously quiet."), fBig, B(Green), new RectangleF(20, 300, W - 40, 30), sfCenter);
                g.DrawString(Leet("bonk something from the home screen to fill a cell."), fNfo, B(Color.Gray), new RectangleF(20, 330, W - 40, 20), sfCenter);
            }
            else
            {
                g.SetClip(area);
                for (int i = 0; i < list.Count; i++)
                {
                    float cx = x0 + (i % cols) * (cw + gap);
                    float cy = JailTop + (i / cols) * (ch + gap) - jailScroll;
                    if (cy > area.Bottom || cy + ch < area.Top) continue;
                    DrawCell(g, list[i], cx, cy, cw, area);
                }
                g.ResetClip();
                Scrollbar(g, W - 26, area.Top, area.Bottom, contentH, jailScroll);
            }

            DrawGuard(g, guardX, 596);
            Button(g, new RectangleF(20, BtnY, 300, 32), "[ PaRoLe aLL (" + jailCount + ") ]", "parole", Color.FromArgb(0, 40, 20), Green, !Busy && jailCount > 0);
            Button(g, new RectangleF(330, BtnY, 200, 32), "[ BaCK HoMe ]", "home", Color.FromArgb(0, 40, 20), Green, true);
            g.DrawString(Leet("click parole under a cell to release just that one."), fSmall, B(Color.Gray), 545, BtnY + 9);
        }

        void DrawCell(Graphics g, Inmate m, float cx, float cy, int cw, Rectangle area)
        {
            AppInfo a = m.App;
            g.DrawImage(bricks, cx, cy, cw, 120);
            if (a.Solitary)
            {
                // the hole: lights almost out, one flickering bulb
                g.FillRectangle(B(Color.FromArgb(232, 4, 2, 2)), cx, cy, cw, 120);
                g.DrawString("S O L i T a R Y", fTiny, B(Color.FromArgb(120, 150, 20, 20)), new RectangleF(cx, cy + 62, cw, 14), sfCenter);
                int glow = (int)(t * 7 + a.Hash) % 9 == 0 ? 40 : 110;
                g.FillEllipse(B(Color.FromArgb(glow / 3, 255, 220, 120)), cx + cw / 2 - 30, cy + 2, 60, 50);
                g.FillEllipse(B(Color.FromArgb(glow + 100, 255, 230, 140)), cx + cw / 2 - 4, cy + 8, 8, 8);
                g.DrawLine(P(Color.FromArgb(80, 80, 80)), cx + cw / 2, cy, cx + cw / 2, cy + 8);
            }
            g.FillRectangle(B(Color.FromArgb(40, 40, 46)), cx, cy + 104, cw, 16);
            // cot and bucket, for ambience
            g.FillRectangle(B(Color.FromArgb(90, 70, 50)), cx + 110, cy + 92, 52, 8);
            g.FillRectangle(B(Color.FromArgb(120, 120, 130)), cx + 8, cy + 92, 12, 12);

            bool grip = m.BubbleT > 0 && !m.Escaping;
            DrawInmate(g, cx + m.X, cy + 104, a, m.Walk, grip, m.Dir);

            // bars slide open while escaping
            float slide = m.Escaping ? Math.Min(1f, m.EscT * 3) * 150 : 0;
            for (int i = 0; i < 7; i++)
            {
                float bx = cx + 8 + i * 25 - slide;
                if (bx < cx - 4) continue;
                g.FillRectangle(B(Color.FromArgb(140, 140, 155)), bx, cy, 5, 120);
                g.FillRectangle(B(Color.FromArgb(230, 230, 240)), bx + 1, cy, 1, 120);
            }
            g.FillRectangle(B(Color.FromArgb(110, 110, 125)), cx, cy, cw, 6);
            g.FillRectangle(B(Color.FromArgb(110, 110, 125)), cx, cy + 114, cw, 6);

            if (m.BubbleT > 0 && m.Bubble != null)
            {
                SizeF bs = g.MeasureString(m.Bubble, fTiny);
                float bx = Math.Max(cx + 2, Math.Min(cx + cw - bs.Width - 8, cx + m.X - bs.Width / 2));
                float by = cy + 10;
                g.FillRectangle(Brushes.White, bx, by, bs.Width + 6, 15);
                g.DrawRectangle(Pens.Black, bx, by, bs.Width + 6, 15);
                g.FillPolygon(Brushes.White, new[] { new PointF(cx + m.X - 3, by + 15), new PointF(cx + m.X + 5, by + 15), new PointF(cx + m.X, by + 22) });
                g.DrawString(m.Bubble, fTiny, Brushes.Black, bx + 3, by + 2);
            }

            // name plate
            float py = cy + 122;
            g.FillRectangle(B(Color.FromArgb(235, 10, 10, 12)), cx, py, cw, 74);
            g.DrawRectangle(P(Color.FromArgb(150, 70, 70)), cx, py, cw - 1, 73);
            g.DrawString("iNMaTe #" + a.Number, fTiny, B(Gold), cx + 5, py + 3);
            g.DrawString(a.Label, fName, Brushes.White, new RectangleF(cx + 5, py + 15, cw - 10, 28), sfWrap);
            if (a.Solitary) g.DrawString(Leet("solitary :: " + a.Escapes + " escape attempts"), fTiny, B(Red), new RectangleF(cx + 5, py + 42, cw - 10, 12), sfLeftMid);
            else g.DrawString(Crimes[a.Hash % Crimes.Length], fTiny, B(Color.FromArgb(160, 160, 170)), new RectangleF(cx + 5, py + 42, cw - 10, 12), sfLeftMid);
            RectangleF solo = new RectangleF(cx + cw - 46, py + 2, 42, 13);
            bool soloHot = Hot("solitary:" + a.Label);
            g.FillRectangle(B(a.Solitary ? Red : soloHot ? Color.FromArgb(90, 20, 20) : Color.FromArgb(30, 30, 36)), solo);
            g.DrawString("SoLo", fTiny, B(a.Solitary ? Color.Black : Color.FromArgb(220, 120, 120)), solo, sfCenter);
            RectangleF soloVis = RectangleF.Intersect(solo, area);
            if (soloVis.Height > 4 && !m.Escaping) Zone(soloVis, "solitary:" + a.Label);
            RectangleF br = new RectangleF(cx + 30, py + 55, cw - 60, 16);
            bool can = !Busy && !m.Escaping;
            bool hot = can && Hot("paroleone:" + a.Label);
            g.FillRectangle(B(hot ? Green : Color.FromArgb(0, 40, 20)), br);
            g.DrawRectangle(P(can ? Green : Color.Gray), br.X, br.Y, br.Width - 1, br.Height - 1);
            g.DrawString(m.Escaping ? "ReLeaSiNG..." : "[ PaRoLe ]", fTiny, B(hot ? Color.Black : can ? Green : Color.Gray), br, sfCenter);
            RectangleF vis = RectangleF.Intersect(br, area);
            if (can && vis.Height > 4) Zone(vis, "paroleone:" + a.Label);
        }

        void DrawInmate(Graphics g, float x, float feet, AppInfo a, float walk, bool grip, float dir)
        {
            const float s = 2;
            Color skin = Hsv(a.Hash % 360, 0.35, 0.95);
            float step = grip ? 0 : (float)Math.Sin(walk) * 3;
            // legs
            g.FillRectangle(B(Color.FromArgb(40, 40, 50)), x - 5 * s, feet - 10 * s + Math.Max(0, step), 4 * s, 10 * s - Math.Max(0, step));
            g.FillRectangle(B(Color.FromArgb(40, 40, 50)), x + 1 * s, feet - 10 * s + Math.Max(0, -step), 4 * s, 10 * s - Math.Max(0, -step));
            // striped body
            float by = feet - 10 * s - 14 * s;
            for (int i = 0; i < 7; i++) g.FillRectangle(B(i % 2 == 0 ? Color.White : Color.Black), x - 7 * s, by + i * 2 * s, 14 * s, 2 * s);
            g.DrawRectangle(Pens.Black, x - 7 * s, by, 14 * s, 14 * s);
            // arms: up on the bars when yelling, swinging otherwise
            if (grip)
            {
                g.FillRectangle(B(skin), x - 10 * s, by - 10 * s, 3 * s, 12 * s);
                g.FillRectangle(B(skin), x + 7 * s, by - 10 * s, 3 * s, 12 * s);
            }
            else
            {
                g.FillRectangle(B(skin), x - 10 * s, by + 1 * s + step, 3 * s, 10 * s);
                g.FillRectangle(B(skin), x + 7 * s, by + 1 * s - step, 3 * s, 10 * s);
            }
            // head
            float hy = by - 11 * s;
            g.FillRectangle(B(skin), x - 5 * s, hy, 10 * s, 10 * s);
            g.FillRectangle(B(Color.FromArgb(30, 30, 30)), x - 5 * s, hy, 10 * s, 2 * s);   // cap
            float look = grip ? 0 : dir * s;
            g.FillRectangle(Brushes.Black, x - 3 * s + look, hy + 4 * s, 2 * s, 2 * s);
            g.FillRectangle(Brushes.Black, x + 1 * s + look, hy + 4 * s, 2 * s, 2 * s);
            if (grip) g.FillEllipse(Brushes.Black, x - 2 * s, hy + 6.5f * s, 4 * s, 3 * s);
            else g.FillRectangle(Brushes.Black, x - 2 * s + look, hy + 7.5f * s, 4 * s, 1 * s);
            // initials on the shirt so you know who is who
            string ini = Initials(a.Label);
            g.FillRectangle(B(Color.FromArgb(255, 210, 0)), x - 6 * s, by + 4 * s, 12 * s, 6 * s);
            g.DrawString(ini, fTiny, Brushes.Black, new RectangleF(x - 6 * s, by + 4 * s, 12 * s, 6 * s), sfCenter);
        }

        void DrawGuard(Graphics g, float x, float y)
        {
            g.DrawLine(P(Color.FromArgb(60, 60, 70)), 20, y + 16, W - 20, y + 16);
            float step = (float)Math.Sin(t * 9) * 3;
            g.FillRectangle(B(Color.FromArgb(40, 40, 50)), x - 6, y + 6 + Math.Max(0, step), 4, 10 - Math.Max(0, step));
            g.FillRectangle(B(Color.FromArgb(40, 40, 50)), x + 2, y + 6 + Math.Max(0, -step), 4, 10 - Math.Max(0, -step));
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(x + 2, y + 8);
            g.ScaleTransform(0.32f * guardDir, 0.32f);
            DrawHammerShape(g, 90, 255);
            g.Restore(st);
            g.FillEllipse(B(Gold), x + 6 * guardDir - 3, y + 2 + (float)Math.Abs(Math.Sin(t * 9)) * 2, 6, 6);
            g.DrawString(Leet("warden"), fTiny, B(Color.Gray), x - 18, y - 30);
        }

        void DrawTumbleweed(Graphics g, float x, float y)
        {
            float bounce = (float)Math.Abs(Math.Sin(t * 4)) * 22;
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(x, y - bounce);
            g.RotateTransform((float)(t * 260));
            Pen p = P(Color.FromArgb(170, 130, 70));
            for (int i = 0; i < 9; i++) g.DrawArc(p, -16 + i % 3, -16 + i % 4, 32 - i % 5, 30 - i % 3, i * 40, 200);
            g.Restore(st);
            g.DrawLine(P(Color.FromArgb(70, 60, 40)), 20, y + 18, W - 20, y + 18);
        }

        // ------------------------------------------------------------ screen 3: dossiers

        List<AppInfo> Filtered()
        {
            List<AppInfo> l = new List<AppInfo>();
            foreach (AppInfo a in apps)
            {
                bool ok = filter == "all"
                    || (filter == "targets" && IsTarget(a))
                    || (filter == "spared" && a.Running && !a.Enabled && !a.Jailed)
                    || (filter == "jailed" && a.Jailed)
                    || (filter == "sleeping" && !a.Running && !a.Jailed);
                if (ok) l.Add(a);
            }
            return l;
        }

        void DrawDossier(Graphics g)
        {
            string[] chips = { "all", "targets", "spared", "jailed", "sleeping" };
            string[] chipNames = { "aLL", "TaRGeTS", "SPaReD", "JaiLeD", "SLeePiNG" };
            for (int i = 0; i < chips.Length; i++)
            {
                string f0 = filter; filter = chips[i]; int n = Filtered().Count; filter = f0;
                RectangleF r = new RectangleF(20 + i * 120, 160, 112, 22);
                bool on = filter == chips[i];
                string id = "chip:" + chips[i];
                g.FillRectangle(B(on ? Green : Hot(id) ? Color.FromArgb(0, 70, 35) : Color.Black), r);
                g.DrawRectangle(P(DimGreen), r.X, r.Y, r.Width - 1, r.Height - 1);
                g.DrawString(chipNames[i] + " " + n, fNfoB, B(on ? Color.Black : Green), r, sfCenter);
                Zone(r, id);
            }
            g.DrawString(Leet("every app on your bonk list, running or not."), fSmall, B(Color.Gray), 630, 165);

            // left: the list
            List<AppInfo> list = Filtered();
            Rectangle area = new Rectangle(20, ListTop, 452, ListBottom - ListTop);
            g.FillRectangle(B(Color.FromArgb(200, 0, 0, 0)), area);
            g.DrawRectangle(P(DimGreen), area);
            const int rh = 24;
            appsScroll = Clamp(appsScroll, 0, Math.Max(0, list.Count * rh - area.Height + 4));
            g.SetClip(area);
            for (int i = 0; i < list.Count; i++)
            {
                AppInfo a = list[i];
                float y = area.Y + 2 + i * rh - appsScroll;
                if (y + rh < area.Top || y > area.Bottom) continue;
                RectangleF row = new RectangleF(area.X + 2, y, area.Width - 10, rh - 2);
                bool sel = a.Label == selected;
                bool hot = Hot("row:" + a.Label);
                g.FillRectangle(B(sel ? Color.FromArgb(0, 90, 45) : hot ? Color.FromArgb(0, 45, 22) : Color.FromArgb(i % 2 == 0 ? 10 : 18, 20, 14)), row);
                RectangleF cb = new RectangleF(row.X + 4, y + 3, 26, 16);
                string box = a.Jailed ? "[#]" : a.Enabled ? "[X]" : "[ ]";
                g.DrawString(box, fNfoB, B(Hot("toggle:" + a.Label) ? Color.White : a.Enabled ? Red : Green), cb.X, cb.Y);
                g.DrawString(a.Label, fNfo, B(Color.White), new RectangleF(row.X + 36, y, 270, rh - 2), sfLeftMid);
                string st = a.Jailed ? "JaiL" : a.Running ? FormatShort(a.MB) : "ZZZ";
                Color sc = a.Jailed ? Color.FromArgb(170, 170, 185) : a.Running ? (a.MB >= 1024 ? Red : a.MB >= 300 ? Color.Yellow : Green) : Color.Gray;
                g.DrawString(st, fNfoB, B(sc), new RectangleF(row.Right - 60, y, 56, rh - 2), sfLeftMid);
                RectangleF rv = RectangleF.Intersect(row, area);
                if (rv.Height > 4) Zone(rv, "row:" + a.Label);
                RectangleF cv = RectangleF.Intersect(cb, area);
                if (cv.Height > 4 && !a.Jailed) Zone(cv, "toggle:" + a.Label);
            }
            g.ResetClip();
            Scrollbar(g, area.Right - 6, area.Top + 2, area.Bottom - 2, list.Count * rh + 4, appsScroll);

            // right: the dossier
            Rectangle panel = new Rectangle(486, ListTop, 454, ListBottom - ListTop);
            AppInfo s = selected != null ? Find(selected) : null;
            Box(g, panel, s == null ? "DoSSieR" : "DoSSieR :: iNMaTe #" + s.Number);
            if (s == null) { g.DrawString(Leet("pick an app on the left."), fNfo, B(Color.Gray), panel.X + 20, panel.Y + 30); return; }

            // mugshot with height chart
            Rectangle mug = new Rectangle(panel.Right - 108, panel.Y + 16, 92, 104);
            g.FillRectangle(B(Color.FromArgb(30, 34, 40)), mug);
            for (int i = 0; i < 6; i++)
            {
                float ly = mug.Y + 8 + i * 16;
                g.DrawLine(P(Color.FromArgb(90, 95, 110)), mug.X, ly, mug.Right, ly);
                g.DrawString((7 - i) + "'", fTiny, B(Color.FromArgb(120, 125, 140)), mug.X + 2, ly + 1);
            }
            GraphicsState st0 = g.Save();
            g.SetClip(mug);
            g.TranslateTransform(mug.X + mug.Width / 2, mug.Bottom + 30);
            g.ScaleTransform(1.6f, 1.6f);
            DrawInmate(g, 0, 0, s, 0, false, 0);
            g.Restore(st0);
            g.DrawRectangle(P(Gold), mug);
            g.DrawString("#" + s.Number, fTiny, B(Gold), mug.X + 4, mug.Bottom - 12);

            float fx = panel.X + 16, fy = panel.Y + 16, wText = panel.Width - 140;
            g.DrawString(s.Label, fBig, B(Color.White), new RectangleF(fx, fy, wText, 46), sfWrap);
            fy += 50;
            string stat = s.Jailed ? "iN JaiL" : s.Running ? "RuNNiNG (" + s.Procs + " PRoCeSSeS)" : "SLeePiNG (NoT RuNNiNG)";
            string sentence = s.Jailed ? "SeRViNG TiMe" : s.Enabled ? "BoNK oN SiGHT" : "SPaReD";
            fy = Field(g, "STaTuS", stat, fx, fy, wText, s.Running ? Green : Color.Gray);
            fy = Field(g, "SeNTeNCe", sentence, fx, fy, wText, s.Enabled ? Red : Green);
            fy = Field(g, "RaM NoW", s.Running ? FormatSize(s.MB) : "0 MB", fx, fy, wText, Color.Yellow);
            if (s.Jailed || s.Escapes > 0 || s.Solitary) fy = Field(g, "eSCaPe aTTeMPTS", s.Escapes + (s.Solitary ? "  ::  iN SoLiTaRY" : ""), fx, fy, wText, s.Solitary ? Red : Color.FromArgb(200, 210, 200));
            fy = Field(g, "CPU / DiSK", s.Running ? FormatCpu(s.Cpu10) + " oF YouR CPU  ::  " + FormatIo(s.IoKB) + " DiSK" : "-", fx, fy, wText, s.Cpu10 >= 80 ? Color.FromArgb(255, 150, 60) : Color.FromArgb(200, 210, 200));
            fy = Math.Max(fy, mug.Bottom + 6);
            fy = Field(g, "aLiaSeS", s.Names, fx, fy, panel.Width - 32, Color.FromArgb(200, 210, 200));
            fy = Field(g, "PaRoLe PLaN", s.Relaunch, fx, fy, panel.Width - 32, Color.FromArgb(200, 210, 200));
            fy = Field(g, "RaP SHeeT", Crimes[s.Hash % Crimes.Length] + "; " + Crimes[(s.Hash / 7 + 3) % Crimes.Length], fx, fy, panel.Width - 32, Color.FromArgb(255, 150, 150));

            // live RAM history
            Rectangle chart = new Rectangle(panel.X + 16, panel.Bottom - 104, panel.Width - 32, 50);
            g.FillRectangle(B(Color.FromArgb(10, 20, 14)), chart);
            g.DrawRectangle(P(DimGreen), chart);
            g.DrawString(Leet("ram (green) and cpu (orange) history, live while this window is open"), fTiny, B(Color.Gray), chart.X, chart.Y - 12);
            if (s.History.Count > 1)
            {
                int max = 1; foreach (int v in s.History) max = Math.Max(max, v);
                PointF[] pts = new PointF[s.History.Count];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new PointF(chart.X + 2 + i * (chart.Width - 4f) / (s.History.Count - 1), chart.Bottom - 3 - s.History[i] * (chart.Height - 6f) / max);
                g.DrawLines(P(Green), pts);
                g.DrawString(FormatShort(max) + " PeaK", fTiny, B(Green), chart.Right - 64, chart.Y + 2);
                if (s.CpuHistory.Count > 1)
                {
                    int cmax = 10; foreach (int v in s.CpuHistory) cmax = Math.Max(cmax, v);
                    PointF[] cp = new PointF[s.CpuHistory.Count];
                    for (int i = 0; i < cp.Length; i++)
                        cp[i] = new PointF(chart.X + 2 + i * (chart.Width - 4f) / (s.CpuHistory.Count - 1), chart.Bottom - 3 - s.CpuHistory[i] * (chart.Height - 6f) / cmax);
                    g.DrawLines(P(Color.FromArgb(255, 150, 60)), cp);
                    g.DrawString("CPU " + FormatCpu(cmax) + " PeaK", fTiny, B(Color.FromArgb(255, 150, 60)), chart.Right - 64, chart.Y + 14);
                }
            }
            else g.DrawString(Leet(s.Running ? "collecting samples..." : "nothing to chart. it's asleep."), fTiny, B(Color.Gray), chart.X + 6, chart.Y + 18);

            float by2 = panel.Bottom - 40;
            bool free = !Busy;
            Button(g, new RectangleF(panel.X + 16, by2, 100, 28), s.Enabled ? "SPaRe" : "SeNTeNCe", "toggle:" + s.Label, Color.FromArgb(0, 40, 20), Green, free && !s.Jailed);
            Button(g, new RectangleF(panel.X + 122, by2, 100, 28), "BoNK!", "bonkone:" + s.Label, Color.FromArgb(120, 0, 0), Color.Yellow, free && s.Running && !s.Jailed);
            Button(g, new RectangleF(panel.X + 228, by2, 100, 28), "PaRoLe", "paroleone:" + s.Label, Color.FromArgb(0, 40, 20), Green, free && s.Jailed);
            Button(g, new RectangleF(panel.X + 334, by2, 104, 28), s.Solitary ? "SoLo: oN" : "SoLo: oFF", "solitary:" + s.Label, s.Solitary ? Color.FromArgb(120, 0, 0) : Color.FromArgb(40, 10, 10), Color.FromArgb(255, 110, 110), true);
        }

        float Field(Graphics g, string key, string value, float x, float y, float w, Color c)
        {
            g.DrawString(key, fTiny, B(Color.FromArgb(0, 200, 100)), x, y);
            SizeF sz = g.MeasureString(value ?? "", fNfo, (int)w);
            float h = Math.Min(sz.Height, 30);
            g.DrawString(value ?? "", fNfo, B(c), new RectangleF(x, y + 11, w, h + 1), sfWrap);
            return y + 13 + h;
        }

        // ------------------------------------------------------------ screen 4: settings

        void DrawStartup(Graphics g)
        {
            int total = startItems.Count;
            string head = "STaRTuP JaiL  ::  " + total + " aPPS LauNCH aT LoGiN  ::  " + startJailed + " JaiLeD  ::  FeWeR = FaSTeR BooT";
            g.DrawString(head, fNfoB, B(Gold), new RectangleF(20, 158, W - 40, 20), sfCenter);
            Rectangle area = new Rectangle(20, 184, W - 40, 398);
            g.FillRectangle(B(Color.FromArgb(200, 0, 0, 0)), area);
            g.DrawRectangle(P(DimGreen), area);
            const int rh = 36;
            float contentH = total * rh + 4;
            startScroll = Clamp(startScroll, 0, Math.Max(0, (int)contentH - area.Height));
            if (total == 0) g.DrawString(Leet("reading your startup apps..."), fNfo, B(Color.Gray), new RectangleF(area.X, area.Y, area.Width, area.Height), sfCenter);
            g.SetClip(area);
            for (int i = 0; i < total; i++)
            {
                StartItem s = startItems[i];
                float y = area.Y + 2 + i * rh - startScroll;
                if (y + rh < area.Top || y > area.Bottom) continue;
                zonesOff = y < area.Top || y + rh - 2 > area.Bottom;
                RectangleF row = new RectangleF(area.X + 2, y, area.Width - 12, rh - 3);
                g.FillRectangle(B(i % 2 == 0 ? Color.FromArgb(10, 20, 14) : Color.FromArgb(16, 28, 20)), row);
                string id = "startup:" + s.Key;
                RectangleF btn = new RectangleF(row.X + 6, y + 6, 104, 22);
                if (s.Immune)
                {
                    g.FillRectangle(B(Color.FromArgb(30, 30, 40)), btn);
                    g.DrawRectangle(P(Color.Gray), btn.X, btn.Y, btn.Width - 1, btn.Height - 1);
                    g.DrawString("iMMuNe", fTiny, B(Color.Gray), btn, sfCenter);
                }
                else
                {
                    bool hot = Hot(id) && !Busy;
                    Color fg = s.Pending ? Gold : s.Enabled ? Green : Red;
                    g.FillRectangle(B(hot ? fg : s.Enabled ? Color.FromArgb(0, 45, 22) : Color.FromArgb(60, 0, 0)), btn);
                    g.DrawRectangle(P(fg), btn.X, btn.Y, btn.Width - 1, btn.Height - 1);
                    string txt = s.Pending ? "..." : s.Enabled ? "STaRTS" : "JaiLeD";
                    g.DrawString(txt, fNfoB, B(hot ? Color.Black : fg), btn, sfCenter);
                    if (!Busy) Zone(btn, id);
                }
                g.DrawString(s.Name, fNfoB, B(s.Enabled || s.Immune ? Color.White : Color.FromArgb(170, 170, 180)), new RectangleF(row.X + 120, y + 3, 330, 16), sfLeftMid);
                g.DrawString(s.Command, fTiny, B(Color.FromArgb(120, 130, 120)), new RectangleF(row.X + 120, y + 18, 520, 13), sfLeftMid);
                SizeF ss = g.MeasureString(s.Source, fTiny);
                g.FillRectangle(B(s.Admin ? Color.FromArgb(70, 50, 0) : Color.FromArgb(0, 40, 50)), row.X + 470, y + 3, ss.Width + 4, 13);
                g.DrawString(s.Source, fTiny, B(s.Admin ? Gold : Color.FromArgb(120, 220, 255)), row.X + 472, y + 4);
                if (s.Admin) g.DrawString(Leet("admin"), fTiny, B(Color.FromArgb(150, 120, 40)), row.X + 476 + ss.Width, y + 4);
                string impact = s.MB >= 300 ? "HeaVY" : s.MB >= 80 ? "MeDiuM" : s.MB > 0 ? "LiGHT" : "NoT RuNNiNG";
                Color ic = s.MB >= 300 ? Red : s.MB >= 80 ? Color.Yellow : s.MB > 0 ? Green : Color.Gray;
                g.DrawString(impact, fNfoB, B(ic), new RectangleF(row.Right - 190, y + 3, 100, 16), sfLeftMid);
                if (s.MB > 0) g.DrawString(FormatSize(s.MB) + Leet(" now"), fTiny, B(Color.FromArgb(160, 170, 160)), new RectangleF(row.Right - 190, y + 19, 100, 12), sfLeftMid);
                if (s.Bars > 0)
                {
                    float bw = row.Width * s.Bars;
                    g.FillRectangle(B(Color.FromArgb((int)(60 * s.Bars), 255, 0, 0)), row.X, row.Y, bw, row.Height);
                    for (float bx = row.X + 130; bx < row.X + bw; bx += 46)
                        g.FillRectangle(B(Color.FromArgb(120, 170, 170, 185)), bx, row.Y, 3, row.Height);
                }
            }
            zonesOff = false;
            g.ResetClip();
            Scrollbar(g, area.Right - 6, area.Top + 2, area.Bottom - 2, contentH, startScroll);
            Button(g, new RectangleF(20, BtnY, 200, 32), "[ ReFReSH ]", "startup-refresh", Color.FromArgb(0, 40, 20), Green, !Busy);
            g.DrawString(Leet("nothing is deleted. a jailed app just stops launching at login; parole flips it back. admin items ask windows first."), fSmall, B(Color.Gray), new RectangleF(232, BtnY + 2, W - 252, 30), sfWrap);
        }
        // ------------------------------------------------------------ screen: stats

        readonly Dictionary<string, string> stats = new Dictionary<string, string>();
        string[] shameLabels = new string[0];
        int[] shameCounts = new int[0];
        string[] achNames = new string[0], achDescs = new string[0];
        bool[] achOn = new bool[0];

        public void SetStats(string[] keys, string[] values) { for (int i = 0; i < keys.Length; i++) stats[keys[i]] = values[i]; }
        public void SetShame(string[] labels, int[] counts) { shameLabels = labels; shameCounts = counts; }
        public void SetAchievements(string[] names, string[] descs, bool[] unlocked) { achNames = names; achDescs = descs; achOn = unlocked; }

        public void ShowAchievement(string name)
        {
            bannerText = "uNLoCKeD!"; bannerSub = Leet("achievement: " + name); bannerT = 0;
            if (shakeOn) shake = 0.4f;
            Play(sfxFanfare);
        }

        string Stat(string k, string def) { string v; return stats.TryGetValue(k, out v) && !string.IsNullOrEmpty(v) ? v : def; }

        void DrawStats(Graphics g)
        {
            string[] cap = { "aPPS BoNKeD", "RaM FReeD", "BoNK SeSSioNS", "PaRoLeS GRaNTeD" };
            string[] val = { Stat("apps", "0"), Stat("freed", "0 MB"), Stat("bonks", "0"), Stat("paroles", "0") };
            for (int i = 0; i < 4; i++)
            {
                RectangleF r = new RectangleF(20 + i * 232, 158, 224, 70);
                g.FillRectangle(B(Color.FromArgb(210, 0, 0, 0)), r);
                g.DrawRectangle(P(i == 1 ? Gold : DimGreen), r.X, r.Y, r.Width - 1, r.Height - 1);
                g.DrawString(cap[i], fTiny, B(Color.FromArgb(150, 170, 150)), new RectangleF(r.X, r.Y + 6, r.Width, 12), sfCenter);
                float bob = (float)Math.Sin(t * 3 + i) * 2;
                g.DrawString(val[i], fCount, B(i == 1 ? Gold : Hsv(t * 40 + i * 70, 0.6, 1)), new RectangleF(r.X, r.Y + 20 + bob, r.Width, 40), sfCenter);
            }
            g.DrawString(Leet("ram freed is what the bonked apps were using. counting since ") + Stat("since", Leet("today")), fTiny, B(Color.Gray), new RectangleF(20, 230, W - 40, 12), sfCenter);

            // hall of shame
            Rectangle shame = new Rectangle(20, 252, 452, 218);
            Box(g, shame, "HaLL oF SHaMe");
            int max = 1; foreach (int n in shameCounts) max = Math.Max(max, n);
            if (shameLabels.Length == 0) g.DrawString(Leet("no repeat offenders yet. go bonk something."), fNfo, B(Color.Gray), new RectangleF(shame.X, shame.Y, shame.Width, shame.Height), sfCenter);
            for (int i = 0; i < shameLabels.Length && i < 8; i++)
            {
                float y = shame.Y + 14 + i * 25;
                string medal = i == 0 ? "#1" : i == 1 ? "#2" : i == 2 ? "#3" : "  ";
                g.DrawString(medal, fNfoB, B(i == 0 ? Gold : i == 1 ? Color.Silver : Color.FromArgb(205, 127, 50)), shame.X + 10, y);
                g.DrawString(shameLabels[i], fNfo, B(Color.White), new RectangleF(shame.X + 36, y, 190, 16), sfLeftMid);
                float bw = 170f * shameCounts[i] / max * (float)Math.Min(1, openT * 1.5);
                g.FillRectangle(B(Color.FromArgb(150, 0, 0)), shame.X + 232, y + 3, bw, 12);
                g.FillRectangle(B(Red), shame.X + 232, y + 3, bw, 3);
                g.DrawString("x" + shameCounts[i], fNfoB, B(Red), shame.X + 236 + bw, y);
            }

            // last bonk report
            Rectangle rep = new Rectangle(486, 252, 454, 218);
            Box(g, rep, "LaST BoNK RePoRT");
            double before, after;
            bool hasRep = double.TryParse(Stat("lastBefore", ""), NumberStyles.Float, Inv, out before) & double.TryParse(Stat("lastAfter", ""), NumberStyles.Float, Inv, out after);
            if (!hasRep) g.DrawString(Leet("bonk something and the before/after shows up here."), fNfo, B(Color.Gray), new RectangleF(rep.X, rep.Y, rep.Width, rep.Height), sfCenter);
            else
            {
                double top = Math.Max(1, ramTotalGB * 1024);
                float bx = rep.X + 110, bwMax = rep.Width - 130;
                g.DrawString(Leet("free before"), fNfo, B(Color.Gray), rep.X + 16, rep.Y + 26);
                g.FillRectangle(B(Color.FromArgb(60, 60, 70)), bx, rep.Y + 26, (float)(bwMax * before / top), 16);
                g.DrawString(FormatSize((float)before), fTiny, B(Color.White), bx + 4, rep.Y + 28);
                g.DrawString(Leet("free after"), fNfo, B(Color.Gray), rep.X + 16, rep.Y + 54);
                float grow = (float)Math.Min(1, openT * 1.2);
                g.FillRectangle(B(Green), bx, rep.Y + 54, (float)(bwMax * (before + (after - before) * grow) / top), 16);
                g.DrawString(FormatSize((float)after), fTiny, B(Color.Black), bx + 4, rep.Y + 56);
                double gained = after - before;
                g.DrawString((gained >= 0 ? "+" : "") + FormatSize((float)gained) + Leet(" measured"), fBig, B(gained >= 0 ? Green : Color.Yellow), new RectangleF(rep.X, rep.Y + 84, rep.Width, 26), sfCenter);
                g.DrawString(Leet(Stat("lastApps", "?") + " apps bonked, about " + Stat("lastEst", "?") + " of ram they were holding"), fNfo, B(Color.FromArgb(200, 210, 200)), new RectangleF(rep.X, rep.Y + 116, rep.Width, 16), sfCenter);
                g.DrawString(Stat("lastWhen", ""), fTiny, B(Color.Gray), new RectangleF(rep.X, rep.Y + 136, rep.Width, 12), sfCenter);
                g.DrawString(Leet("windows refills free ram with cache over time; the measured number is a snapshot, not a promise."), fTiny, B(Color.FromArgb(110, 120, 110)), new RectangleF(rep.X + 14, rep.Y + 160, rep.Width - 28, 40), sfWrap);
            }

            // achievements
            int n2 = achNames.Length, unlocked = 0;
            foreach (bool b in achOn) if (b) unlocked++;
            g.DrawString("aCHieVeMeNTS  " + unlocked + " / " + n2, fNfoB, B(Gold), 24, 478);
            const int cols = 8;
            float aw = (920 - (cols - 1) * 6) / (float)cols;
            for (int i = 0; i < n2; i++)
            {
                RectangleF r = new RectangleF(20 + (i % cols) * (aw + 6), 496 + (i / cols) * 58, aw, 52);
                bool on = achOn[i];
                string id = "ach:" + i;
                g.FillRectangle(B(on ? Color.FromArgb(230, 40, 32, 0) : Color.FromArgb(220, 14, 14, 16)), r);
                g.DrawRectangle(P(on ? ((int)(t * 2 + i) % 3 == 0 ? Color.White : Gold) : Color.FromArgb(70, 70, 80)), r.X, r.Y, r.Width - 1, r.Height - 1);
                if (on) DrawTrophy(g, r.X + 16, r.Y + 26, (float)Math.Sin(t * 4 + i) * 2);
                else DrawLock(g, r.X + 16, r.Y + 26);
                g.DrawString(achNames[i], fTiny, B(on ? Gold : Color.FromArgb(120, 120, 130)), new RectangleF(r.X + 32, r.Y + 6, r.Width - 34, 14), sfLeftMid);
                g.DrawString(on ? achDescs[i] : "???", fTiny, B(on ? Color.FromArgb(210, 210, 190) : Color.FromArgb(90, 90, 100)), new RectangleF(r.X + 32, r.Y + 20, r.Width - 34, 30), sfWrap);
                Zone(r, id);
            }
            string hov = (hoverId != null && hoverId.StartsWith("ach:")) ? hoverId.Substring(4) : null;
            int hi;
            string tip = hov != null && int.TryParse(hov, out hi) && hi < n2 ? "> " + achNames[hi] + ": " + achDescs[hi] + (achOn[hi] ? "  (UNLOCKED)" : "  (LOCKED)") : Leet("> hover an achievement to read how to get it.");
            g.DrawString(tip, fSmall, B(Color.Yellow), new RectangleF(24, BtnY + 8, W - 48, 16), sfLeftMid);
        }

        void DrawTrophy(Graphics g, float cx, float cy, float bob)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            cy += bob;
            g.DrawArc(P(Gold), cx - 12, cy - 12, 8, 9, 90, 180);
            g.DrawArc(P(Gold), cx + 4, cy - 12, 8, 9, 270, 180);
            g.FillPie(B(Gold), cx - 8, cy - 18, 16, 18, 0, 180);
            g.FillRectangle(B(Gold), cx - 8, cy - 10, 16, 2);
            g.FillRectangle(B(Gold), cx - 1.5f, cy - 1, 3, 6);
            g.FillRectangle(B(Gold), cx - 6, cy + 5, 12, 3);
            g.FillEllipse(B(Color.White), cx - 4, cy - 7, 3, 3);
            g.Restore(st);
        }

        void DrawLock(Graphics g, float cx, float cy)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(100, 100, 110), 2)) g.DrawArc(p, cx - 5, cy - 14, 10, 12, 180, 180);
            g.FillRectangle(B(Color.FromArgb(100, 100, 110)), cx - 7, cy - 8, 14, 12);
            g.FillRectangle(B(Color.Black), cx - 1, cy - 4, 2, 4);
            g.Restore(st);
        }
        void DrawSettings(Graphics g)
        {
            Rectangle box = new Rectangle(20, 162, W - 40, 448);
            Box(g, box, "SeTTiNGS");
            int top = box.Y + 10, bottom = box.Bottom - 8;
            float contentH = SettingRows.Length * 47;
            settingsScroll = Clamp(settingsScroll, 0, Math.Max(0, (int)contentH - (bottom - top)));
            g.SetClip(new Rectangle(box.X + 4, top, box.Width - 8, bottom - top));
            for (int i = 0; i < SettingRows.Length; i++)
            {
                string[] row = SettingRows[i];
                string key = row[0], kind = row[1];
                float y = box.Y + 14 + i * 47 - settingsScroll;
                if (y + 44 < top || y - 4 > bottom) continue;
                zonesOff = y - 4 < top || y + 40 > bottom;   // half-visible rows are not clickable
                if (i % 2 == 0) g.FillRectangle(B(Color.FromArgb(14, 0, 255, 120)), box.X + 8, y - 4, box.Width - 16, 44);
                g.DrawString(Leet(row[2]), fBtn, B(Color.White), box.X + 22, y);
                g.DrawString(Leet(row[3]), fSmall, B(Color.FromArgb(150, 165, 150)), box.X + 22, y + 19);
                string v = settings.ContainsKey(key) ? settings[key] : "";
                float cx = box.Right - 330;
                if (kind == "bool")
                {
                    bool on = IsTrue(v);
                    Button(g, new RectangleF(cx, y + 2, 150, 28), on ? "[X] oN" : "[ ] oFF", "set:" + key + ":toggle", on ? Color.FromArgb(0, 60, 30) : Color.FromArgb(40, 10, 10), on ? Green : Red, true);
                }
                else if (kind == "slider")
                {
                    cx -= 40;
                    int n; int.TryParse(v, out n);
                    for (int s = 1; s <= 10; s++)
                    {
                        RectangleF cell = new RectangleF(cx + (s - 1) * 24, y + 4, 20, 24);
                        bool filled = s * 10 <= n;
                        string id = "set:" + key + ":val:" + (s * 10);
                        g.FillRectangle(B(filled ? Hsv(120 - s * 11, 0.9, 1) : Color.FromArgb(25, 30, 25)), cell);
                        g.DrawRectangle(P(Hot(id) ? Color.White : DimGreen), cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
                        Zone(cell, id);
                    }
                    g.DrawString(n + "%", fBtn, B(Green), new RectangleF(cx + 244, y + 4, 60, 24), sfLeftMid);
                    RectangleF mute = new RectangleF(cx + 290, y + 4, 40, 24);
                    Button(g, mute, "0", "set:" + key + ":val:0", Color.Black, Color.Gray, true);
                }
                else
                {
                    Button(g, new RectangleF(cx, y + 2, 36, 28), "<", "set:" + key + ":prev", Color.Black, Green, true);
                    string shown = key == "polite" ? v + " SeC" : key == "radarMB" ? v + " MB" : Leet(v);
                    g.FillRectangle(B(Color.FromArgb(0, 30, 15)), cx + 40, y + 2, 190, 28);
                    g.DrawString(shown, fBtn, B(Gold), new RectangleF(cx + 40, y + 2, 190, 28), sfCenter);
                    Button(g, new RectangleF(cx + 234, y + 2, 36, 28), ">", "set:" + key + ":next", Color.Black, Green, true);
                }
            }
            zonesOff = false;
            g.ResetClip();
            Scrollbar(g, box.Right - 8, top, bottom, contentH, settingsScroll);
            g.DrawString(Leet("every change saves instantly. nothing in here can bonk steam."), fSmall, B(Color.Gray), new RectangleF(20, BtnY + 8, W - 40, 16), sfCenter);
        }

        // ------------------------------------------------------------ screen 5: updates

        void DrawUpdates(Graphics g)
        {
            Rectangle box = new Rectangle(20, 162, W - 40, 112);
            Box(g, box, "PaTCH CeNTeR");
            string latest = remoteVersion == null ? "???" : "v" + remoteVersion;
            string when = lastCheck == DateTime.MinValue ? "NeVeR" : lastCheck.ToString("HH:mm:ss", Inv);
            string[] lines = {
                Dots("iNSTaLLeD", "v" + localVersion),
                Dots("LaTeST", latest),
                Dots("STaTuS", updChecking ? "CHeCKiNG GiTHuB" + new string('.', 1 + (int)(t * 3) % 3) : updStatus),
                Dots("RePo", repoUrl.Replace("https://", "")),
                Dots("LaST CHeCK", when)
            };
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], fNfo, B(i == 2 && updAvailable ? Gold : Color.FromArgb(210, 230, 210)), 36, box.Y + 12 + i * 18);

            if (updAvailable)
            {
                GraphicsState st = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(box.Right - 130, box.Y + 56);
                g.RotateTransform((float)(Math.Sin(t * 3) * 8));
                float sc = (float)(1 + 0.06 * Math.Sin(t * 8));
                g.ScaleTransform(sc, sc);
                PointF[] star = Burst(22, 70, 48);
                g.FillPolygon(B(Gold), star);
                g.DrawPolygon(Pens.Black, star);
                g.DrawString("NeW CRaCK!", fBtn, Brushes.Black, new RectangleF(-60, -18, 120, 18), sfCenter);
                g.DrawString("v" + remoteVersion, fBtn, B(Color.FromArgb(180, 0, 0)), new RectangleF(-60, 0, 120, 18), sfCenter);
                g.Restore(st);
            }

            Rectangle logBox = new Rectangle(20, 290, W - 40, 306);
            Box(g, logBox, "CHaNGeLoG");
            g.SetClip(new Rectangle(logBox.X + 6, logBox.Y + 8, logBox.Width - 12, logBox.Height - 14));
            if (changelog.Count == 0)
                g.DrawString(Leet(updChecking ? "fetching the changelog..." : "hit check now to pull the changelog from github."), fNfo, B(Color.Gray), logBox.X + 20, logBox.Y + 20);
            int lh = 15;
            logScroll = Clamp(logScroll, 0, Math.Max(0, changelog.Count * lh - logBox.Height + 24));
            for (int i = 0; i < changelog.Count; i++)
            {
                float y = logBox.Y + 14 + i * lh - logScroll;
                if (y < logBox.Y - lh || y > logBox.Bottom) continue;
                string line = changelog[i];
                Color c = line.StartsWith("## ") ? Gold : line.StartsWith("# ") ? Green : line.StartsWith("- ") ? Color.FromArgb(210, 230, 210) : Color.Gray;
                Font f = line.StartsWith("#") ? fNfoB : fNfo;
                g.DrawString(line.TrimStart('#', ' '), f, B(c), logBox.X + 20, y);
            }
            g.ResetClip();
            Scrollbar(g, logBox.Right - 10, logBox.Y + 8, logBox.Bottom - 8, changelog.Count * lh + 24, logScroll);

            Button(g, new RectangleF(20, BtnY, 220, 32), updChecking ? "[ CHeCKiNG... ]" : "[ CHeCK NoW ]", "upd:check", Color.FromArgb(0, 40, 20), Green, !updChecking && !Busy);
            Color ib = updAvailable && (int)(t * 4) % 2 == 0 ? Color.FromArgb(160, 110, 0) : Color.FromArgb(90, 60, 0);
            Button(g, new RectangleF(250, BtnY, 300, 32), updAvailable ? "[ iNSTaLL v" + remoteVersion + " ]" : "[ NoTHiNG To iNSTaLL ]", "upd:install", ib, Gold, updAvailable && !Busy);
            Button(g, new RectangleF(560, BtnY, 200, 32), "[ oPeN RePo ]", "upd:repo", Color.FromArgb(0, 40, 20), Green, true);
            g.DrawString(Leet("your bonk list and settings survive updates."), fTiny, B(Color.Gray), 770, BtnY + 11);

            if (updPct >= 0) DrawPatcher(g);
        }

        void DrawPatcher(Graphics g)
        {
            Rectangle o = new Rectangle(60, 200, W - 120, 380);
            g.FillRectangle(B(Color.FromArgb(240, 0, 0, 0)), o);
            g.DrawRectangle(P(Gold), o);
            g.DrawString("PaTCHiNG BoNK MoDe", fBig, B(Gold), new RectangleF(o.X, o.Y + 10, o.Width, 26), sfCenter);
            // fake-but-fun hex dump scrolling under the real progress
            Random hr = new Random((int)(t * 20));
            for (int i = 0; i < 12; i++)
            {
                int addr = ((int)(t * 400) + i * 16) & 0xFFFF0;
                string hex = addr.ToString("X8") + ":";
                for (int j = 0; j < 12; j++) hex += " " + hr.Next(256).ToString("X2");
                g.DrawString(hex, fNfo, B(Color.FromArgb(i == 11 ? 255 : 90 + i * 12, 0, 255, 120)), o.X + 40, o.Y + 50 + i * 15);
            }
            int pct = Math.Max(0, Math.Min(100, updPct));
            int cells = 40, filled = pct * cells / 100;
            string bar = "[" + new string((char)0x2588, filled) + new string((char)0x2591, cells - filled) + "] " + pct + "%";
            g.DrawString(bar, fBtn, B(Green), new RectangleF(o.X, o.Bottom - 80, o.Width, 20), sfCenter);
            g.DrawString(updMsg, fNfoB, B(Color.Yellow), new RectangleF(o.X, o.Bottom - 54, o.Width, 18), sfCenter);
            g.DrawString(Leet("do not unplug your hammer"), fTiny, B(Color.Gray), new RectangleF(o.X, o.Bottom - 30, o.Width, 14), sfCenter);
        }

        // ------------------------------------------------------------ screen 6: greetz

        void DrawGreetz(Graphics g)
        {
            Rectangle area = new Rectangle(20, 158, W - 40, 486);
            UpdatePlasma();
            g.DrawImage(plasma, area);
            g.DrawRectangle(P(DimGreen), area);

            DrawCube(g, 250, 390, 120);

            Rectangle box = new Rectangle(470, 174, 450, 456);
            g.FillRectangle(B(Color.FromArgb(190, 0, 0, 0)), box);
            g.DrawRectangle(P(Green), box);
            string[] nfo = {
                "    B O N K   C R E W   P R E S E N T S",
                "",
                Dots("CoDe", "BoNK CReW"),
                Dots("MuSiC", "HoMeMaDe SQuaRe WaVeS"),
                Dots("GFX", "GDi+ aND PRaYeR"),
                Dots("RaM uSeD", "LeSS THaN 1 CHRoMe TaB"),
                Dots("PRoTeCTiON", "NoNe. iT'S a HaMMeR"),
                "",
                "  GReeTZ To:",
                "    RaZoR 1911 * FaiRLiGHT * ReLoaDeD",
                "    CoDeX * SKiDRoW * SND * CPY",
                "    eVeRY KeYGeN MuSiCiaN eVeR",
                "",
                "  No GReeTZ To:",
                "    aDoBe CReaTiVe CLouD (You KNoW)",
                "    aPPS THaT MiNiMiZe WHeN You HiT X",
                "",
                "  KeYS:",
                "    [1-8] SCReeNS  [B] BoNK  [P] PaRoLe",
                "    [M] MuSiC  [eSC] eXiT  WHeeL SCRoLLS",
                "",
                "  oPeN SouRCe (aPaCHe-2.0):",
                "    FoRKS MuST CReDiT GiTHuB.CoM/MaKeSuPPLY/BoNK-MoDe"
            };
            for (int i = 0; i < nfo.Length; i++)
            {
                Color c = i == 0 ? Hsv(t * 120, 0.8, 1) : nfo[i].StartsWith("  ") && !nfo[i].StartsWith("    ") ? Gold : Color.FromArgb(210, 230, 210);
                g.DrawString(nfo[i], i == 0 ? fNfoB : fNfo, B(c), box.X + 14, box.Y + 12 + i * 18.5f);
            }
        }

        void UpdatePlasma()
        {
            int w = plasma.Width, h = plasma.Height;
            BitmapData d = plasma.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            int[] px = new int[w * h];
            double tt = t * 1.4;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v = Math.Sin(x * 0.09 + tt) + Math.Sin(y * 0.11 + tt * 1.3) + Math.Sin((x + y) * 0.06 + tt * 0.7) + Math.Sin(Math.Sqrt(x * x + y * y) * 0.08 - tt);
                    int r = (int)(40 + 38 * Math.Sin(v * Math.PI));
                    int gg = (int)(30 + 28 * Math.Sin(v * Math.PI + 2.1));
                    int b = (int)(60 + 55 * Math.Sin(v * Math.PI + 4.2));
                    px[y * w + x] = (255 << 24) | (r << 16) | (gg << 8) | b;
                }
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            plasma.UnlockBits(d);
        }

        void DrawCube(Graphics g, float cx, float cy, float size)
        {
            double ax = t * 0.9, ay = t * 0.6, az = t * 0.3;
            double[,] v = new double[8, 3];
            int idx = 0;
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) { v[idx, 0] = x; v[idx, 1] = y; v[idx, 2] = z; idx++; }
            PointF[] p = new PointF[8];
            double[] depth = new double[8];
            for (int i = 0; i < 8; i++)
            {
                double x = v[i, 0], y = v[i, 1], z = v[i, 2];
                double y1 = y * Math.Cos(ax) - z * Math.Sin(ax), z1 = y * Math.Sin(ax) + z * Math.Cos(ax);
                double x2 = x * Math.Cos(ay) + z1 * Math.Sin(ay), z2 = -x * Math.Sin(ay) + z1 * Math.Cos(ay);
                double x3 = x2 * Math.Cos(az) - y1 * Math.Sin(az), y3 = x2 * Math.Sin(az) + y1 * Math.Cos(az);
                double persp = 3.2 / (3.2 + z2);
                p[i] = new PointF((float)(cx + x3 * size * persp), (float)(cy + y3 * size * persp));
                depth[i] = z2;
            }
            int[,] edges = { { 0, 1 }, { 0, 2 }, { 0, 4 }, { 1, 3 }, { 1, 5 }, { 2, 3 }, { 2, 6 }, { 3, 7 }, { 4, 5 }, { 4, 6 }, { 5, 7 }, { 6, 7 } };
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Green, 2))
                for (int e = 0; e < 12; e++) g.DrawLine(pen, p[edges[e, 0]], p[edges[e, 1]]);
            int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } };
            string[] letters = { "B", "O", "N", "K", "!", "?" };
            for (int f = 0; f < 6; f++)
            {
                double dz = 0; float fx = 0, fy = 0;
                foreach (int k in faces[f]) { dz += depth[k]; fx += p[k].X; fy += p[k].Y; }
                if (dz / 4 > 0.2) continue;   // only faces pointing at you
                g.DrawString(letters[f], fBanner, B(Hsv(t * 90 + f * 60, 0.8, 1)), new RectangleF(fx / 4 - 40, fy / 4 - 30, 80, 60), sfCenter);
            }
            g.Restore(st);
            g.DrawString(Leet("a spinning cube. it's the law."), fSmall, B(Color.Gray), new RectangleF(cx - 150, cy + 175, 300, 14), sfCenter);
        }

        // ------------------------------------------------------------ effects

        void DrawHammer(Graphics g)
        {
            if (hammerIdx < 0 || hammerIdx >= hammerQueue.Count || screen != 0) return;
            RectangleF r = HomeRect(hammerQueue[hammerIdx]);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            double a = hammerT < 0.55 ? 80 * (1 - Math.Pow(hammerT / 0.55, 2)) : 28 * ((hammerT - 0.55) / 0.45);
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx + 76, cy + 4);
            if (hammerT < 0.55) { DrawHammerShape(g, (float)(a + 22), 50); DrawHammerShape(g, (float)(a + 11), 100); }
            DrawHammerShape(g, (float)a, 255);
            g.Restore(st);
        }

        void DrawHammerShape(Graphics g, float angle, int alpha)
        {
            GraphicsState st = g.Save();
            g.RotateTransform(angle);
            Pen outline = P(Color.FromArgb(alpha, 0, 0, 0));
            g.FillRectangle(B(Color.FromArgb(alpha, 150, 92, 40)), -70, -5, 74, 10);
            g.DrawRectangle(outline, -70, -5, 74, 10);
            g.FillRectangle(B(Color.FromArgb(alpha, 70, 70, 80)), -96, -24, 28, 48);
            g.FillRectangle(B(Color.FromArgb(alpha, 190, 190, 205)), -96, -24, 28, 7);
            g.FillRectangle(B(Color.FromArgb(alpha, 255, 200, 0)), -68, -6, 6, 12);
            g.DrawRectangle(outline, -96, -24, 28, 48);
            g.Restore(st);
        }

        void DrawParticles(Graphics g)
        {
            foreach (Particle p in parts)
                g.DrawString(p.C, fPop, B(Color.FromArgb((int)(255 * Math.Min(1, p.Life * 2)), p.Col)), p.X, p.Y);
        }

        static PointF[] Burst(int n, float r1, float r2)
        {
            PointF[] pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double ang = i * Math.PI * 2 / n;
                float rad = i % 2 == 0 ? r1 : r2;
                pts[i] = new PointF((float)(Math.Cos(ang) * rad * 1.25), (float)(Math.Sin(ang) * rad * 0.8));
            }
            return pts;
        }

        void DrawPops(Graphics g)
        {
            foreach (Pop p in pops)
            {
                float s = p.Age < 0.12f ? 0.4f + p.Age / 0.12f * 0.9f : 1.3f - Math.Min(0.3f, (p.Age - 0.12f) * 1.5f);
                int alpha = Math.Max(0, Math.Min(255, p.Age > 0.55f ? (int)(255 * (0.75f - p.Age) / 0.2f) : 255));
                GraphicsState st = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(p.X, p.Y);
                g.ScaleTransform(s, s);
                g.RotateTransform(-8);
                PointF[] star = Burst(20, 42, 26);
                g.FillPolygon(B(Color.FromArgb(alpha, 255, 220, 0)), star);
                using (Pen pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 3)) g.DrawPolygon(pen, star);
                g.DrawString(p.Text, fPop, B(Color.FromArgb(alpha, 200, 0, 0)), new RectangleF(-40, -12, 80, 24), sfCenter);
                g.Restore(st);
            }
        }

        void DrawScroller(Graphics g)
        {
            int first = Math.Max(0, (int)(-scrollX / charW) - 1);
            for (int i = first; i < scrollText.Length; i++)
            {
                float x = (float)(scrollX + i * charW);
                if (x > W) break;
                if (x < -charW) continue;
                float y = (float)(664 + Math.Sin(t * 4 + x * 0.018) * 10);
                g.DrawString(scrollText[i].ToString(), fScroll, B(Hsv((x * 0.5 + t * 120), 0.85, 1)), x, y);
            }
        }

        void DrawBanner(Graphics g)
        {
            if (bannerT < 0) return;
            float s = bannerT < 0.25 ? (float)(bannerT / 0.25 * 1.25) : (float)(1 + 0.25 * Math.Max(0, 1 - (bannerT - 0.25) * 4));
            int alpha = Math.Max(0, Math.Min(255, bannerT > 1.8 ? (int)(255 * (2.2 - bannerT) / 0.4) : 255));
            g.FillRectangle(B(Color.FromArgb(alpha * 160 / 255, 0, 0, 0)), 0, 350, W, 120);
            GraphicsState st = g.Save();
            g.TranslateTransform(W / 2, 400);
            g.ScaleTransform(s, s);
            SizeF full = g.MeasureString(bannerText, fBanner, PointF.Empty, StringFormat.GenericTypographic);
            float cw = full.Width / Math.Max(1, bannerText.Length);
            for (int i = 0; i < bannerText.Length; i++)
            {
                float x = -full.Width / 2 + i * cw;
                float y = (float)(-full.Height / 2 + Math.Sin(t * 10 + i * 0.7) * 6);
                string ch = bannerText[i].ToString();
                g.DrawString(ch, fBanner, B(Color.FromArgb(alpha, 0, 0, 0)), x + 4, y + 4, StringFormat.GenericTypographic);
                g.DrawString(ch, fBanner, B(Color.FromArgb(alpha, Hsv(t * 240 + i * 32, 0.8, 1))), x, y, StringFormat.GenericTypographic);
            }
            g.Restore(st);
            g.DrawString(bannerSub, fSub, B(Color.FromArgb(alpha, 255, 255, 255)), new RectangleF(0, 440, W, 20), sfCenter);
        }

        // ============================================================ static art

        static readonly string[][] Letters = {
            new[] { "#####.", "##..##", "##..##", "#####.", "##..##", "##..##", "#####." },
            new[] { ".####.", "##..##", "##..##", "##..##", "##..##", "##..##", ".####." },
            new[] { "##..##", "###.##", "######", "##.###", "##..##", "##..##", "##..##" },
            new[] { "##..##", "##.##.", "####..", "###...", "####..", "##.##.", "##..##" }
        };

        Bitmap BuildLogo()
        {
            const int cell = 9, cols = 4 * 6 + 3, rows = 7;
            int w = cols * cell + 8, h = rows * cell + 3 * cell + 8;
            bool[,] on = new bool[cols, rows];
            for (int L = 0; L < 4; L++)
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < 6; x++)
                        if (Letters[L][y][x] == '#') on[L * 7 + x, y] = true;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            Random dr = new Random(7);
            using (Graphics g = Graphics.FromImage(bmp))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(0, 90, 45)))
            using (SolidBrush edge = new SolidBrush(Color.Black))
            using (SolidBrush white = new SolidBrush(Color.FromArgb(236, 244, 236)))
            {
                g.Clear(Color.Transparent);
                for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (on[x, y]) g.FillRectangle(shadow, x * cell + 4, y * cell + 4, cell, cell);
                for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (on[x, y]) g.FillRectangle(edge, x * cell - 2, y * cell - 2, cell + 4, cell + 4);
                for (int x = 0; x < cols; x++)
                {
                    int lowest = -1;
                    for (int y = rows - 1; y >= 0; y--) if (on[x, y]) { lowest = y; break; }
                    if (lowest < rows - 1 || dr.Next(3) == 0) continue;
                    int len = cell + dr.Next(cell * 2);
                    g.FillRectangle(edge, x * cell + 1, rows * cell, cell - 2, len + 2);
                    g.FillRectangle(white, x * cell + 3, rows * cell, cell - 6, len);
                    g.FillEllipse(white, x * cell + 1, rows * cell + len - 4, cell - 2, cell - 2);
                }
                for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (on[x, y]) g.FillRectangle(white, x * cell, y * cell, cell, cell);
            }
            for (int py = 0; py < rows * cell; py++)
            {
                int every = py < cell * 3 ? 0 : py < cell * 5 ? 4 : 2;
                if (every == 0) continue;
                for (int px = 0; px < cols * cell; px++)
                    if (on[px / cell, py / cell] && (px + py) % every == 0) bmp.SetPixel(px, py, Color.FromArgb(120, 150, 125));
            }
            return bmp;
        }

        Bitmap BuildOverlay()
        {
            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen scan = new Pen(Color.FromArgb(55, 0, 0, 0)))
                    for (int y = 0; y < H; y += 3) g.DrawLine(scan, 0, y, W, y);
                using (SolidBrush d1 = new SolidBrush(Color.FromArgb(150, 0, 70, 35)))
                using (SolidBrush d2 = new SolidBrush(Color.FromArgb(80, 0, 70, 35)))
                    for (int y = 0; y < H; y += 2)
                        for (int x = 0; x < 8; x += 2)
                        {
                            SolidBrush b = x < 4 ? d1 : d2;
                            int off = (y / 2) % 2;
                            g.FillRectangle(b, x + off, y, 1, 1);
                            g.FillRectangle(b, W - 1 - x - off, y, 1, 1);
                        }
            }
            return bmp;
        }

        Bitmap BuildBricks()
        {
            Bitmap bmp = new Bitmap(170, 120, PixelFormat.Format32bppPArgb);
            Random br = new Random(3);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(22, 18, 20));
                for (int row = 0; row < 10; row++)
                {
                    int off = row % 2 == 0 ? 0 : 14;
                    for (int x = -off; x < 170; x += 28)
                    {
                        int v = 46 + br.Next(18);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(v + 10, v - 8, v - 10)))
                            g.FillRectangle(b, x + 1, row * 12 + 1, 26, 10);
                    }
                }
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(shade, 0, 0, 170, 120);
                using (SolidBrush win = new SolidBrush(Color.FromArgb(60, 70, 90))) g.FillRectangle(win, 70, 14, 30, 18);
                using (Pen wb = new Pen(Color.FromArgb(20, 20, 24), 2)) { g.DrawLine(wb, 80, 14, 80, 32); g.DrawLine(wb, 90, 14, 90, 32); }
            }
            return bmp;
        }

        // ============================================================ helpers

        public static string Leet(string s)
        {
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                c[i] = "aeiouAEIOU".IndexOf(c[i]) >= 0 ? char.ToLowerInvariant(c[i]) : char.ToUpperInvariant(c[i]);
            return new string(c);
        }

        static string Dots(string key, string value)
        {
            return "=> " + key + " " + new string('.', Math.Max(2, 14 - key.Length)) + " " + value;
        }

        string RamBar()
        {
            int filled = (int)Math.Round(ramLoad / 10.0);
            return "[" + new string((char)0x2588, filled) + new string((char)0x2591, 10 - filled) + "]";
        }

        static string FormatSize(float mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + " GB" : ((int)mb).ToString(Inv) + " MB";
        }

        static string FormatShort(float mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + "G" : ((int)Math.Round(mb)).ToString(Inv) + "M";
        }

        static string FormatCpu(int cpu10) { return (cpu10 / 10.0).ToString(cpu10 >= 100 ? "0" : "0.0", Inv) + "%"; }

        static string FormatIo(int kb) { return kb >= 1024 ? (kb / 1024.0).ToString("0.0", Inv) + "MB/s" : kb + "KB/s"; }

        static string Initials(string label)
        {
            string ini = "";
            foreach (string w in label.Split(new[] { ' ', '/', '(', '-' }, StringSplitOptions.RemoveEmptyEntries))
                if (char.IsLetterOrDigit(w[0])) { ini += char.ToUpperInvariant(w[0]); if (ini.Length == 2) break; }
            return ini.Length == 0 ? "??" : ini;
        }

        static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7FFFFFFF;
            }
        }

        static Color Hsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r = 0, gg = 0, b = 0;
            if (h < 60) { r = c; gg = x; } else if (h < 120) { r = x; gg = c; } else if (h < 180) { gg = c; b = x; }
            else if (h < 240) { gg = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
        }
    }
}
