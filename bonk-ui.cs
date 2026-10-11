// Bonk Mode keygen window. Compiled at runtime by bonk-mode.ps1 (C# 5, .NET Framework).
// Everything is drawn with GDI+ on one double-buffered form; music and sound effects are
// synthesized square/triangle/noise chiptune streamed through winmm waveOut. No assets.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BonkMode
{
    // ------------------------------------------------------------------ audio

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
            lock (voices)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    int s = 0;
                    if (MusicOn) { s = music[musicPos] * 3 / 5; if (++musicPos >= music.Length) musicPos = 0; }
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
            if (hwo == IntPtr.Zero || sfx == null) return;
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
                double tri = 1 - 4 * Math.Abs(ph - 0.5);
                // 16-step quantized like the NES triangle channel
                tri = Math.Round(tri * 8) / 8;
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

        static short[] ToPcm(float[] b)
        {
            float peak = 0.0001f;
            foreach (float v in b) { float a = Math.Abs(v); if (a > peak) peak = a; }
            float gain = peak > 0.95f ? 0.95f / peak : 1f;
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
            return ToPcm(b);
        }

        public static short[] Bonk()
        {
            int len = (int)(R * 0.32);
            float[] b = new float[len];
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
            return ToPcm(b);
        }

        public static short[] Click()
        {
            float[] b = new float[(int)(R * 0.035)];
            Square(b, 0, b.Length, 1400, 0.5, 0.12, false);
            short[] o = ToPcm(b);
            for (int i = 0; i < o.Length; i++) o[i] = (short)(o[i] / 4);
            return o;
        }

        public static short[] Parole()
        {
            int[] notes = { 72, 76, 79, 84, 88, 91 };
            int step = (int)(R * 0.055);
            float[] b = new float[step * notes.Length + step * 3];
            for (int i = 0; i < notes.Length; i++) Square(b, i * step, step * (i == notes.Length - 1 ? 4 : 1), Hz(notes[i]), 0.25, 0.2, false);
            short[] o = ToPcm(b);
            for (int i = 0; i < o.Length; i++) o[i] = (short)(o[i] / 2);
            return o;
        }

        public static short[] Slam()
        {
            float[] b = new float[(int)(R * 0.45)];
            Noise(b, 0, b.Length, 0.3, 0.35);
            Square(b, 0, (int)(R * 0.3), 55, 0.5, 0.25, false);
            Kick(b, 0, 0.8);
            short[] o = ToPcm(b);
            for (int i = 0; i < o.Length; i++) o[i] = (short)(o[i] * 2 / 3);
            return o;
        }
    }

    // ------------------------------------------------------------------ window

    public class Tile
    {
        public string Label; public int MB; public bool Enabled; public bool Running; public bool Jailed;
        public RectangleF Rect; public float Shake, Flash, Bars, ShownMB; public bool Hover;
    }

    class Particle { public float X, Y, VX, VY, Life; public string C; public Color Col; }
    class Pop { public float X, Y, Age; public string Text; }
    class Star { public float X, Y, Z; }

    [StructLayout(LayoutKind.Sequential)]
    class MemStat
    {
        public uint Length = 64; public uint Load;
        public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt;
    }

    public class KeygenForm : Form
    {
        public event Action BonkClicked;
        public event Action ParoleClicked;
        public event Action EditClicked;
        public event Action<string, bool> ToggleClicked;

        public static bool NoAudio;

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemStat m);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        const int W = 820, H = 660;
        const int TileX = 20, TileY = 276, TileW = 780, TileH = 276;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color Green = Color.FromArgb(0, 255, 120);
        static readonly Color DimGreen = Color.FromArgb(0, 110, 55);

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly List<Tile> tiles = new List<Tile>();
        readonly List<Particle> parts = new List<Particle>();
        readonly List<Pop> pops = new List<Pop>();
        readonly Star[] stars = new Star[140];
        readonly Random rnd = new Random();
        readonly SolidBrush[] hue = new SolidBrush[36];
        readonly SolidBrush[] grey = new SolidBrush[8];

        ChipAudio audio;
        short[] sfxBonk, sfxClick, sfxParole, sfxSlam;
        Bitmap logo, overlay;
        Font fSmall, fTile, fTileBig, fTileMid, fNfo, fBtn, fScroll, fBanner, fPop, fSub;
        StringFormat sfCenter, sfLabel;

        double t, openT, ramTimer, scrollX;
        DateTime last = DateTime.Now;
        float shake, charW;
        List<Tile> hammerQueue = new List<Tile>();
        int hammerIdx = -1;
        double hammerT;
        bool hammerHit, busy;
        double bannerT = -1;
        string bannerText = "", bannerSub = "";
        object[] pending;
        string status;
        uint ramLoad; double ramFreeGB;
        int jailCount;
        Rectangle btnBonk, btnParole, btnMusic, btnEdit, btnClose, btnMin;
        string hoverBtn;
        string scrollText;
        static readonly string[] PopWords = { "BoNK!", "WHaM!", "SMaSH!", "BoNK!", "K.O.!", "ByE!", "BoNK!", "PoW!" };

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
            fTile = new Font("Consolas", 7.5f);
            fTileBig = new Font("Consolas", 14f, FontStyle.Bold);
            fTileMid = new Font("Consolas", 10f, FontStyle.Bold);
            fNfo = new Font("Consolas", 9f);
            fBtn = new Font("Consolas", 10f, FontStyle.Bold);
            fScroll = new Font("Consolas", 16f, FontStyle.Bold);
            fBanner = new Font("Consolas", 40f, FontStyle.Bold);
            fPop = new Font("Consolas", 11f, FontStyle.Bold);
            fSub = new Font("Consolas", 10f);
            sfCenter = new StringFormat(); sfCenter.Alignment = StringAlignment.Center; sfCenter.LineAlignment = StringAlignment.Center;
            sfLabel = new StringFormat(); sfLabel.Trimming = StringTrimming.EllipsisCharacter;

            for (int i = 0; i < hue.Length; i++) hue[i] = new SolidBrush(Hsv(i * 10, 0.85, 1));
            for (int i = 0; i < grey.Length; i++) { int v = 60 + i * 27; grey[i] = new SolidBrush(Color.FromArgb(v, v, Math.Min(255, v + 20))); }
            for (int i = 0; i < stars.Length; i++) { stars[i] = new Star(); ResetStar(stars[i]); stars[i].Z = (float)rnd.NextDouble(); }

            logo = BuildLogo();
            overlay = BuildOverlay();

            btnBonk = new Rectangle(20, 562, 270, 32);
            btnParole = new Rectangle(300, 562, 170, 32);
            btnMusic = new Rectangle(480, 562, 150, 32);
            btnEdit = new Rectangle(640, 562, 160, 32);
            btnClose = new Rectangle(W - 38, 6, 30, 18);
            btnMin = new Rectangle(W - 72, 6, 30, 18);

            status = Leet("ready. pick your victims.");
            scrollText = "     *** BONK MODE  //  RAM JAiL KEYGEN v1.0 ***   CRACKED BY THE BONK CREW IN 2026 ...   " +
                "CLICK A SQUARE TO SENTENCE OR SPARE IT ...   HIT [BONK 'EM ALL] AND WATCH YOUR BACKGROUND APPS GET SENT TO RAM JAiL ...   " +
                "PAROLE BRINGS THEM HOME WHEN YOU ARE DONE FRAGGING ...   STEAM, YOUR GPU DRIVER AND WINDOWS HAVE DIPLOMATIC IMMUNITY ...   " +
                "GREETZ FLY OUT TO RAZOR 1911, FAIRLIGHT, RELOADED, CODEX, SKIDROW, SND, CPY AND EVERY KEYGEN MUSICIAN WHO MADE A 40KB TRACKER FILE SLAP ...   " +
                "NO GREETZ TO ADOBE CREATIVE CLOUD, YOU KNOW WHAT YOU DID ...   [B] BONK   [P] PAROLE   [M] MUSIC   [ESC] EXIT ...   ";
            using (Graphics g = CreateGraphics())
                charW = g.MeasureString("MMMMMMMMMM", fScroll, PointF.Empty, StringFormat.GenericTypographic).Width / 10f;
            scrollX = W;

            if (!NoAudio) { try { audio = new ChipAudio(); } catch { audio = null; } }
            sfxBonk = Synth.Bonk(); sfxClick = Synth.Click(); sfxParole = Synth.Parole(); sfxSlam = Synth.Slam();
            ReadRam();

            timer.Interval = 30;
            timer.Tick += delegate { Step(); Invalidate(); };
            timer.Start();
        }

        // ---------------------------------------------------------- public API (PowerShell side)

        public bool Busy { get { return busy || hammerIdx >= 0; } }

        public void SetBusy(bool on) { busy = on; }

        public void SetStatus(string s) { status = Leet(s ?? ""); }

        public void SetRows(string[] labels, int[] mb, bool[] enabled, bool[] running, bool[] jailed)
        {
            if (hammerIdx >= 0) { pending = new object[] { labels, mb, enabled, running, jailed }; return; }
            Dictionary<string, Tile> old = new Dictionary<string, Tile>();
            foreach (Tile x in tiles) old[x.Label] = x;
            tiles.Clear();
            jailCount = 0;
            for (int i = 0; i < labels.Length; i++)
            {
                Tile tl;
                if (!old.TryGetValue(labels[i], out tl)) { tl = new Tile(); tl.Label = labels[i]; }
                tl.MB = mb[i]; tl.Enabled = enabled[i]; tl.Running = running[i];
                tl.Jailed = jailed[i] && !running[i];
                if (tl.Jailed) jailCount++;
                tiles.Add(tl);
            }
            LayoutTiles();
        }

        public void PreviewBonk() { StartBonk(false); }

        public void Advance(double dt)
        {
            t += dt; openT += dt;
            foreach (Star s in stars) { s.Z -= (float)(dt * 0.3); if (s.Z <= 0.02f) ResetStar(s); }
            foreach (Tile tl in tiles)
            {
                tl.Shake = Math.Max(0, tl.Shake - (float)dt * 2.5f);
                tl.Flash = Math.Max(0, tl.Flash - (float)dt * 3.5f);
                if (tl.Jailed) tl.Bars = Math.Min(1, tl.Bars + (float)dt * 5);
                else tl.Bars = Math.Max(0, tl.Bars - (float)dt * 2.5f);
                tl.ShownMB += (tl.MB - tl.ShownMB) * (float)Math.Min(1, dt * 5);
            }
            if (hammerIdx >= 0)
            {
                hammerT += dt / 0.26;
                if (!hammerHit && hammerT >= 0.55) { hammerHit = true; Impact(hammerQueue[hammerIdx]); }
                if (hammerT >= 1)
                {
                    hammerIdx++; hammerT = 0; hammerHit = false;
                    if (hammerIdx >= hammerQueue.Count)
                    {
                        hammerIdx = -1; hammerQueue.Clear();
                        bannerText = "RaM JaiLeD!"; bannerT = 0; shake = 1f;
                        Play(sfxSlam);
                        if (busy) status = Leet("waiting for stragglers...");
                        if (pending != null)
                        {
                            object[] p = pending; pending = null;
                            SetRows((string[])p[0], (int[])p[1], (bool[])p[2], (bool[])p[3], (bool[])p[4]);
                        }
                    }
                }
            }
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Particle p = parts[i];
                p.X += p.VX * (float)dt; p.Y += p.VY * (float)dt; p.VY += 700 * (float)dt; p.Life -= (float)dt;
                if (p.Life <= 0) parts.RemoveAt(i);
            }
            for (int i = pops.Count - 1; i >= 0; i--) { pops[i].Age += (float)dt; if (pops[i].Age > 0.75f) pops.RemoveAt(i); }
            if (bannerT >= 0) { bannerT += dt; if (bannerT > 2.2) bannerT = -1; }
            shake = Math.Max(0, shake - (float)dt * 2.5f);
            scrollX -= dt * 140;
            if (scrollX < -scrollText.Length * charW) scrollX = W;
            ramTimer -= dt;
            if (ramTimer <= 0) { ramTimer = 1; ReadRam(); }
        }

        // ---------------------------------------------------------- behavior

        void Step()
        {
            DateTime now = DateTime.Now;
            double dt = (now - last).TotalSeconds;
            last = now;
            if (dt > 0.1) dt = 0.1;
            Advance(dt);
        }

        void Play(short[] s) { if (audio != null) audio.Play(s); }

        void StartBonk(bool raise)
        {
            if (Busy) return;
            List<Tile> q = new List<Tile>();
            float mb = 0;
            foreach (Tile x in tiles) if (x.Enabled && x.Running && !x.Jailed) { q.Add(x); mb += x.MB; }
            if (q.Count == 0) { status = Leet("nothing to bonk. your ram is vibing."); Play(sfxClick); return; }
            hammerQueue = q; hammerIdx = 0; hammerT = 0; hammerHit = false;
            bannerSub = "+" + FormatSize(mb) + " FReeD";
            status = Leet("bonking " + q.Count + " apps...");
            if (raise)
            {
                busy = true;
                if (BonkClicked != null) BonkClicked();
            }
        }

        void StartParole()
        {
            if (Busy) return;
            if (jailCount == 0) { status = Leet("jail is empty. nobody to release."); Play(sfxClick); return; }
            foreach (Tile x in tiles) if (x.Jailed) { x.Jailed = false; x.Flash = 0.6f; }
            jailCount = 0;
            Play(sfxParole);
            bannerText = "PaRoLe!"; bannerSub = Leet("they're free. for now."); bannerT = 0;
            status = Leet("granting parole...");
            busy = true;
            if (ParoleClicked != null) ParoleClicked();
        }

        void Impact(Tile tl)
        {
            tl.Jailed = true; tl.Shake = 1; tl.Flash = 1;
            jailCount++;
            float cx = tl.Rect.X + tl.Rect.Width / 2, cy = tl.Rect.Y + tl.Rect.Height / 2;
            const string bits = "01$#*@%&!";
            for (int i = 0; i < 22; i++)
            {
                Particle p = new Particle();
                p.X = cx; p.Y = cy;
                p.VX = (float)(rnd.NextDouble() * 520 - 260);
                p.VY = (float)(-120 - rnd.NextDouble() * 320);
                p.Life = (float)(0.5 + rnd.NextDouble() * 0.5);
                p.C = bits[rnd.Next(bits.Length)].ToString();
                p.Col = rnd.Next(3) == 0 ? Color.Yellow : Green;
                parts.Add(p);
            }
            Pop pop = new Pop(); pop.X = cx; pop.Y = cy - 6; pop.Text = PopWords[rnd.Next(PopWords.Length)];
            pops.Add(pop);
            shake = Math.Max(shake, 0.55f);
            Play(sfxBonk);
        }

        void LayoutTiles()
        {
            int n = Math.Max(1, tiles.Count), gap = 8, size = 120, cols = 1;
            for (size = 120; size >= 56; size -= 2)
            {
                cols = (TileW + gap) / (size + gap);
                int rows = (n + cols - 1) / cols;
                if (rows * (size + gap) - gap <= TileH) break;
            }
            int used = Math.Min(cols, n);
            int totalW = used * (size + gap) - gap;
            int x0 = TileX + (TileW - totalW) / 2;
            for (int i = 0; i < tiles.Count; i++)
                tiles[i].Rect = new RectangleF(x0 + (i % cols) * (size + gap), TileY + (i / cols) * (size + gap), size, size);
        }

        void ReadRam()
        {
            MemStat m = new MemStat();
            if (GlobalMemoryStatusEx(m)) { ramLoad = m.Load; ramFreeGB = m.Avail / 1073741824.0; }
        }

        void ResetStar(Star s)
        {
            s.X = (float)(rnd.NextDouble() * 2 - 1); s.Y = (float)(rnd.NextDouble() * 2 - 1); s.Z = 1;
        }

        // ---------------------------------------------------------- input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            hoverBtn = HitButton(e.Location);
            bool any = hoverBtn != null;
            foreach (Tile tl in tiles) { tl.Hover = tl.Rect.Contains(e.Location); if (tl.Hover) any = true; }
            Cursor = any ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverBtn = null;
            foreach (Tile tl in tiles) tl.Hover = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (HitButton(e.Location) != null) return;
            foreach (Tile tl in tiles) if (tl.Rect.Contains(e.Location)) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // drag the borderless window
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            string b = HitButton(e.Location);
            if (b == "close") { Close(); return; }
            if (b == "min") { WindowState = FormWindowState.Minimized; return; }
            if (b == "music") { ToggleMusic(); return; }
            if (b == "edit") { Play(sfxClick); if (EditClicked != null) EditClicked(); return; }
            if (b == "bonk") { StartBonk(true); return; }
            if (b == "parole") { StartParole(); return; }
            if (Busy) return;
            foreach (Tile tl in tiles)
            {
                if (!tl.Rect.Contains(e.Location) || tl.Jailed) continue;
                tl.Enabled = !tl.Enabled;
                tl.Shake = 0.25f;
                Play(sfxClick);
                status = Leet((tl.Enabled ? "sentenced: " : "spared: ") + tl.Label);
                if (ToggleClicked != null) ToggleClicked(tl.Label, tl.Enabled);
                return;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.M) ToggleMusic();
            else if (e.KeyCode == Keys.B) StartBonk(true);
            else if (e.KeyCode == Keys.P) StartParole();
        }

        void ToggleMusic()
        {
            if (audio != null) audio.MusicOn = !audio.MusicOn;
            Play(sfxClick);
        }

        string HitButton(Point p)
        {
            if (btnClose.Contains(p)) return "close";
            if (btnMin.Contains(p)) return "min";
            if (btnBonk.Contains(p)) return "bonk";
            if (btnParole.Contains(p)) return "parole";
            if (btnMusic.Contains(p)) return "music";
            if (btnEdit.Contains(p)) return "edit";
            return null;
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
                if (logo != null) logo.Dispose();
                if (overlay != null) overlay.Dispose();
                foreach (Font f in new[] { fSmall, fTile, fTileBig, fTileMid, fNfo, fBtn, fScroll, fBanner, fPop, fSub }) if (f != null) f.Dispose();
                foreach (SolidBrush br in hue) br.Dispose();
                foreach (SolidBrush br in grey) br.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (shake > 0) g.TranslateTransform((float)((rnd.NextDouble() * 2 - 1) * shake * 8), (float)((rnd.NextDouble() * 2 - 1) * shake * 6));

            DrawStars(g);
            DrawCopper(g);
            DrawLogo(g);
            DrawSubtitle(g);
            DrawNfo(g);
            DrawTiles(g);
            DrawHammer(g);
            DrawParticles(g);
            DrawPops(g);
            DrawButtons(g);
            DrawScroller(g);
            DrawBanner(g);

            g.ResetTransform();
            g.DrawImageUnscaled(overlay, 0, 0);
            using (Pen p = new Pen(DimGreen)) g.DrawRectangle(p, 0, 0, W - 1, H - 1);
            DrawSmallButton(g, btnMin, "_", hoverBtn == "min");
            DrawSmallButton(g, btnClose, "X", hoverBtn == "close");
        }

        void DrawStars(Graphics g)
        {
            foreach (Star s in stars)
            {
                float px = W / 2 + s.X / s.Z * W / 2, py = H / 2 + s.Y / s.Z * H / 2;
                if (px < 0 || px >= W || py < 0 || py >= H) { ResetStar(s); continue; }
                float k = 1 - s.Z;
                int sz = 1 + (int)(k * 3);
                g.FillRectangle(grey[Math.Min(7, (int)(k * 8))], px, py, sz, sz);
            }
        }

        void DrawCopper(Graphics g)
        {
            for (int i = 0; i < 3; i++)
            {
                float y = (float)(78 + Math.Sin(t * 1.7 + i * 1.3) * 46);
                Color c = Hsv(t * 50 + i * 120, 0.9, 0.9);
                RectangleF r = new RectangleF(0, y, W, 18);
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
            int lx = (W - logo.Width) / 2, ly = 14;
            for (int x = 0; x < logo.Width; x += 3)
            {
                int dy = (int)(Math.Sin(t * 3.2 + x * 0.035) * 5);
                g.DrawImage(logo, new Rectangle(lx + x, ly + dy, 3, logo.Height), x, 0, 3, logo.Height, GraphicsUnit.Pixel);
            }
        }

        void DrawSubtitle(Graphics g)
        {
            string sub = "- = r a m   j a i l   k e y g e n = -";
            SizeF sz = g.MeasureString(sub, fSub);
            float x = (W - sz.Width) / 2, y = 132;
            using (Pen p = new Pen(Green, 2))
            {
                g.DrawLine(p, 60, y + 9, x - 10, y + 9);
                g.DrawLine(p, x + sz.Width + 10, y + 9, W - 60, y + 9);
            }
            using (SolidBrush br = new SolidBrush(Green)) g.DrawString(sub, fSub, br, x, y);
            g.DrawString("[ BoNK CReW 2026 ]  ::  CRaCK #001  ::  0 BYTeS oF aDoBe aLLoWeD", fSmall, grey[3], new RectangleF(0, 146, W, 14), sfCenter);
        }

        void DrawNfo(Graphics g)
        {
            Rectangle box = new Rectangle(20, 170, W - 40, 98);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(bg, box);
            using (Pen p = new Pen(DimGreen)) { g.DrawRectangle(p, box); g.DrawRectangle(p, box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6); }
            string title = " :: ReLeaSe iNFo :: ";
            SizeF ts = g.MeasureString(title, fNfo);
            g.FillRectangle(Brushes.Black, (W - ts.Width) / 2, box.Y - 7, ts.Width, 14);
            using (SolidBrush br = new SolidBrush(Green)) g.DrawString(title, fNfo, br, (W - ts.Width) / 2, box.Y - 8);

            int targets = 0; float targetMb = 0;
            foreach (Tile tl in tiles) if (tl.Enabled && tl.Running && !tl.Jailed) { targets++; targetMb += tl.MB; }
            int filled = (int)Math.Round(ramLoad / 10.0);
            string bar = "[" + new string((char)0x2588, filled) + new string((char)0x2591, 10 - filled) + "]";
            string[] left = {
                Dots("CRaCKeR", "BoNK CReW"),
                Dots("TaRGeTS", targets + " aPPS"),
                Dots("RaM 2 FRee", FormatSize(targetMb)),
                Dots("iN JaiL", jailCount.ToString(Inv))
            };
            string[] right = {
                Dots("SYSTeM RaM", bar + " " + ramLoad + "%"),
                Dots("FRee RaM", ramFreeGB.ToString("0.0", Inv) + " GB"),
                Dots("iMMuNe", "STeaM GPU WiNDoWS"),
                Dots("DaTe", DateTime.Now.ToString("MM/dd/yy", Inv))
            };
            int budget = (int)((openT - 0.15) * 160);
            using (SolidBrush keyBr = new SolidBrush(Color.FromArgb(210, 230, 210)))
            {
                for (int i = 0; i < 4; i++)
                {
                    budget = TypeLine(g, left[i], keyBr, 34, box.Y + 12 + i * 17, budget);
                    budget = TypeLine(g, right[i], keyBr, 420, box.Y + 12 + i * 17, budget);
                }
            }
            string cursor = ((int)(t * 3) % 2 == 0) ? "_" : " ";
            using (SolidBrush st = new SolidBrush(Color.Yellow))
                TypeLine(g, "=> STaTuS: " + status + cursor, st, 34, box.Y + 78, budget);
        }

        int TypeLine(Graphics g, string s, Brush b, float x, float y, int budget)
        {
            if (budget <= 0) return budget;
            string shown = s.Length <= budget ? s : s.Substring(0, budget);
            g.DrawString(shown, fNfo, b, x, y);
            return budget - s.Length;
        }

        void DrawTiles(Graphics g)
        {
            if (tiles.Count == 0)
            {
                using (SolidBrush br = new SolidBrush(Green))
                    g.DrawString(Leet("no background apps from your list are running. clean machine. respect."), fNfo, br, new RectangleF(TileX, TileY, TileW, TileH), sfCenter);
                return;
            }
            float pulse = (float)(0.6 + 0.4 * Math.Sin(t * 6));
            foreach (Tile tl in tiles)
            {
                RectangleF r = tl.Rect;
                if (tl.Shake > 0) r.Offset((float)(Math.Sin(t * 90) * tl.Shake * 6), (float)(Math.Cos(t * 70) * tl.Shake * 3));
                bool small = r.Width < 84;

                Color edge = tl.Jailed ? Color.FromArgb(200, 50, 50)
                    : tl.Enabled ? Color.FromArgb((int)(255 * pulse), Green)
                    : Color.FromArgb(90, 90, 100);
                if (tl.Hover && !tl.Jailed) edge = Color.White;
                using (SolidBrush bg = new SolidBrush(tl.Hover ? Color.FromArgb(235, 18, 40, 26) : Color.FromArgb(225, 6, 12, 8))) g.FillRectangle(bg, r);
                using (Pen p = new Pen(edge)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                using (SolidBrush cb = new SolidBrush(edge))
                {
                    g.FillRectangle(cb, r.X, r.Y, 4, 4); g.FillRectangle(cb, r.Right - 4, r.Y, 4, 4);
                    g.FillRectangle(cb, r.X, r.Bottom - 4, 4, 4); g.FillRectangle(cb, r.Right - 4, r.Bottom - 4, 4, 4);
                }

                string check = tl.Jailed ? "[#]" : tl.Enabled ? "[X]" : "[ ]";
                using (SolidBrush cbr = new SolidBrush(tl.Enabled || tl.Jailed ? Green : Color.Gray)) g.DrawString(check, fTile, cbr, r.X + 4, r.Y + 5);

                g.DrawString(tl.Label, fTile, Brushes.White, new RectangleF(r.X + 4, r.Y + 19, r.Width - 8, small ? 12 : 26), sfLabel);

                Color mbc = tl.MB >= 1024 ? Color.FromArgb(255, 70, 70) : tl.MB >= 300 ? Color.Yellow : Green;
                if (!tl.Jailed)
                using (SolidBrush mb = new SolidBrush(mbc))
                    g.DrawString(FormatShort(tl.ShownMB), small ? fTileMid : fTileBig, mb, new RectangleF(r.X, r.Y + r.Height * 0.42f, r.Width, r.Height * 0.3f), sfCenter);

                string foot; Color fc;
                if (tl.Jailed) { foot = "JaiLeD"; fc = Color.FromArgb(255, 80, 80); }
                else if (tl.Enabled) { foot = (int)(t * 4) % 2 == 0 ? "> BoNK <" : "  BoNK  "; fc = Color.Yellow; }
                else { foot = "SPaReD"; fc = Color.Gray; }
                using (SolidBrush fb = new SolidBrush(fc)) g.DrawString(foot, fTile, fb, new RectangleF(r.X, r.Bottom - 17, r.Width, 14), sfCenter);

                if (tl.Bars > 0)
                {
                    float bh = r.Height * tl.Bars;
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb((int)(130 * tl.Bars), 0, 0, 0))) g.FillRectangle(dim, r.X, r.Y, r.Width, bh);
                    using (SolidBrush bar = new SolidBrush(Color.FromArgb(170, 170, 185)))
                    using (SolidBrush shine = new SolidBrush(Color.FromArgb(235, 235, 245)))
                    {
                        for (int i = 1; i <= 4; i++)
                        {
                            float bx = r.X + r.Width * i / 5f - 2;
                            g.FillRectangle(bar, bx, r.Y, 4, bh);
                            g.FillRectangle(shine, bx, r.Y, 1, bh);
                        }
                        g.FillRectangle(bar, r.X, r.Y + 2, r.Width, 4);
                        if (tl.Bars >= 1) g.FillRectangle(bar, r.X, r.Bottom - 22, r.Width, 3);
                    }
                }
                if (tl.Flash > 0)
                    using (SolidBrush fl = new SolidBrush(Color.FromArgb((int)(220 * tl.Flash), 255, 255, 255))) g.FillRectangle(fl, r);
            }
        }

        void DrawHammer(Graphics g)
        {
            if (hammerIdx < 0 || hammerIdx >= hammerQueue.Count) return;
            RectangleF r = hammerQueue[hammerIdx].Rect;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            double a;
            if (hammerT < 0.55) { double k = hammerT / 0.55; a = 80 * (1 - k * k); }
            else a = 28 * ((hammerT - 0.55) / 0.45);
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(cx + 76, cy + 4);
            if (hammerT < 0.55)
            {
                DrawHammerShape(g, (float)(a + 22), 50);
                DrawHammerShape(g, (float)(a + 11), 100);
            }
            DrawHammerShape(g, (float)a, 255);
            g.Restore(st);
        }

        void DrawHammerShape(Graphics g, float angle, int alpha)
        {
            GraphicsState st = g.Save();
            g.RotateTransform(angle);
            using (SolidBrush wood = new SolidBrush(Color.FromArgb(alpha, 150, 92, 40)))
            using (SolidBrush head = new SolidBrush(Color.FromArgb(alpha, 70, 70, 80)))
            using (SolidBrush shine = new SolidBrush(Color.FromArgb(alpha, 190, 190, 205)))
            using (SolidBrush band = new SolidBrush(Color.FromArgb(alpha, 255, 200, 0)))
            using (Pen outline = new Pen(Color.FromArgb(alpha, 0, 0, 0), 2))
            {
                g.FillRectangle(wood, -70, -5, 74, 10);
                g.DrawRectangle(outline, -70, -5, 74, 10);
                g.FillRectangle(head, -96, -24, 28, 48);
                g.FillRectangle(shine, -96, -24, 28, 7);
                g.FillRectangle(band, -68, -6, 6, 12);
                g.DrawRectangle(outline, -96, -24, 28, 48);
            }
            g.Restore(st);
        }

        void DrawParticles(Graphics g)
        {
            foreach (Particle p in parts)
                using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, p.Life * 2)), p.Col)))
                    g.DrawString(p.C, fPop, br, p.X, p.Y);
        }

        void DrawPops(Graphics g)
        {
            foreach (Pop p in pops)
            {
                float s = p.Age < 0.12f ? 0.4f + p.Age / 0.12f * 0.9f : 1.3f - Math.Min(0.3f, (p.Age - 0.12f) * 1.5f);
                int alpha = p.Age > 0.55f ? (int)(255 * (0.75f - p.Age) / 0.2f) : 255;
                alpha = Math.Max(0, Math.Min(255, alpha));
                GraphicsState st = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(p.X, p.Y);
                g.ScaleTransform(s, s);
                g.RotateTransform(-8);
                PointF[] star = new PointF[20];
                for (int i = 0; i < star.Length; i++)
                {
                    double ang = i * Math.PI * 2 / star.Length;
                    float rad = i % 2 == 0 ? 42 : 26;
                    star[i] = new PointF((float)(Math.Cos(ang) * rad * 1.25), (float)(Math.Sin(ang) * rad));
                }
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(alpha, 255, 220, 0)))
                using (Pen pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 3))
                using (SolidBrush txt = new SolidBrush(Color.FromArgb(alpha, 200, 0, 0)))
                {
                    g.FillPolygon(fill, star);
                    g.DrawPolygon(pen, star);
                    g.DrawString(p.Text, fPop, txt, new RectangleF(-40, -12, 80, 24), sfCenter);
                }
                g.Restore(st);
            }
        }

        void DrawButtons(Graphics g)
        {
            int targets = 0;
            foreach (Tile tl in tiles) if (tl.Enabled && tl.Running && !tl.Jailed) targets++;
            string bonkText = Busy ? "[ BoNKiNG... ]" : "[ BoNK 'eM aLL (" + targets + ") ]";
            bool hot = targets > 0 && !Busy;
            Color bonkBg = hot ? ((int)(t * 4) % 2 == 0 ? Color.FromArgb(150, 0, 0) : Color.FromArgb(210, 40, 0)) : Color.FromArgb(40, 40, 40);
            DrawButton(g, btnBonk, bonkText, hoverBtn == "bonk", bonkBg, Color.Yellow);
            DrawButton(g, btnParole, "[ PaRoLe (" + jailCount + ") ]", hoverBtn == "parole", Color.FromArgb(0, 40, 20), Green);
            bool musicOn = audio != null && audio.MusicOn;
            string note = ((char)0x266A).ToString();
            DrawButton(g, btnMusic, musicOn ? "[ " + note + " MuSiC oN ]" : "[ " + note + " MuSiC oFF ]", hoverBtn == "music", Color.FromArgb(0, 40, 20), Green);
            DrawButton(g, btnEdit, "[ eDiT LiST ]", hoverBtn == "edit", Color.FromArgb(0, 40, 20), Green);
        }

        void DrawButton(Graphics g, Rectangle r, string text, bool hover, Color bg, Color fg)
        {
            using (SolidBrush b = new SolidBrush(hover ? fg : bg)) g.FillRectangle(b, r);
            using (Pen p = new Pen(fg)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (SolidBrush f = new SolidBrush(hover ? Color.Black : fg)) g.DrawString(text, fBtn, f, r, sfCenter);
        }

        void DrawSmallButton(Graphics g, Rectangle r, string text, bool hover)
        {
            using (SolidBrush b = new SolidBrush(hover ? Green : Color.Black)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Green)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (SolidBrush f = new SolidBrush(hover ? Color.Black : Green)) g.DrawString(text, fSmall, f, r, sfCenter);
        }

        void DrawScroller(Graphics g)
        {
            int first = Math.Max(0, (int)(-scrollX / charW) - 1);
            for (int i = first; i < scrollText.Length; i++)
            {
                float x = (float)(scrollX + i * charW);
                if (x > W) break;
                if (x < -charW) continue;
                float y = (float)(612 + Math.Sin(t * 4 + x * 0.018) * 10);
                int h = (int)(((x * 0.5 + t * 120) % 360 + 360) % 360 / 10) % 36;
                g.DrawString(scrollText[i].ToString(), fScroll, hue[h], x, y);
            }
        }

        void DrawBanner(Graphics g)
        {
            if (bannerT < 0) return;
            float s = bannerT < 0.25 ? (float)(bannerT / 0.25 * 1.25) : (float)(1 + 0.25 * Math.Max(0, 1 - (bannerT - 0.25) * 4));
            int alpha = bannerT > 1.8 ? (int)(255 * (2.2 - bannerT) / 0.4) : 255;
            alpha = Math.Max(0, Math.Min(255, alpha));
            using (SolidBrush band = new SolidBrush(Color.FromArgb(alpha * 160 / 255, 0, 0, 0))) g.FillRectangle(band, 0, 340, W, 120);
            GraphicsState st = g.Save();
            g.TranslateTransform(W / 2, 390);
            g.ScaleTransform(s, s);
            SizeF full = g.MeasureString(bannerText, fBanner, PointF.Empty, StringFormat.GenericTypographic);
            float cw = full.Width / Math.Max(1, bannerText.Length);
            for (int i = 0; i < bannerText.Length; i++)
            {
                float x = -full.Width / 2 + i * cw;
                float y = (float)(-full.Height / 2 + Math.Sin(t * 10 + i * 0.7) * 6);
                string ch = bannerText[i].ToString();
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0))) g.DrawString(ch, fBanner, sh, x + 4, y + 4, StringFormat.GenericTypographic);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(alpha, Hsv(t * 240 + i * 32, 0.8, 1)))) g.DrawString(ch, fBanner, br, x, y, StringFormat.GenericTypographic);
            }
            g.Restore(st);
            using (SolidBrush sub = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                g.DrawString(bannerSub, fSub, sub, new RectangleF(0, 428, W, 20), sfCenter);
        }

        // ---------------------------------------------------------- static art

        static readonly string[][] Letters = {
            new[] { "#####.", "##..##", "##..##", "#####.", "##..##", "##..##", "#####." },   // B
            new[] { ".####.", "##..##", "##..##", "##..##", "##..##", "##..##", ".####." },   // O
            new[] { "##..##", "###.##", "######", "##.###", "##..##", "##..##", "##..##" },   // N
            new[] { "##..##", "##.##.", "####..", "###...", "####..", "##.##.", "##..##" }    // K
        };

        Bitmap BuildLogo()
        {
            const int cell = 11, cols = 4 * 6 + 3, rows = 7;
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
                for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (on[x, y]) g.FillRectangle(shadow, x * cell + 5, y * cell + 5, cell, cell);
                for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (on[x, y]) g.FillRectangle(edge, x * cell - 2, y * cell - 2, cell + 4, cell + 4);
                // drips hanging off the bottom of each letter column
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
            // ASCII-style dither shading: denser toward the bottom of the letters
            for (int py = 0; py < rows * cell; py++)
            {
                int every = py < cell * 3 ? 0 : py < cell * 5 ? 4 : 2;
                if (every == 0) continue;
                for (int px = 0; px < cols * cell; px++)
                {
                    if (!on[px / cell, py / cell]) continue;
                    if ((px + py) % every == 0) bmp.SetPixel(px, py, Color.FromArgb(120, 150, 125));
                }
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

        // ---------------------------------------------------------- helpers

        public static string Leet(string s)
        {
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                c[i] = "aeiouAEIOU".IndexOf(c[i]) >= 0 ? char.ToLowerInvariant(c[i]) : char.ToUpperInvariant(c[i]);
            return new string(c);
        }

        static string Dots(string key, string value)
        {
            int n = Math.Max(2, 16 - key.Length);
            return "=> " + key + " " + new string('.', n) + " " + value;
        }

        static string FormatSize(float mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + " GB" : ((int)mb).ToString(Inv) + " MB";
        }

        static string FormatShort(float mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + "G" : ((int)Math.Round(mb)).ToString(Inv) + "M";
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
