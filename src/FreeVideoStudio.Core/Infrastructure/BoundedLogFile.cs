using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace FreeVideoStudio.Core.Infrastructure
{
    public sealed class BoundedLogFile : IDisposable
    {
        public const int MaxBytes = 5000000;
        private const int CompactionReserve = 256 * 1024;
        private readonly string path;
        private readonly Mutex gate;
        public BoundedLogFile(string filePath)
        {
            path = Path.GetFullPath(filePath);
            using (var hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
                gate = new Mutex(false, "FreeVideoStudio.Log." + key);
            }
        }
        public void Dispose() { gate.Dispose(); }
        public static void Append(string path, string text)
        {
            using (var log = new BoundedLogFile(path)) log.Write(text);
        }
        public static void Replace(string path, string text)
        {
            using (var log = new BoundedLogFile(path)) log.Edit(text, true, false, false);
        }
        public static void WriteLines(string path, IEnumerable<string> lines)
        {
            using (var log = new BoundedLogFile(path))
            {
                log.Edit(string.Empty, true, false, false);
                var batch = new StringBuilder();
                foreach (string line in lines)
                {
                    batch.AppendLine(line);
                    if (batch.Length < 65536) continue;
                    log.Write(batch.ToString());
                    batch.Clear();
                }
                if (batch.Length != 0) log.Write(batch.ToString());
            }
        }
        public static void Prepend(string path, string text)
        {
            using (var log = new BoundedLogFile(path)) log.Edit(text, false, true, false);
        }
        public void Write(string text) { Edit(text, false, false, false); }
        public void WriteDurable(string text) { Edit(text, false, false, true); }
        private static int StartOfCharacter(byte[] bytes, int offset)
        {
            while (offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80) offset++;
            return offset;
        }
        private void Edit(string text, bool replace, bool prepend, bool durable)
        {
            if (text.Length > MaxBytes) text = text.Substring(text.Length - MaxBytes);
            byte[] incoming = Encoding.UTF8.GetBytes(text);
            int start = StartOfCharacter(incoming, Math.Max(0, incoming.Length - MaxBytes));
            int count = incoming.Length - start;
            try { gate.WaitOne(); }
            catch (AbandonedMutexException)
            {
#if NET
                CoreLogger.Warn("Log", "Recovered the log writer lock after another process exited.");
#else
                Console.Error.WriteLine("Recovered the log writer lock after another process exited.");
#endif
            }
            try
            {
                using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    int room = MaxBytes - count;
                    if (replace)
                        file.SetLength(0);
                    else if (prepend || file.Length > room)
                    {
                        int keep = (int)Math.Min(file.Length, room);
                        if (file.Length > room && !prepend && count != 0)
                            keep = Math.Min(keep, Math.Max(0, room - CompactionReserve));
                        file.Position = file.Length - keep;
                        byte[] tail = new byte[keep];
                        for (int at = 0; at < keep;)
                        {
                            int read = file.Read(tail, at, keep - at);
                            if (read == 0) throw new EndOfStreamException();
                            at += read;
                        }
                        int first = StartOfCharacter(tail, 0);
                        file.Position = 0;
                        if (prepend) file.Write(incoming, start, count);
                        file.Write(tail, first, keep - first);
                        file.SetLength(file.Position);
                    }
                    if (!prepend)
                    {
                        file.Position = file.Length;
                        file.Write(incoming, start, count);
                    }
                    if (durable) file.Flush(true);
                }
            }
            finally { gate.ReleaseMutex(); }
        }
    }
}
