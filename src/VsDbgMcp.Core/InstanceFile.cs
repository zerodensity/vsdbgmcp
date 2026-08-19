using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VsDbgMcp
{
    /// <summary>
    /// Writes the discovery record.
    ///
    /// Hand-rolled rather than using a JSON library because this code runs inside
    /// devenv.exe, where every dependency competes with Visual Studio's own assembly
    /// versions. The shape is fixed and tiny, so a serializer buys nothing.
    /// </summary>
    public static class InstanceFile
    {
        public static void Write(InstanceRecord record)
        {
            var dir = Names.InstanceDir;
            Directory.CreateDirectory(dir);

            var path = Names.InstanceFile(record.Pid);
            var temp = path + ".tmp";

            File.WriteAllText(temp, Serialize(record), new UTF8Encoding(false));

            // Replace in one step. A reader either sees the old file or the new one,
            // never a half-written one.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static void Remove(int pid)
        {
            // Best effort: a shim reading the directory at this moment can hold the
            // file briefly. One left behind is pruned on the next discovery anyway,
            // because the process it names is gone.
            try
            {
                var path = Names.InstanceFile(pid);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static string Serialize(InstanceRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            Num(sb, "pid", r.Pid); sb.Append(",\n");
            Str(sb, "pipe", r.Pipe); sb.Append(",\n");
            Str(sb, "token", r.Token); sb.Append(",\n");
            Str(sb, "vsVersion", r.VsVersion); sb.Append(",\n");
            Num(sb, "contract", r.Contract); sb.Append(",\n");

            sb.Append("  \"workspace\": ");
            if (r.Workspace == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append("{ ");
                sb.Append("\"kind\": ").Append(Quote(r.Workspace.Kind)).Append(", ");
                sb.Append("\"root\": ").Append(Quote(r.Workspace.Root)).Append(", ");
                sb.Append("\"file\": ").Append(Quote(r.Workspace.File)).Append(", ");
                sb.Append("\"filter\": ").Append(Quote(r.Workspace.Filter)).Append(", ");
                sb.Append("\"name\": ").Append(Quote(r.Workspace.Name));
                sb.Append(" }");
            }
            sb.Append(",\n");

            Arr(sb, "projectDirs", r.ProjectDirs); sb.Append(",\n");
            Arr(sb, "capabilities", r.Capabilities); sb.Append(",\n");
            Str(sb, "debugMode", r.DebugMode); sb.Append(",\n");
            Str(sb, "startedAt", r.StartedAt); sb.Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static void Str(StringBuilder sb, string name, string value) =>
            sb.Append("  \"").Append(name).Append("\": ").Append(Quote(value));

        static void Num(StringBuilder sb, string name, int value) =>
            sb.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));

        static void Arr(StringBuilder sb, string name, IReadOnlyList<string> values)
        {
            sb.Append("  \"").Append(name).Append("\": [");
            if (values != null)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Quote(values[i]));
                }
            }
            sb.Append("]");
        }

        static string Quote(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
