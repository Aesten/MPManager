using System;
using System.IO;
using System.Text;

namespace MPManager;

// MPManager's own copy of the server log (MPM_LOG_FILE, set by run-server.sh to ~/bannerlord/mpmanager.log),
// for mpm console and mpm logs. Unlike screen's log of the console it is written as soon as a line is printed,
// has dates, no blank lines, and no lobby keepalive noise (see LogFilter).
//
// It receives every managed Debug.Print (through LogFilter) and everything written to Console.Out. Lines the
// engine prints from native code only appear in screen's log (server.log).
internal static class ServerLog
{
    private static readonly object Lock = new object();
    private static StreamWriter _writer;

    public static void Open()
    {
        string path = Environment.GetEnvironmentVariable("MPM_LOG_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetOut(new ConsoleTee(Console.Out));
        }
        catch (Exception e)
        {
            _writer = null;
            MPManagerSubModule.Log("Could not open " + path + ": " + e.Message);
        }
    }

    public static void Write(string message)
    {
        if (_writer == null || message == null)
        {
            return;
        }
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        lock (Lock)
        {
            foreach (string raw in message.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length > 0)
                {
                    _writer.WriteLine(stamp + " " + line);
                }
            }
        }
    }

    // Passes Console output through unchanged and copies each completed line into the log.
    private sealed class ConsoleTee : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _line = new StringBuilder();

        public ConsoleTee(TextWriter inner)
        {
            _inner = inner;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            lock (_line)
            {
                if (value == '\n')
                {
                    ServerLog.Write(_line.ToString());
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string value)
        {
            if (value == null)
            {
                return;
            }
            _inner.Write(value);
            lock (_line)
            {
                foreach (char c in value)
                {
                    if (c == '\n')
                    {
                        ServerLog.Write(_line.ToString());
                        _line.Clear();
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }
        }

        public override void Flush() => _inner.Flush();
    }
}
