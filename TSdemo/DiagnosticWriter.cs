using System;
using System.Windows.Forms;

namespace TSdemo
{
    /// <summary>
    /// Simple, thread-safe helper to write diagnostic text into a Windows Forms TextBox.
    /// Keeps sensitive parts of a connection string masked when using AppendConnectionString.
    /// </summary>
    internal sealed class DiagnosticWriter : IDisposable
    {
        private readonly TextBox _box;

        public DiagnosticWriter(TextBox box)
        {
            _box = box ?? throw new ArgumentNullException(nameof(box));
        }

        public void Write(string text) => Append(text);

        public void WriteLine(string text) => Append(text + Environment.NewLine);

        public void Clear()
        {
            if (_box.IsDisposed) return;
            if (_box.InvokeRequired)
                _box.BeginInvoke(new Action(() => _box.Clear()));
            else
                _box.Clear();
        }

        public void AppendConnectionString(string conn)
        {
            var masked = MaskConnectionString(conn);
            WriteLine($"ConnectionString: {masked}");
        }

        private void Append(string text)
        {
            if (_box.IsDisposed) return;
            if (_box.InvokeRequired)
            {
                _box.BeginInvoke(new Action(() => _box.AppendText(text)));
            }
            else
            {
                _box.AppendText(text);
            }
        }

        private static string MaskConnectionString(string conn)
        {
            if (string.IsNullOrWhiteSpace(conn)) return "(empty)";

            try
            {
                var parts = conn.Split(';');
                for (var i = 0; i < parts.Length; i++)
                {
                    var kv = parts[i];
                    var idx = kv.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = kv.Substring(0, idx).Trim().ToLowerInvariant();
                    if (key.Contains("password") || key.Contains("pwd") || key.Contains("user id") || key.Contains("uid"))
                        parts[i] = kv.Substring(0, idx + 1) + "****";
                }
                return string.Join(";", parts);
            }
            catch
            {
                return "(masked)";
            }
        }

        public void Dispose()
        {
            // No unmanaged resources; placeholder in case future cleanup is needed.
        }
    }
}